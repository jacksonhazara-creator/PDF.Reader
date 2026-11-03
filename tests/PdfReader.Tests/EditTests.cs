using PdfReader.Rendering;

namespace PdfReader.Tests;

public class EditTests
{
    private static uint Pixel(PixelBuffer px, int x, int y) => px.Pixels[y * px.Width + x];
    private static int Count(PixelBuffer px, Func<uint, bool> match)
    {
        var n = 0;
        foreach (var p in px.Pixels) if (match(p)) n++;
        return n;
    }
    private static bool Reddish(uint p) => ((p >> 16) & 0xFF) > 170 && ((p >> 8) & 0xFF) < 110 && (p & 0xFF) < 110;
    private static bool Bluish(uint p) => (p & 0xFF) > 170 && ((p >> 16) & 0xFF) < 110 && ((p >> 8) & 0xFF) < 150;

    public static TheoryData<string> Kinds => new() { "highlight", "ink", "square", "stamp", "comment" };

    private static async Task<string> Add(PdfDocument doc, string kind)
    {
        var sel = await doc.GetSelectionAsync(0, 0, 4);
        return kind switch
        {
            "highlight" => await doc.AddTextMarkupAsync(0, TextMarkupKind.Highlight, sel.Rects, 0xFFFF0000),
            "ink" => await doc.AddInkAsync(0, [[new(300, 400), new(400, 400)]], 0xFFFF0000, 6),
            "square" => await doc.AddShapeAsync(0, ShapeKind.Rectangle, new PdfRect(300, 380, 100, 40), 0xFFFF0000, 4),
            "stamp" => await doc.AddStampAsync(0, [new StampPath([new(300, 380), new(400, 380), new(400, 420), new(300, 420)], true, 0xFFFF0000, 4, 0xFFFF0000)], "Test"),
            _ => await doc.AddCommentAsync(0, new PdfRect(300, 380, 40, 40), "note", "Hello", sel.Rects, CommentShape.Square, 0xFFFF0000),
        };
    }

    private static (double X, double Y) PointFor(string kind, PdfDocument doc) => kind == "comment" ? (320, 400) : (350, 400);

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Right_click_targets_can_be_found_recolored_and_deleted(string kind)
    {
        await using var doc = await PdfDocument.OpenAsync(new MemoryStream(TestPdf.Create("Hello world")));
        var id = await Add(doc, kind);
        var (x, y) = PointFor(kind, doc);
        if (kind == "highlight")
        {
            var r = (await doc.GetSelectionAsync(0, 0, 4)).Rects[0];
            (x, y) = (r.X + r.Width / 2, r.Y + r.Height / 2);
        }

        var hit = await doc.GetAnnotationAtAsync(0, x, y);
        Assert.NotNull(hit);
        Assert.Equal(id, hit!.Id);
        Assert.Null(await doc.GetAnnotationAtAsync(0, 580, 770));

        using (var before = await doc.RenderPageAsync(0, 1, PageColors.Normal))
            Assert.True(Count(before, Reddish) > 50, "should start red");

        Assert.True(await doc.SetAnnotationColorAsync(0, id, 0x0000FF));
        using (var after = await doc.RenderPageAsync(0, 1, PageColors.Normal))
        {
            Assert.True(Count(after, Reddish) < 10, $"{kind}: red should be gone");
            Assert.True(Count(after, Bluish) > 50, $"{kind}: should be blue now");
        }

        Assert.True(await doc.RemoveAnnotationAsync(0, id));
        Assert.Null(await doc.GetAnnotationAtAsync(0, x, y));
    }

    [Fact]
    public async Task Comment_text_can_be_edited()
    {
        await using var doc = await PdfDocument.OpenAsync(new MemoryStream(TestPdf.Create("x")));
        var id = await doc.AddCommentAsync(0, new PdfRect(100, 100, 16, 16), "old", "", [], CommentShape.Circle, 0xFFFFB020);
        Assert.True(await doc.SetAnnotationTextAsync(0, id, "new text"));
        Assert.Equal("new text", Assert.Single(await doc.GetCommentsAsync(0)).Text);
    }
}
