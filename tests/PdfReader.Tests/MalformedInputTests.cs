using PdfReader.Rendering;

namespace PdfReader.Tests;

/// <summary>
/// Treat every PDF as hostile: damaged input must produce a PdfException (or open best-effort),
/// never crash the process or hang. This is a deterministic mutation fuzzer over the open,
/// render, text and outline paths. Set FUZZ_ITERATIONS for longer runs (CI nightly uses 20000).
/// </summary>
public class MalformedInputTests
{
    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46 })] // "%PDF"
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })] // a JPEG header
    public async Task Garbage_is_rejected_with_PdfException(byte[] data)
    {
        var ex = await Assert.ThrowsAsync<PdfException>(() => PdfDocument.OpenAsync(new MemoryStream(data)));
        Assert.True(ex.Error is PdfError.InvalidFormat or PdfError.FileNotFound);
    }

    [Fact]
    public async Task Truncated_files_never_crash()
    {
        var pdf = TestPdf.Create(new TestPdf.Options { Pages = ["a", "b"], Outline = true });
        for (var len = 1; len < pdf.Length; len += Math.Max(1, pdf.Length / 60))
            await Exercise(pdf.AsSpan(0, len).ToArray());
    }

    [Fact]
    public async Task Random_mutations_never_crash()
    {
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("FUZZ_ITERATIONS"), out var n) ? n : 300;
        var seeds = new[]
        {
            TestPdf.Create(new TestPdf.Options { Pages = ["alpha", "beta"], Outline = true, UriLink = true }),
            TestPdf.Create(new TestPdf.Options { JavaScript = true, Attachment = true, LaunchLink = true, Rotate = 90 }),
        };
        var random = new Random(20261103);

        for (var i = 0; i < iterations; i++)
        {
            var data = seeds[i % seeds.Length].ToArray();
            var mutations = random.Next(1, 12);
            for (var m = 0; m < mutations; m++)
            {
                var pos = random.Next(data.Length);
                switch (random.Next(4))
                {
                    case 0: data[pos] = (byte)random.Next(256); break;              // flip a byte
                    case 1: data[pos] = (byte)"0123456789"[random.Next(10)]; break;  // corrupt numbers/offsets
                    case 2: data[pos] = (byte)"<>[]()/ "[random.Next(8)]; break;     // corrupt syntax
                    case 3: Array.Fill(data, (byte)'9', pos, Math.Min(8, data.Length - pos)); break; // huge numbers
                }
            }
            await Exercise(data);
        }
    }

    private static async Task Exercise(byte[] data)
    {
        PdfDocument doc;
        try
        {
            doc = await PdfDocument.OpenAsync(new MemoryStream(data));
        }
        catch (PdfException)
        {
            return; // rejected cleanly
        }

        await using (doc)
        {
            for (var p = 0; p < Math.Min(doc.PageCount, 3); p++)
            {
                await Tolerate(async () => { using var _ = await doc.RenderPageAsync(p, 0.25, PageColors.Normal); });
                await Tolerate(() => doc.GetPageTextAsync(p));
                await Tolerate(() => doc.SearchPageAsync(p, "a", false, false));
                await Tolerate(() => doc.GetLinkAtAsync(p, 100, 80));
            }
            await Tolerate(() => doc.GetOutlineAsync());
            await Tolerate(() => doc.ScanActiveContentAsync());
        }
    }

    private static async Task Tolerate(Func<Task> action)
    {
        try { await action(); }
        catch (PdfException) { }
    }
}
