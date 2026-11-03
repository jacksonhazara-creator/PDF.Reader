using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfReader.App;
using PdfReader.App.Controls;
using PdfReader.App.ViewModels;
using PdfReader.App.Views;
using PdfReader.Core.Settings;
using PdfReader.Rendering;
using PdfReader.Tests;

[assembly: AvaloniaTestApplication(typeof(PdfReader.UiTests.TestAppBuilder))]

namespace PdfReader.UiTests;

public static class TestAppBuilder
{
    // Real Skia rendering (not the headless stub) so screenshots show actual pixels.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<PdfReader.App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Opens the real MainWindow headlessly and drives it like a user would (clicks, drags, keys).</summary>
internal sealed class AppSession
{
    public static readonly string[] SampleText =
    [
        "The quick brown fox jumps over the lazy dog",
        "Second page with some text to mark",
        "Third page for bookmarks and comments",
        "Fourth page",
    ];

    private AppSession(MainWindow window, string dataDir)
    {
        Window = window;
        DataDir = dataDir;
    }

    public MainWindow Window { get; }
    public string DataDir { get; }
    public MainViewModel Vm => (MainViewModel)Window.DataContext!;
    public DocumentViewModel Doc => Vm.ActiveDocument!;
    public MarkupViewModel Markup => Vm.Markup;
    public PdfViewer Viewer => All<PdfViewer>().First();

    public static string NewPdf(TestPdf.Options? options = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfreader-ui-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, TestPdf.Create(options ?? new TestPdf.Options { Pages = SampleText, Outline = true, UriLink = true }));
        return path;
    }

    public static async Task<AppSession> StartAsync(string? pdf = null, Action<AppSettings>? configure = null, AppState? state = null, string? dataDir = null)
    {
        var dir = dataDir ?? Path.Combine(Path.GetTempPath(), $"pdfreader-ui-data-{Guid.NewGuid():N}");
        var store = new JsonStore(dir);
        var settings = dataDir is null ? new AppSettings() : store.LoadSettings();
        settings.CheckForUpdatesOnStartup = false;
        if (dataDir is null)
            settings.RestoreSession = false;
        configure?.Invoke(settings);
        var window = new MainWindow(store, settings, state ?? (dataDir is null ? new AppState() : store.LoadState()),
            new StartupOptions(pdf is null ? [] : [pdf], null))
        {
            Width = 1280,
            Height = 860,
        };
        window.Show();
        var session = new AppSession(window, dir);
        await session.Settle(200);
        if (pdf is not null)
            await session.WaitSharpAsync();
        return session;
    }

    // --- Waiting -----------------------------------------------------------------------------

    public static async Task Until(Func<bool> condition, string what, int timeoutMs = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"Timed out waiting for: {what}");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    public async Task WaitSharpAsync()
    {
        await Until(() => Vm.ActiveDocument is not null, "document open");
        await Until(() => All<PdfViewer>().FirstOrDefault()?.IsSharp == true, "page rendered");
    }

    /// <summary>Lets queued UI work and PDFium work finish.</summary>
    public async Task Settle(int ms = 120)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    // --- Finding controls --------------------------------------------------------------------

    public IEnumerable<T> All<T>() where T : Visual => Window.GetVisualDescendants().OfType<T>();

    public static string? NameOf(Control c) => AutomationProperties.GetName(c) is { Length: > 0 } n ? n : c is ContentControl { Content: string s } ? s : null;

    /// <summary>Visible to a user: effectively visible and not inside a collapsed (zero-size, clipped) container.</summary>
    public static bool IsShown(Control c)
    {
        if (!c.IsEffectivelyVisible || c.Bounds.Width < 1)
            return false;
        for (var v = c.GetVisualParent(); v is not null; v = v.GetVisualParent())
            if (v is Control { ClipToBounds: true } p && (p.Bounds.Width < 1 || p.Bounds.Height < 1))
                return false;
        return true;
    }

    public Button Button(string name) => All<Button>().FirstOrDefault(b => NameOf(b) == name && IsShown(b))
        ?? throw new InvalidOperationException($"No visible button '{name}'");

