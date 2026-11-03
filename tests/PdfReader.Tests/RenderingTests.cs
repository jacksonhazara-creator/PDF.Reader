using PdfReader.Core.Search;
using PdfReader.Rendering;

namespace PdfReader.Tests;

public class RenderingTests
{
    private static Task<PdfDocument> Open(byte[] pdf, string? password = null) => PdfDocument.OpenAsync(new MemoryStream(pdf), password: password);

    [Fact]
    public async Task Opens_document_and_reads_geometry()
    {
        await using var doc = await Open(TestPdf.Create("One", "Two", "Three"));
        Assert.Equal(3, doc.PageCount);
        Assert.All(doc.PageSizes, s => Assert.Equal(new PdfPageSize(612, 792), s));
    }

    [Fact]
    public async Task Rotated_page_reports_rotated_size()
    {
        await using var doc = await Open(TestPdf.Create(new TestPdf.Options { Rotate = 90 }));
        Assert.Equal(new PdfPageSize(792, 612), doc.PageSizes[0]);
    }

    [Fact]
    public async Task Renders_page_pixels()
    {
        await using var doc = await Open(TestPdf.Create(new TestPdf.Options { BlackSquare = true }));
        using var px = await doc.RenderPageAsync(0, 1.0, PageColors.Normal);
        Assert.Equal(612, px.Width);
        Assert.Equal(792, px.Height);
        Assert.Equal(0xFFFFFFFFu, Pixel(px, 10, 10));     // paper
        Assert.Equal(0xFF000000u, Pixel(px, 150, 150));   // the square
    }

    [Fact]
    public async Task Renders_tiles_that_match_the_full_page()
    {
        await using var doc = await Open(TestPdf.Create(new TestPdf.Options { BlackSquare = true }));
        using var tile = await doc.RenderAsync(new RenderRequest(0, 2.0, 180, 180, 64, 64, PageColors.Normal));
        // Page point (100,100) is pixel (200,200) at scale 2 => tile-local (20,20); inside the square.
        Assert.Equal(0xFF000000u, Pixel(tile, 30, 30));
        Assert.Equal(0xFFFFFFFFu, Pixel(tile, 5, 5));
    }

    [Fact]
    public async Task Dark_mode_recolors_paper_and_ink_without_touching_the_file()
    {
        var bytes = TestPdf.Create(new TestPdf.Options { BlackSquare = true });
        var original = bytes.ToArray();
        await using var doc = await Open(bytes);

        var dark = PageColors.Dark(keepImagesNatural: false);
        using var px = await doc.RenderPageAsync(0, 1.0, dark);
        Assert.Equal(dark.Background, Pixel(px, 10, 10));
        Assert.Equal(dark.Foreground, Pixel(px, 150, 150));

        var natural = PageColors.Dark(keepImagesNatural: true);
        using var px2 = await doc.RenderPageAsync(0, 1.0, natural);
        Assert.Equal(natural.Background, Pixel(px2, 10, 10));

        Assert.Equal(original, bytes);
    }

    [Fact]
    public void Luminance_map_maps_white_to_background_and_black_to_foreground()
    {
        uint[] pixels = [0xFFFFFFFF, 0xFF000000];
        var colors = PageColors.Sepia(false);
        PageColorFilter.ApplyLuminanceMap(pixels, colors);
        Assert.Equal(colors.Background, pixels[0]);
        Assert.Equal(colors.Foreground, pixels[1]);
    }

    [Fact]
    public async Task Extracts_text_and_selection_rectangles()
    {
        await using var doc = await Open(TestPdf.Create("Hello PDF reader"));
        var text = await doc.GetPageTextAsync(0);
        Assert.Contains("Hello PDF reader", text);

        var selection = await doc.GetSelectionAsync(0, 0, 4);
        Assert.Equal("Hello", selection.Text);
        Assert.NotEmpty(selection.Rects);
        var r = selection.Rects[0];
        // Text baseline is at y=700 from the bottom => ~92pt from the top, 24pt font.
        Assert.InRange(r.X, 60, 90);
        Assert.InRange(r.Y, 60, 100);
    }

