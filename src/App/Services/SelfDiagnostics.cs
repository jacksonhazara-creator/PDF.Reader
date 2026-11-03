using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfReader.App.Controls;
using PdfReader.App.Localization;
using PdfReader.App.ViewModels;
using PdfReader.Core.Bookmarks;
using PdfReader.Core.Diagnostics;
using PdfReader.Core.Settings;
using PdfReader.Rendering;

namespace PdfReader.App.Services;

/// <summary>
/// Developer mode → "Run self-diagnosis": tests this installation on this PC from inside the app
/// (engine, every annotation type, tools, shortcuts, and live scroll/zoom lag on the real window)
/// and writes a plain-text report. Uses a generated sample document; never the user's files.
/// </summary>
public sealed class SelfDiagnostics(MainViewModel main, TopLevel window)
{
    private const double SmoothFrameMs = 33.4;  // ≥ 30 fps
    private const double SharpWithinMs = 600;

    private readonly List<(string Area, string Name, Outcome Result, string Detail)> _checks = [];
    private readonly StringBuilder _metrics = new();
    private Action<string>? _progress;

    private enum Outcome { Pass, Warn, Fail }

    public static string ReportFolder => Path.Combine(AppPaths.DataDirectory, "Diagnostics");

    public static void OpenReportFolder()
    {
        Directory.CreateDirectory(ReportFolder);
        Process.Start(new ProcessStartInfo(ReportFolder) { UseShellExecute = true });
    }

