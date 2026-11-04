using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using PdfReader.App.Controls;
using PdfReader.App.Localization;
using PdfReader.App.Services;
using PdfReader.App.ViewModels;
using PdfReader.Core.Settings;

namespace PdfReader.App.Views;

public sealed partial class MainWindow : Window, IAppShell
{
    private readonly MainViewModel _vm;
    private readonly StartupOptions _options;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;

    // Parameterless constructor for the XAML previewer only.
    public MainWindow() : this(new JsonStore(Path.GetTempPath()), new AppSettings(), new AppState(), new StartupOptions([], null)) { }

    public MainWindow(JsonStore store, AppSettings settings, AppState state, StartupOptions options)
    {
        _options = options;
        _vm = new MainViewModel(store, settings, state, this);
        DataContext = _vm;
        InitializeComponent();
        ApplyTheme(settings.Theme);

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        _vm.ReadAloud.Problem += key => Dispatcher.UIThread.Post(() => _ = _vm.ShowMessageAsync(Loc.Get("ReadAloud"), Loc.Get(key)));
        Opened += OnOpened;
        Closing += OnClosing;
        _vm.PropertyChanged += (_, e) =>
        {
            // Keep the docked tools panel's enabled states current.
            if (e.PropertyName is nameof(MainViewModel.ActiveDocument) or nameof(MainViewModel.HasDocuments) or nameof(MainViewModel.IsToolsPanelOpen))
                this.GetVisualDescendantsOfType<ToolsPanel>().FirstOrDefault()?.RefreshAvailability();
        };
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        HideDrawnTitle();
        PerfLog.Start(this);

        if (_options.BenchmarkOutput is { } output)
        {
            await RunBenchmarkAsync(output);
            return;
        }

        if (_options.DiagnoseOutput is { } reportPath)
        {
            await RunDiagnosisAsync(reportPath);
            return;
        }

        SingleInstance.StartServer(files => Dispatcher.UIThread.Post(async () =>
        {
            ActivateWindow();
            await _vm.OpenFilesAsync(files);
        }));

        if (_options.Files.Count > 0)
            await _vm.OpenFilesAsync(_options.Files);
        else
            await _vm.RestoreSessionAsync();

        if (_vm.Settings.CheckForUpdatesOnStartup)
        {
            // Wait until startup work has settled; the check is the app's only network traffic.
            await Task.Delay(TimeSpan.FromSeconds(5));
            _ = _vm.Updates.CheckAsync(userInitiated: false);
        }
    }

