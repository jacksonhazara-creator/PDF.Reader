using PdfReader.Core.Bookmarks;
using PdfReader.Rendering;

namespace PdfReader.Tests;

public class MarkupTests
{
    private static uint Pixel(PixelBuffer px, int x, int y) => px.Pixels[y * px.Width + x];
    private static bool IsRed(uint p) => ((p >> 16) & 0xFF) > 180 && ((p >> 8) & 0xFF) < 100 && (p & 0xFF) < 100;
    private static int CountRed(PixelBuffer px, int x0, int y0, int x1, int y1)
    {
        var n = 0;
        for (var y = y0; y < y1; y++) for (var x = x0; x < x1; x++) if (IsRed(Pixel(px, x, y))) n++;
        return n;
    }

    [Theory]
    [InlineData(TextMarkupKind.Underline)]
    [InlineData(TextMarkupKind.StrikeOut)]
    [InlineData(TextMarkupKind.Squiggly)]
    public async Task Text_markup_draws_lines(TextMarkupKind kind)
    {
        await using var doc = await PdfDocument.OpenAsync(new MemoryStream(TestPdf.Create("Underline and strike")));
        var sel = await doc.GetSelectionAsync(0, 0, 8);
        await doc.AddTextMarkupAsync(0, kind, sel.Rects, 0xFFFF0000);
        using var px = await doc.RenderPageAsync(0, 1, PageColors.Normal);
        var r = sel.Rects[0];
        Assert.True(CountRed(px, (int)r.X, (int)r.Y - 2, (int)r.Right, (int)r.Bottom + 3) > 20, $"{kind} not drawn");
    }

    [Theory]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.Ellipse)]
    public async Task Shapes_draw_outline(ShapeKind kind)
    {
        await using var doc = await PdfDocument.OpenAsync(new MemoryStream(TestPdf.Create("x")));
        await doc.AddShapeAsync(0, kind, new PdfRect(200, 300, 100, 60), 0xFFFF0000, 3);
        using var px = await doc.RenderPageAsync(0, 1, PageColors.Normal);
        Assert.True(CountRed(px, 195, 295, 305, 365) > 100);
        Assert.False(IsRed(Pixel(px, 250, 330))); // not filled
    }

    [Fact]
    public async Task Stamp_with_text_and_check_mark()
    {
        await using var doc = await PdfDocument.OpenAsync(new MemoryStream(TestPdf.Create("x")));
        var width = await doc.MeasureTextAsync("APPROVED", 20, "Helvetica-Bold");
        Assert.InRange(width, 60, 140);
        await doc.AddStampAsync(0,
        [
            new StampPath([new(100, 400), new(100 + width + 20, 400), new(100 + width + 20, 440), new(100, 440)], true, 0xFFFF0000, 3),
            new StampText("APPROVED", new PdfPoint(110, 427), 20, 0xFFFF0000, "Helvetica-Bold"),
            new StampPath([new(300, 420), new(308, 430), new(325, 405)], false, 0xFFFF0000, 3),
        ], "Stamp", "Approved");
        using var px = await doc.RenderPageAsync(0, 1, PageColors.Normal);
        Assert.True(CountRed(px, 95, 395, (int)(125 + width), 445) > 300, "stamp missing");
        Assert.True(CountRed(px, 298, 400, 328, 432) > 20, "check mark missing");
        var comment = Assert.Single(await doc.GetCommentsAsync(0));
        Assert.Equal("Stamp", comment.Kind);
        Assert.Equal("Approved", comment.Text);
    }

    [Fact]
    public async Task Insert_and_attachment_are_listed_as_comments_and_survive_save()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfreader-markup-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, TestPdf.Create("Insert here"));
        try
        {
            await using (var doc = await PdfDocument.OpenAsync(path))
            {
                await doc.AddInsertAsync(0, new PdfRect(150, 90, 10, 10), "new words", 0xFF2F6FEB);
                await doc.AddFileAttachmentAsync(0, new PdfRect(400, 90, 16, 20), "notes.txt", "hello"u8.ToArray(), 0xFF2F6FEB);
                await doc.AddTextMarkupAsync(0, TextMarkupKind.StrikeOut, (await doc.GetSelectionAsync(0, 0, 5)).Rects, 0xFFFF0000,
                    contents: "Add", tag: "Replace", quote: "Insert");
                await doc.SaveAsync(path);
            }
            await using var reopened = await PdfDocument.OpenAsync(path);
            var comments = await reopened.GetCommentsAsync(0);
            Assert.Contains(comments, c => c.Kind == "Insert" && c.Text == "new words");
            Assert.Contains(comments, c => c.Kind == "Attachment" && c.Text == "notes.txt");
            Assert.Contains(comments, c => c.Kind == "Replace" && c.Text == "Add" && c.Quote == "Insert");
            Assert.True(reopened.Security.HasAttachments || (await reopened.ScanActiveContentAsync()).HasAttachments);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Bookmarks_can_be_removed_and_added_on_save()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfreader-outline-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, TestPdf.Create(new TestPdf.Options { Pages = ["a", "b", "c"], Outline = true }));
        try
        {
            await using (var doc = await PdfDocument.OpenAsync(path))
            {
                await doc.SaveAsync(path, temp => OutlineEditor.Apply(temp,
                [
                    new OutlineEdit.Remove([1]),
                    new OutlineEdit.Add("My bookmark", 2),
                ]));
                // Reopened in place: the new outline is visible right away.
                var outline = await doc.GetOutlineAsync();
                Assert.Equal(["Chapter 1", "Chapter 3", "My bookmark"], outline.Select(o => o.Title));
                Assert.Equal([0, 2, 2], outline.Select(o => o.PageIndex));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