    /// <returns>The report text; it is also saved to <see cref="ReportFolder"/>.</returns>
    public async Task<(string Report, string Path)> RunAsync(Action<string>? progress = null)
    {
        _progress = progress;
        var total = Stopwatch.StartNew();
        await Section("Environment", EnvironmentChecks);
        await Section("PDF engine", EngineAsync);
        await Section("Annotations", AnnotationsAsync);
        await Section("Tools & commands", ToolsAndCommands);
        await Section("Live viewer (lag)", LiveViewerAsync);

        var report = BuildReport(total.Elapsed);
        Directory.CreateDirectory(ReportFolder);
        var path = System.IO.Path.Combine(ReportFolder, $"report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(path, report);
        return (report, path);
    }

    // ---------------------------------------------------------------------------------------

    private async Task Section(string name, Func<Task> body)
    {
        _progress?.Invoke(name);
        await Task.Delay(30); // let the progress text paint
        try { await body(); }
        catch (Exception ex) { Check(name, "Section completed", false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    private Task Section(string name, Action body) => Section(name, () => { body(); return Task.CompletedTask; });

    private void Check(string area, string name, bool ok, string detail = "") => _checks.Add((area, name, ok ? Outcome.Pass : Outcome.Fail, detail));
    private void Warn(string area, string name, string detail) => _checks.Add((area, name, Outcome.Warn, detail));
    private void Metric(string text) => _metrics.AppendLine("  " + text);

    private static async Task<T> Timed<T>(Func<Task<T>> work, Action<double> ms)
    {
        var sw = Stopwatch.StartNew();
        var result = await work();
        ms(sw.Elapsed.TotalMilliseconds);
        return result;
    }

    // --- Environment ---------------------------------------------------------------------------

    private void EnvironmentChecks()
    {
        const string a = "Environment";
        Metric($"App {AppInfo.Version}, {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}");
        Metric($"Render scaling {window.RenderScaling:0.##}, window {window.Bounds.Width:0}×{window.Bounds.Height:0}, language {Loc.Language}");
        Metric($"Working set {Process.GetCurrentProcess().WorkingSet64 / 1048576} MB, tile cache {TileCache.Shared.Bytes / 1048576} MB");
        var fonts = FontManager.Current.SystemFonts.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var iconFont = fonts.Contains("Segoe Fluent Icons") || fonts.Contains("Segoe MDL2 Assets");
        Check(a, "Icon font available (Segoe Fluent Icons / MDL2)", iconFont, iconFont ? "" : "icons fall back to line drawings");
        if (FontProvider.Handwriting is null)
            Warn(a, "Handwriting font for typed signatures", "not found; typed signatures use Helvetica Oblique");
        else
            Check(a, "Handwriting font for typed signatures", true);
        Check(a, "Settings folder writable", IsWritable(AppPaths.DataDirectory), AppPaths.DataDirectory);
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = System.IO.Path.Combine(dir, $".probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception) { return false; }
    }

    // --- Engine --------------------------------------------------------------------------------

    private async Task EngineAsync()
    {
        const string a = "PDF engine";
        double ms = 0;
        await using var doc = await Timed(() => PdfDocument.OpenAsync(new MemoryStream(SamplePdf.Create(60))), t => ms = t);
        Check(a, "Open 60-page document", doc.PageCount == 60, $"{ms:0} ms");

        using (var px = await Timed(() => doc.RenderPageAsync(0, 2.0, PageColors.Normal, WorkPriority.Immediate), t => ms = t))
            Check(a, "Render page at 200%", px.Width == 1224 && HasInk(px), $"{ms:0} ms");

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < doc.PageCount; i++)
            (await doc.RenderPageAsync(i, 0.25, PageColors.Normal)).Dispose();
        Check(a, "Render 60 thumbnails", true, $"{sw.ElapsedMilliseconds} ms ({sw.ElapsedMilliseconds / 60.0:0.0} ms/page)");

        using (var dark = await doc.RenderPageAsync(0, 0.5, PageColors.Dark(false)))
            Check(a, "Dark page colors", (dark.Pixels[dark.Width * 300 + 20] & 0xFF) < 60);

        var text = await Timed(() => doc.GetPageTextAsync(0), t => ms = t);
        Check(a, "Extract text", text.Contains("PDFium"), $"{ms:0} ms");

        sw.Restart();
        var hits = 0;
        for (var i = 0; i < doc.PageCount; i++)
            hits += (await doc.SearchPageAsync(i, "fox", false, false)).Count;
        Check(a, "Search whole document", hits > 60, $"{hits} hits in {sw.ElapsedMilliseconds} ms");

        var outline = await doc.GetOutlineAsync();
        Check(a, "Read bookmarks", outline.Count == 60);
        var link = await doc.GetLinkAtAsync(0, 100, 792 - 50);
        Check(a, "Detect link (never followed)", link is { Kind: LinkKind.Uri, Target: "https://example.com/" });
        var security = await Timed(() => doc.ScanActiveContentAsync(), t => ms = t);
        Check(a, "Security scan (scripts/attachments/launch)", !security.HasActiveContent, $"{ms:0} ms");
    }

    private static bool HasInk(PixelBuffer px)
    {
        foreach (var p in px.Pixels)
            if ((p & 0xFF) < 100)
                return true;
        return false;
    }

    // --- Annotations ------------------------------------------------------------------------------

    private async Task AnnotationsAsync()
    {
        const string a = "Annotations";
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pdfreader-diag-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, SamplePdf.Create(3));
        try
        {
            var ids = new List<(string Name, string Id, double X, double Y)>();
            await using (var doc = await PdfDocument.OpenAsync(path))
            {
                var line = await doc.GetSelectionAsync(0, 3, 30);
                var y = line.Rects[0].Y + line.Rects[0].Height / 2;
                async Task Add(string name, Func<Task<string>> create, double x, double py)
                {
                    using var before = await doc.RenderPageAsync(0, 1, PageColors.Normal, WorkPriority.Immediate);
                    var sw = Stopwatch.StartNew();
                    var id = await create();
                    var hit = await doc.GetAnnotationAtAsync(0, x, py);
                    using var after = await doc.RenderPageAsync(0, 1, PageColors.Normal, WorkPriority.Immediate);
                    var changed = Differs(before, after);
                    Check(a, $"{name}: drawn and found under the pointer", hit?.Id == id && changed, $"{sw.ElapsedMilliseconds} ms");
                    ids.Add((name, id, x, py));
                }

                await Add("Highlight", () => doc.AddTextMarkupAsync(0, TextMarkupKind.Highlight, line.Rects, 0x80FFE600), line.Rects[0].X + 10, y);
                var line2 = await doc.GetSelectionAsync(0, 80, 110);
                var y2 = line2.Rects[0].Y + line2.Rects[0].Height / 2;
                await Add("Underline", () => doc.AddTextMarkupAsync(0, TextMarkupKind.Underline, line2.Rects, 0xFF2F6FEB), line2.Rects[0].X + 10, y2);
                var line3 = await doc.GetSelectionAsync(0, 150, 180);
                var y3 = line3.Rects[0].Y + line3.Rects[0].Height / 2;
                await Add("Strikethrough", () => doc.AddTextMarkupAsync(0, TextMarkupKind.StrikeOut, line3.Rects, 0xFFD9363E), line3.Rects[0].X + 10, y3);
                await Add("Freehand drawing", () => doc.AddInkAsync(0, [[new(400, 300), new(450, 320), new(500, 300)]], 0xFFD9363E, 3), 450, 310);
                await Add("Rectangle", () => doc.AddShapeAsync(0, ShapeKind.Rectangle, new PdfRect(400, 400, 80, 50), 0xFF1F9D55, 2), 440, 425);
                await Add("Circle", () => doc.AddShapeAsync(0, ShapeKind.Ellipse, new PdfRect(400, 480, 80, 50), 0xFF8E5BE8, 2), 440, 505);
                await Add("Stamp (text + shape)", () => doc.AddStampAsync(0,
                    [new StampPath([new(400, 560), new(520, 560), new(520, 590), new(400, 590)], true, 0xFF1F9D55, 2), new StampText("APPROVED", new PdfPoint(410, 581), 16, 0xFF1F9D55, "Helvetica-Bold")],
                    "Stamp", "Approved"), 460, 575);
                foreach (var shape in Enum.GetValues<CommentShape>())
                {
                    var x = 40 + (int)shape * 30;
                    await Add($"Comment ({shape})", () => doc.AddCommentAsync(0, new PdfRect(x, 640, 20, 20), $"note {shape}", "quoted", line.Rects, shape, 0xFFFFB020), x + 10, 650);
                }
                await Add("Insert text", () => doc.AddInsertAsync(0, new PdfRect(250, 645, 10, 10), "inserted", 0xFF2F6FEB), 255, 650);
                await Add("File attachment", () => doc.AddFileAttachmentAsync(0, new PdfRect(300, 640, 16, 20), "diag.txt", "x"u8.ToArray(), 0xFF2F6FEB), 308, 650);

                // Recolor and delete through the same calls the right-click menu uses.
                var rect = ids.First(i => i.Name == "Rectangle");
                Check(a, "Right-click: change color", await doc.SetAnnotationColorAsync(0, rect.Id, 0x2F6FEB)
                                                      && ((await doc.GetAnnotationAtAsync(0, rect.X, rect.Y))!.Color & 0xFFFFFF) == 0x2F6FEB);
                var circle = ids.First(i => i.Name == "Circle");
                Check(a, "Right-click: delete", await doc.RemoveAnnotationAsync(0, circle.Id) && await doc.GetAnnotationAtAsync(0, circle.X, circle.Y) is null);
                var comment = ids.First(i => i.Name.StartsWith("Comment", StringComparison.Ordinal));
                Check(a, "Right-click: edit comment", await doc.SetAnnotationTextAsync(0, comment.Id, "edited"));

                var sw = Stopwatch.StartNew();
                await doc.SaveAsync(path, temp => OutlineEditor.Apply(temp, [new OutlineEdit.Remove([1]), new OutlineEdit.Add("Diagnostics", 0)]));
                Check(a, "Save in place (incremental) with bookmark edits", !doc.IsModified, $"{sw.ElapsedMilliseconds} ms");
            }

            await using var reopened = await PdfDocument.OpenAsync(path);
            var comments = await reopened.GetCommentsAsync(0);
            Check(a, "Reopen: comments kept", comments.Count == 8, $"{comments.Count} of 8 (5 shapes, insert, attachment, stamp)");
            Check(a, "Reopen: edited comment text kept", comments.Any(c => c.Text == "edited"));
            var kept = 0;
            foreach (var (name, id, x, y) in ids.Where(i => i.Name != "Circle"))
                if ((await reopened.GetAnnotationAtAsync(0, x, y))?.Id == id)
                    kept++;
            Check(a, "Reopen: every mark kept", kept == ids.Count - 1, $"{kept} of {ids.Count - 1}");
            var outline = (await reopened.GetOutlineAsync()).Select(o => o.Title).ToList();
            Check(a, "Reopen: bookmark removed and added", outline.SequenceEqual(["Chapter 1", "Chapter 3", "Diagnostics"]), string.Join(", ", outline));
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static bool Differs(PixelBuffer a, PixelBuffer b)
    {
        var pa = a.Pixels;
        var pb = b.Pixels;
        var diff = 0;
        for (var i = 0; i < pa.Length; i += 3)
            if (pa[i] != pb[i] && ++diff > 10)
                return true;
        return false;
    }

    // --- Tools & commands ----------------------------------------------------------------------------

    private void ToolsAndCommands()
    {
        const string a = "Tools & commands";
        var markup = main.Markup;
        var previous = markup.Tool;
        var problems = new List<string>();
        foreach (var tool in markup.AllTools)
        {
            markup.Tool = tool.Kind;
            if (tool.Icon is null) problems.Add($"{tool.Kind}: no icon");
            if (tool.Title == tool.TitleKey) problems.Add($"{tool.Kind}: untranslated title");
            if (markup.Hint.StartsWith("Hint", StringComparison.Ordinal)) problems.Add($"{tool.Kind}: no hint text");
            if (markup.HasOptions == tool.Kind is ToolKind.Select or ToolKind.Pan) problems.Add($"{tool.Kind}: options bar wrong");
        }
        markup.Tool = previous;
        Check(a, $"All {markup.AllTools.Count()} tools selectable, with icon, name, hint", problems.Count == 0, string.Join("; ", problems));

        var commandProblems = main.Commands.Where(c => c.Icon is null || c.Title == c.TitleKey).Select(c => c.Id).ToList();
        Check(a, $"All {main.Commands.Count} commands have icon and translated title", commandProblems.Count == 0, string.Join(", ", commandProblems));

        var clashes = main.Commands.SelectMany(c => c.Gestures.Select(g => (Gesture: g.ToString(), c.Id)))
            .GroupBy(x => x.Gesture).Where(g => g.Count() > 1).Select(g => $"{g.Key}: {string.Join(" / ", g.Select(x => x.Id))}").ToList();
        Check(a, "No two commands share a shortcut", clashes.Count == 0, string.Join("; ", clashes));

        var german = Loc.Language == "de";
        Check(a, $"UI language ({(german ? "Deutsch" : "English")}) strings present", main.Commands.All(c => !string.IsNullOrWhiteSpace(c.Title)));
    }

    // --- Live viewer: lag on the real window --------------------------------------------------------

    private async Task LiveViewerAsync()
    {
        const string a = "Live viewer (lag)";
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pdfreader-diag-live-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, SamplePdf.Create(200));
        var previousTab = main.ActiveDocument;
        var toolsWereOpen = main.IsToolsPanelOpen;
        DocumentViewModel? doc = null;
        try
        {
            var sw = Stopwatch.StartNew();
            doc = await main.OpenFileAsync(path);
            if (doc is null)
            {
                Check(a, "Open sample in a tab", false);
                return;
            }
            var viewer = await WaitForViewerAsync();
            await UntilSharpAsync(viewer, 5000);
            var firstPage = sw.Elapsed.TotalMilliseconds;
            Check(a, "Open 200-page document until first page is sharp", firstPage < 1500, $"{firstPage:0} ms");
            doc.Request(new ViewerRequest.SetZoomMode(ZoomMode.FitWidth));
            await UntilSharpAsync(viewer, 3000);

            // 1. Fast continuous scrolling (like a flick of the wheel), 3 seconds.
            var stats = await MeasureFramesAsync(TimeSpan.FromSeconds(3), _ => viewer.ScrollByPixels(45));
            var sharp = await UntilSharpAsync(viewer, 5000);
            Report(a, "Fast scrolling (3 s)", stats, sharp);

            // 2. Jumping to far-away pages.
            var jumps = new List<double>();
            foreach (var page in new[] { 150, 10, 199, 90, 0 })
            {
                doc.GoToPage(page);
                jumps.Add(await UntilSharpAsync(viewer, 5000));
            }
            CheckTimes(a, "Jump to far pages until sharp", jumps);

            // 3. Zooming in and out step by step.
            var zoomStats = await MeasureFramesAsync(TimeSpan.FromSeconds(2.4), frame =>
            {
                if (frame % 9 == 0)
                    doc.Request(new ViewerRequest.ZoomStep(frame < 72 ? +1 : -1));
            });
            sharp = await UntilSharpAsync(viewer, 5000);
            Report(a, "Zoom in/out (8 steps each)", zoomStats, sharp);

            // 4. All tools panel and right panel sliding in/out.
            var panelStats = await MeasureFramesAsync(TimeSpan.FromSeconds(2.4), frame =>
            {
                if (frame % 18 == 0) main.IsToolsPanelOpen = !main.IsToolsPanelOpen;
                if (frame % 18 == 9) doc.IsSidebarOpen = !doc.IsSidebarOpen;
            });
            main.IsToolsPanelOpen = toolsWereOpen;
            sharp = await UntilSharpAsync(viewer, 5000);
            Report(a, "Panels opening/closing", panelStats, sharp);

            // 5. Scrolling in dark mode.
            var mode = main.Settings.PageColorMode;
            main.SetPageColorMode(PageColorMode.Dark);
            await UntilSharpAsync(viewer, 3000);
            var darkStats = await MeasureFramesAsync(TimeSpan.FromSeconds(1.5), _ => viewer.ScrollByPixels(45));
            sharp = await UntilSharpAsync(viewer, 5000);
            main.SetPageColorMode(mode);
            Report(a, "Scrolling in dark mode", darkStats, sharp);

            // 6. Idle: nothing should be redrawing hard.
            var idle = await MeasureFramesAsync(TimeSpan.FromSeconds(1), null);
            Report(a, "Idle", idle, 0);

            Metric($"Memory after test: {Process.GetCurrentProcess().WorkingSet64 / 1048576} MB working set, tile cache {TileCache.Shared.Bytes / 1048576} MB (limit 384 MB)");
            Check(a, "Tile cache stays within its memory limit", TileCache.Shared.Bytes <= 384L * 1024 * 1024);
        }
        finally
        {
            if (doc is not null)
                await main.TryCloseDocumentAsync(doc);
            if (previousTab is not null && main.Documents.Contains(previousTab))
                main.ActiveDocument = previousTab;
            // Leave no trace of the sample: not in Recent files, no saved position, no thumbnail.
            main.State.Documents.Remove(path);
            main.RemoveRecent(path);
            try { File.Delete(path); } catch (IOException) { }
            try { File.Delete(AppPaths.ThumbnailPathFor(path)); } catch (IOException) { }
        }
    }

    private async Task<PdfViewer> WaitForViewerAsync()
    {
        for (var i = 0; i < 200; i++)
        {
            if (window.GetVisualDescendants().OfType<PdfViewer>().FirstOrDefault(v => v.IsEffectivelyVisible && v.DataContext == main.ActiveDocument) is { } viewer)
                return viewer;
            await Task.Delay(20);
        }
        throw new TimeoutException("Viewer did not appear");
    }

    private static async Task<double> UntilSharpAsync(PdfViewer viewer, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        await Task.Delay(16);
        while (!viewer.IsSharp && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(8);
        return sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>Runs <paramref name="perFrame"/> on every rendered frame and records the time between frames.</summary>
    private Task<FrameStats> MeasureFramesAsync(TimeSpan duration, Action<int>? perFrame)
    {
        var tcs = new TaskCompletionSource<FrameStats>();
        var gaps = new List<double>();
        var sw = Stopwatch.StartNew();
        long last = 0;
        var frame = 0;
        void OnFrame(TimeSpan _)
        {
            var now = Stopwatch.GetTimestamp();
            if (last != 0)
                gaps.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
            last = now;
            try { perFrame?.Invoke(frame++); } catch (Exception ex) { tcs.TrySetException(ex); return; }
            if (sw.Elapsed < duration)
                window.RequestAnimationFrame(OnFrame);
            else
                tcs.TrySetResult(new FrameStats(gaps));
        }
        window.RequestAnimationFrame(OnFrame);
        return tcs.Task;
    }

    private void Report(string area, string name, FrameStats s, double sharpMs)
    {
        Metric($"{name}: {s.Fps:0} fps, frame p50 {s.P(0.5):0.0} ms, p95 {s.P(0.95):0.0} ms, max {s.Max:0} ms, frames >33 ms: {s.Over(33.4)}, >100 ms: {s.Over(100)}" +
               (sharpMs > 0 ? $", sharp again after {sharpMs:0} ms" : ""));
        var smooth = s.P(0.95) <= SmoothFrameMs;
        var detail = $"p95 {s.P(0.95):0.0} ms, max {s.Max:0} ms" + (sharpMs > 0 ? $", sharp after {sharpMs:0} ms" : "");
        if (!smooth || s.Max > 250 || sharpMs > 3000)
            Check(area, $"{name}: smooth", false, detail);
        else if (s.Max > 100 || sharpMs > SharpWithinMs)
            Warn(area, $"{name}: smooth", detail + " (occasional hitch)");
        else
            Check(area, $"{name}: smooth", true, detail);
    }

    private void CheckTimes(string area, string name, List<double> times)
    {
        var max = times.Max();
        Metric($"{name}: {string.Join(", ", times.Select(t => $"{t:0}"))} ms");
        if (max <= SharpWithinMs) Check(area, name, true, $"max {max:0} ms");
        else if (max <= 1500) Warn(area, name, $"max {max:0} ms");
        else Check(area, name, false, $"max {max:0} ms");
    }

    private sealed class FrameStats(List<double> gaps)
    {
        private readonly double[] _sorted = gaps.Order().ToArray();
        public double P(double q) => _sorted.Length == 0 ? 0 : _sorted[Math.Min(_sorted.Length - 1, (int)(_sorted.Length * q))];
        public double Max => _sorted.Length == 0 ? 0 : _sorted[^1];
        public int Over(double ms) => _sorted.Count(g => g > ms);
        public double Fps => _sorted.Length == 0 ? 0 : 1000 / _sorted.Average();
    }

    // --- Report ------------------------------------------------------------------------------------

    private string BuildReport(TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        var pass = _checks.Count(c => c.Result == Outcome.Pass);
        var warn = _checks.Count(c => c.Result == Outcome.Warn);
        var fail = _checks.Count(c => c.Result == Outcome.Fail);
        sb.AppendLine(CultureInfo.InvariantCulture, $"PDF.Reader self-diagnosis — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Result: {pass} passed, {warn} warnings, {fail} failed  (took {elapsed.TotalSeconds:0.0} s)");
        sb.AppendLine("No document content is included. Nothing has been sent anywhere.");
        sb.AppendLine();
        foreach (var group in _checks.GroupBy(c => c.Area))
        {
            sb.AppendLine(group.Key);
            foreach (var c in group)
            {
                var mark = c.Result switch { Outcome.Pass => "PASS", Outcome.Warn => "WARN", _ => "FAIL" };
                sb.AppendLine(CultureInfo.InvariantCulture, $"  [{mark}] {c.Name}{(c.Detail.Length > 0 ? " — " + c.Detail : "")}");
            }
            sb.AppendLine();
        }
        sb.AppendLine("Measurements");
        sb.Append(_metrics);
        return sb.ToString();
    }
}
