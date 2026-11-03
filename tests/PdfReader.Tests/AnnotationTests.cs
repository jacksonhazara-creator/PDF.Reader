using PdfReader.Rendering;

namespace PdfReader.Tests;

public class AnnotationTests
{
    private static string TempPdf(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfreader-annot-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static uint Pixel(PixelBuffer px, int x, int y) => px.Pixels[y * px.Width + x];

    [Fact]
    public async Task Highlight_is_rendered_and_survives_save()
    {
        var path = TempPdf(TestPdf.Create("Hello highlight"));
        try
        {
            await using (var doc = await PdfDocument.OpenAsync(path))
            {
                var sel = await doc.GetSelectionAsync(0, 0, 4);
                Assert.False(doc.IsModified);
                await doc.AddHighlightAsync(0, sel.Rects, 0x80FFFF00); // 50% yellow
                Assert.True(doc.IsModified);

                var r = sel.Rects[0];
                using var px = await doc.RenderPageAsync(0, 1, PageColors.Normal);
                // Sample just above the glyph baseline area but inside the rect, on paper between letters.
                var hits = 0;
                for (var x = (int)r.X; x < (int)r.Right; x++)
                {
                    var p = Pixel(px, x, (int)(r.Y + 2));
                    if (((p >> 16) & 0xFF) > 200 && (p & 0xFF) < 160) hits++; // yellowish
                }
                Assert.True(hits > 5, "highlight should tint the text area yellow");

                await doc.SaveAsync(path); // overwrite the file that is open
                Assert.False(doc.IsModified);
            }

            await using var reopened = await PdfDocument.OpenAsync(path);
            var sel2 = await reopened.GetSelectionAsync(0, 0, 4);
            using var px2 = await reopened.RenderPageAsync(0, 1, PageColors.Normal);
            var r2 = sel2.Rects[0];
            var yellow = Enumerable.Range((int)r2.X, (int)r2.Width).Select(x => Pixel(px2, x, (int)(r2.Y + 2)))
                .Count(p => ((p >> 16) & 0xFF) > 200 && (p & 0xFF) < 160);
            Assert.True(yellow > 5, "saved highlight missing");
            Assert.Equal("Hello highlight", (await reopened.GetPageTextAsync(0)).Trim()); // text untouched
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Ink_stroke_draws_on_the_page()
    {
        await using var doc = await PdfDocument.OpenAsync(new MemoryStream(TestPdf.Create("x")));
        var stroke = Enumerable.Range(0, 50).Select(i => new PdfPoint(300 + i * 2, 400)).ToList();
        await doc.AddInkAsync(0, [stroke], 0xFFFF0000, 6);
        using var px = await doc.RenderPageAsync(0, 1, PageColors.Normal);
        var p = Pixel(px, 350, 400);
        Assert.True(((p >> 16) & 0xFF) > 200 && ((p >> 8) & 0xFF) < 80, $"expected red ink, got {p:X8}");
    }

    [Theory]
    [InlineData(CommentShape.Square)]
    [InlineData(CommentShape.Circle)]
    [InlineData(CommentShape.Cloud)]
    [InlineData(CommentShape.Bubble)]
    [InlineData(CommentShape.Star)]
    public async Task Comment_shapes_render_and_round_trip(CommentShape shape)
    {
        var path = TempPdf(TestPdf.Create("Comment on this text"));
        try
        {
            var marker = new PdfRect(400, 300, 24, 24);
            await using (var doc = await PdfDocument.OpenAsync(path))
            {
                var sel = await doc.GetSelectionAsync(0, 11, 14);
                await doc.AddCommentAsync(0, marker, "Check this ünïcödé note", sel.Text, sel.Rects, shape, 0xFF2F6FEB);
                using var px = await doc.RenderPageAsync(0, 1, PageColors.Normal);
                var center = Pixel(px, 412, 312);
                Assert.True((center & 0xFF) > 180 && ((center >> 16) & 0xFF) < 120, $"{shape} marker not drawn (pixel {center:X8})");
                await doc.SaveAsync(path);
            }

            await using var reopened = await PdfDocument.OpenAsync(path);
            var comments = await reopened.GetCommentsAsync(0);
            var c = Assert.Single(comments);
            Assert.Equal("Check this ünïcödé note", c.Text);
            Assert.Equal("this", c.Quote);
            Assert.Equal(shape, c.Shape);
            Assert.Equal(0xFF2F6FEBu, c.Color);
            Assert.NotEmpty(c.TextRects);
            Assert.InRange(c.Marker.X, 399, 401);
            Assert.InRange(c.Marker.Y, 299, 301);
            Assert.True((DateTime.UtcNow - c.CreatedUtc).TotalMinutes < 5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Removing_an_annotation_undoes_it()
    {
        await using var doc = await PdfDocument.OpenAsync(new MemoryStream(TestPdf.Create("Remove me")));
        var id = await doc.AddCommentAsync(0, new PdfRect(100, 100, 20, 20), "temp", "", [], CommentShape.Circle, 0xFFFF0000);
        Assert.Single(await doc.GetCommentsAsync(0));
        Assert.True(await doc.RemoveAnnotationAsync(0, id));
        Assert.Empty(await doc.GetCommentsAsync(0));
        Assert.False(await doc.RemoveAnnotationAsync(0, "missing"));
    }

    [Fact]
    public async Task Save_as_writes_a_new_file_and_keeps_the_original()
    {
        var path = TempPdf(TestPdf.Create("Original"));
        var copy = Path.ChangeExtension(path, ".copy.pdf");
        try
        {
            var originalBytes = await File.ReadAllBytesAsync(path);
            await using (var doc = await PdfDocument.OpenAsync(path))
            {
                await doc.AddCommentAsync(0, new PdfRect(50, 50, 20, 20), "in copy", "", [], CommentShape.Square, 0xFFFFC83D);
                await doc.SaveAsync(copy);
            }
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
            var saved = await File.ReadAllBytesAsync(copy);
            Assert.Equal(originalBytes, saved.AsSpan(0, originalBytes.Length).ToArray()); // incremental update: original bytes preserved

            await using var reopened = await PdfDocument.OpenAsync(copy);
            Assert.Equal("in copy", Assert.Single(await reopened.GetCommentsAsync(0)).Text);
        }
        finally
        {
            File.Delete(path);
            File.Delete(copy);
        }
    }

    [Theory]
    [InlineData("D:20261103142530Z", 2026, 11, 3, 14, 25)]
    [InlineData("D:20261103142530+02'00'", 2026, 11, 3, 12, 25)]
    [InlineData("D:2026110314", 2026, 11, 3, 14, 0)]
    public void Parses_pdf_dates(string value, int y, int mo, int d, int h, int mi)
    {
        var parsed = PdfDocument.ParsePdfDate(value);
        Assert.Equal(new DateTime(y, mo, d, h, mi, parsed!.Value.Second, DateTimeKind.Utc), parsed.Value);
    }
}