    [Fact]
    public async Task Hit_test_finds_character_under_point()
    {
        await using var doc = await Open(TestPdf.Create("Hello"));
        var sel = await doc.GetSelectionAsync(0, 0, 0);
        var r = sel.Rects[0];
        var index = await doc.GetCharIndexAtAsync(0, r.X + r.Width / 2, r.Y + r.Height / 2);
        Assert.Equal(0, index);
        Assert.Equal(-1, await doc.GetCharIndexAtAsync(0, 500, 700, tolerance: 1));
    }

    [Fact]
    public async Task Searches_across_pages()
    {
        await using var doc = await Open(TestPdf.Create("alpha beta", "gamma", "beta again"));
        var hits = new List<SearchHit>();
        await foreach (var batch in DocumentSearch.SearchAsync(doc, "beta", new SearchOptions()))
            hits.AddRange(batch);
        Assert.Equal([0, 2], hits.Select(h => h.PageIndex).Order());
        Assert.All(hits, h => Assert.NotEmpty(h.Rects));

        var caseHits = await doc.SearchPageAsync(0, "BETA", matchCase: true, wholeWord: false);
        Assert.Empty(caseHits);
    }

    [Fact]
    public async Task Reads_outline()
    {
        await using var doc = await Open(TestPdf.Create(new TestPdf.Options { Pages = ["a", "b", "c"], Outline = true }));
        var outline = await doc.GetOutlineAsync();
        Assert.Equal(["Chapter 1", "Chapter 2", "Chapter 3"], outline.Select(o => o.Title));
        Assert.Equal([0, 1, 2], outline.Select(o => o.PageIndex));
    }

    [Fact]
    public async Task Detects_scripts_attachments_and_launch_actions()
    {
        await using var doc = await Open(TestPdf.Create(new TestPdf.Options { JavaScript = true, Attachment = true, LaunchLink = true }));
        Assert.True(doc.Security.HasScripts);
        Assert.True(doc.Security.HasAttachments);

        var report = await doc.ScanActiveContentAsync();
        Assert.True(report.HasLaunchActions);
        Assert.True(report.HasActiveContent);
    }

    [Fact]
    public async Task Clean_document_has_no_active_content()
    {
        await using var doc = await Open(TestPdf.Create("plain"));
        var report = await doc.ScanActiveContentAsync();
        Assert.False(report.HasActiveContent);
    }

    [Fact]
    public async Task Reports_links_without_following_them()
    {
        await using var doc = await Open(TestPdf.Create(new TestPdf.Options { UriLink = true }));
        // Link rect in PDF space [72 690 300 730] => top-left space y = 792-730 .. 792-690
        var link = await doc.GetLinkAtAsync(0, 100, 80);
        Assert.NotNull(link);
        Assert.Equal(LinkKind.Uri, link!.Kind);
        Assert.Equal("https://example.com/", link.Target);
        Assert.Null(await doc.GetLinkAtAsync(0, 500, 500));

        await using var launchDoc = await Open(TestPdf.Create(new TestPdf.Options { LaunchLink = true }));
        var launch = await launchDoc.GetLinkAtAsync(0, 100, 80);
        Assert.Equal(LinkKind.Launch, launch!.Kind);
        Assert.Equal("calc.exe", launch.Target);
    }

    [Fact]
    public async Task Cancelled_render_is_dropped()
    {
        await using var doc = await Open(TestPdf.Create("x"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doc.RenderPageAsync(0, 1, PageColors.Normal, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Missing_file_gives_friendly_error()
    {
        var ex = await Assert.ThrowsAsync<PdfException>(() => PdfDocument.OpenAsync(@"Z:\definitely\missing.pdf"));
        Assert.Equal(PdfError.FileNotFound, ex.Error);
    }

    [Fact]
    public async Task Opens_from_disk_and_allows_others_to_write()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfreader-test-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, TestPdf.Create("on disk"));
        try
        {
            await using var doc = await PdfDocument.OpenAsync(path);
            Assert.Equal(1, doc.PageCount);
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) { }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static uint Pixel(PixelBuffer px, int x, int y) => px.Pixels[y * px.Width + x];
}