    public bool HasButton(string name) => All<Button>().Any(b => NameOf(b) == name && IsShown(b));

    public bool HasText(string text) => All<TextBlock>().Any(t => t.Text == text && IsShown(t)) || PopupTexts().Contains(text);

    public T Named<T>(string name) where T : Control => All<T>().FirstOrDefault(c => NameOf(c) == name && IsShown(c))
        ?? throw new InvalidOperationException($"No visible {typeof(T).Name} '{name}'");

    // --- Input ---------------------------------------------------------------------------------

    public Point Center(Control c) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), Window)
        ?? throw new InvalidOperationException("Control is not in the window");

    public void Click(Point p, MouseButton button = MouseButton.Left, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Window.MouseMove(p, modifiers);
        Window.MouseDown(p, button, modifiers);
        Window.MouseUp(p, button, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    public void Click(Control c) => Click(Center(c));

    public void Click(string buttonName) => Click(Button(buttonName));

    public async Task Drag(Point from, Point to, int steps = 14)
    {
        Window.MouseMove(from);
        Window.MouseDown(from, MouseButton.Left);
        for (var i = 1; i <= steps; i++)
        {
            Window.MouseMove(new Point(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps));
            await Settle(15);
        }
        Window.MouseUp(to, MouseButton.Left);
        await Settle(200);
    }

    public void Key(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Window.KeyPress(key, modifiers, PhysicalKey.None, null);
        Window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    public void Type(string text)
    {
        Window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Texts inside open popups (flyouts, menus, tooltips live in their own top levels).</summary>
    public IEnumerable<string> PopupTexts() => Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(Window) is { } layer
        ? layer.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")
        : [];

    /// <summary>Opens a button's flyout menu and invokes one of its items, like clicking it.</summary>
    public void ChooseMenu(Button owner, string header)
    {
        var menu = (MenuFlyout)owner.Flyout!;
        menu.ShowAt(owner);
        Dispatcher.UIThread.RunJobs();
        ChooseMenu(menu, header);
        menu.Hide();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Invokes an item of an open menu.</summary>
    public static void ChooseMenu(MenuFlyout menu, string header)
    {
        var item = Flatten(menu.Items.OfType<MenuItem>()).FirstOrDefault(m => m.Header as string == header)
                   ?? throw new InvalidOperationException($"No menu item '{header}' (have: {string.Join(", ", Flatten(menu.Items.OfType<MenuItem>()).Select(m => m.Header))})");
        if (item.Command is { } command)
            command.Execute(item.CommandParameter);
        else
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        if (menu.IsOpen)
            menu.Hide(); // a real click closes the menu; a raised event doesn't
        Dispatcher.UIThread.RunJobs();
    }

    public static IEnumerable<MenuItem> Flatten(IEnumerable<MenuItem> items)
        => items.SelectMany(i => new[] { i }.Concat(Flatten(i.Items.OfType<MenuItem>())));

    public void ChooseTool(string group, string tool) => ChooseMenu(Button(group), tool);

    // --- Page geometry ---------------------------------------------------------------------------

    /// <summary>Window position of a point on a page (page points, top-left origin).</summary>
    public Point PagePoint(double x, double y, int page = -1)
    {
        var p = Viewer.PageToViewer(page < 0 ? Doc.CurrentPage : page, x, y);
        return Viewer.TranslatePoint(p, Window) ?? throw new InvalidOperationException();
    }

    /// <summary>Vertical middle of the text line of a TestPdf page (24pt Helvetica, baseline 700).</summary>
    public const double TextY = 84;

    public Task<PdfAnnotationHit?> AnnotationAt(double x, double y, int page = 0) => Doc.Document.GetAnnotationAtAsync(page, x, y);

    // --- Pixels ----------------------------------------------------------------------------------

    public unsafe uint PixelAt(Point windowPoint)
    {
        var frame = Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame");
        using var buffer = frame.Lock();
        var scaling = frame.PixelSize.Width / Window.Bounds.Width;
        var x = (int)(windowPoint.X * scaling);
        var y = (int)(windowPoint.Y * scaling);
        return ((uint*)buffer.Address)[y * (buffer.RowBytes / 4) + x];
    }
}