    /// <summary>
    /// Avalonia draws the caption overlay (title + window buttons) in a host layer outside the
    /// window's styles. Our top bar shows tabs where the title would be, so hide just the title.
    /// </summary>
    private void HideDrawnTitle()
    {
        Visual root = this;
        while (Avalonia.VisualTree.VisualExtensions.GetVisualParent(root) is { } parent)
            root = parent;
        // The extra Fullscreen caption button would sit on top of our Page colors / Settings
        // buttons; reading mode (F11) covers full screen.
        foreach (var visual in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root))
            if (visual is Control { Name: "PART_TitleTextPanel" or "PART_FullScreenButton" } part)
                part.IsVisible = false;
    }

    public void SaveAll()
    {
        _vm.ReadAloud.Stop();
        _vm.SaveState();
        PerfLog.Summary();
    }

    private void ActivateWindow()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    // ---------------------------------------------------------------------------------------
    // Keyboard
    // ---------------------------------------------------------------------------------------

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_vm.CloseTopmost())
                e.Handled = true;
            return;
        }

        // Let text boxes keep their own editing keys (typing, Ctrl+C/V/A/Z, arrows...).
        if (FocusManager?.GetFocusedElement() is TextBox && IsTextEditingKey(e))
            return;
        // Overlays with their own keyboard handling.
        if (_vm.Dialog is not null || _vm.IsCommandPaletteOpen)
            return;

        foreach (var command in _vm.Commands)
        {
            if (command.Gestures.Any(g => g.Matches(e)))
            {
                if (command.CanExecute())
                    command.Execute();
                e.Handled = true;
                return;
            }
        }
    }

    private static bool IsTextEditingKey(KeyEventArgs e)
    {
        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        var alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;
        var isFunctionKey = e.Key is >= Key.F1 and <= Key.F24;
        if (!ctrl && !alt)
            return !isFunctionKey;
        return ctrl && e.Key is Key.C or Key.V or Key.X or Key.A or Key.Z or Key.Y or Key.Left or Key.Right or Key.Back or Key.Delete or Key.Home or Key.End;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.ActiveDocument is { } doc)
        {
            doc.NextHit((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : +1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _vm.ActiveDocument is { SearchText.Length: > 0 } d)
        {
            d.ClearSearch();
            e.Handled = true;
        }
    }

    private void OnPrevHit(object? sender, RoutedEventArgs e) => _vm.ActiveDocument?.NextHit(-1);
    private void OnNextHit(object? sender, RoutedEventArgs e) => _vm.ActiveDocument?.NextHit(+1);
    private void OnExitClick(object? sender, RoutedEventArgs e) => CloseWindow();

    /// <summary>Fills File → Open recent each time the menu opens.</summary>
    private void OnFileMenuOpening(object? sender, EventArgs e)
    {
        RecentMenu.Items.Clear();
        foreach (var recent in _vm.RecentFiles.Take(10))
        {
            var item = new MenuItem { Header = recent.Name, Command = recent.OpenCommand };
            ToolTip.SetTip(item, recent.Path);
            RecentMenu.Items.Add(item);
        }
        if (RecentMenu.Items.Count == 0)
            RecentMenu.Items.Add(new MenuItem { Header = Loc.Get("NoRecent"), IsEnabled = false });
    }

    private bool _closeConfirmed;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_closeConfirmed && _vm.Documents.Any(d => d.IsModified))
        {
            e.Cancel = true;
            if (await _vm.ConfirmCloseAllAsync())
            {
                _closeConfirmed = true;
                Close();
            }
            return;
        }
        SaveAll();
    }
    private void OnSettingsClick(object? sender, RoutedEventArgs e) => _vm.OpenSettings();
    private void OnStopReading(object? sender, RoutedEventArgs e) => _vm.ReadAloud.Stop();

    // ---------------------------------------------------------------------------------------
    // Drag & drop
    // ---------------------------------------------------------------------------------------

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
            return;
        var paths = files.Select(f => f.TryGetLocalPath())
            .OfType<string>()
            .Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .ToList();
        await _vm.OpenFilesAsync(paths);
    }

    // ---------------------------------------------------------------------------------------
    // IAppShell

    Avalonia.Controls.TopLevel IAppShell.Window => this;
    // ---------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> PickPdfFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(Loc.Get("PdfFiles")) { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] }],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickSavePathAsync(string suggestedFileName)
    {
        if (PickSavePathOverride is { } pick)
            return pick(suggestedFileName);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "pdf",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(Loc.Get("PdfFiles")) { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] }],
        });
        return file?.TryGetLocalPath();
    }

    public void CloseWindow() => Close();

    /// <summary>Test hooks: native file dialogs cannot open in headless UI tests.</summary>
    internal Func<string?>? PickFileOverride { get; set; }
    internal Func<string, string?>? PickSavePathOverride { get; set; }

    public async Task<string?> PickAnyFileAsync()
    {
        if (PickFileOverride is { } pick)
            return pick();
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, Title = Loc.Get("ToolAttach") });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task CopyToClipboardAsync(string text)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    public Task<PrintJob?> ShowPrintDialogAsync(int pageCount, int currentPage)
        => PrintDialog.ShowAsync(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, pageCount);

    public void SetFullScreen(bool fullScreen)
    {
        if (fullScreen)
        {
            _stateBeforeFullScreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
        else if (WindowState == WindowState.FullScreen)
        {
            WindowState = _stateBeforeFullScreen;
        }
    }

    public void FocusSearch()
    {
        _vm.IsReadingMode = false;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void ApplyTheme(AppTheme theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    // ---------------------------------------------------------------------------------------
    // CI startup benchmark: --benchmark out.json file.pdf
    // ---------------------------------------------------------------------------------------

    private async Task RunBenchmarkAsync(string output)
    {
        var start = Process.GetCurrentProcess().StartTime;
        var windowMs = (DateTime.Now - start).TotalMilliseconds;
        double firstPageMs = -1;

        if (_options.Files.Count > 0)
        {
            var rendered = new TaskCompletionSource();
            void Hook(Visual root)
            {
                foreach (var viewer in root.GetVisualDescendantsOfType<PdfViewer>())
                    viewer.FirstPageRendered += () => rendered.TrySetResult();
            }

            var doc = await _vm.OpenFileAsync(_options.Files[0]);
            if (doc is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => Hook(this), DispatcherPriority.Loaded);
                await Task.WhenAny(rendered.Task, Task.Delay(15000));
                if (rendered.Task.IsCompleted)
                    firstPageMs = (DateTime.Now - start).TotalMilliseconds;
            }
        }

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var result = new Dictionary<string, double>
        {
            ["windowShownMs"] = Math.Round(windowMs),
            ["firstPageMs"] = Math.Round(firstPageMs),
            ["workingSetMb"] = Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 1),
        };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, BenchmarkJsonContext.Default.DictionaryStringDouble));
        Close();
    }

    // ---------------------------------------------------------------------------------------
    // Unattended self-diagnosis: --diagnose report.txt
    // ---------------------------------------------------------------------------------------

    private async Task RunDiagnosisAsync(string reportPath)
    {
        string report;
        try
        {
            (report, _) = await new SelfDiagnostics(_vm, this).RunAsync();
        }
        catch (Exception ex)
        {
            report = $"Result: 0 passed, 0 warnings, 1 failed{Environment.NewLine}Self-diagnosis crashed: {ex}";
        }
        var full = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, report);
        var exitCode = System.Text.RegularExpressions.Regex.IsMatch(report, @"warnings, 0 failed") ? 0 : 1;
        _closeConfirmed = true; // the sample is already closed; never prompt in an unattended run
        // Avalonia sets the process exit code from Shutdown(), so pass it there.
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown(exitCode);
        else
            Close();
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, double>))]
internal sealed partial class BenchmarkJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

internal static class VisualTreeHelpers
{
    public static IEnumerable<T> GetVisualDescendantsOfType<T>(this Visual root) where T : Visual
    {
        foreach (var child in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root))
            if (child is T match)
                yield return match;
    }
}
