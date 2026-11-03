using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfReader.App.Localization;
using PdfReader.App.Services;
using PdfReader.Core.Settings;
using PdfReader.Rendering;

namespace PdfReader.App.ViewModels;

/// <summary>Non-generic base so XAML item templates can bind to Label with compiled bindings.</summary>
public abstract record ChoiceBase(string Label);

public sealed record Choice<T>(T Value, string Label) : ChoiceBase(Label);

/// <summary>Three pages only: General, Appearance, Updates. Every change saves immediately.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly string _initialLanguage;
    private readonly bool _ready; // property setters in the constructor must not trigger saves

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        _initialLanguage = main.Settings.Language;
        Languages =
        [
            new("system", Loc.Get("LanguageSystem")),
            new("en", "English"),
            new("de", "Deutsch"),
        ];
        ZoomModes =
        [
            new(ZoomMode.FitWidth, Loc.Get("FitWidth")),
            new(ZoomMode.FitPage, Loc.Get("FitPage")),
            new(ZoomMode.ActualSize, Loc.Get("ActualSize")),
        ];
        SelectedLanguage = Languages.FirstOrDefault(l => l.Value == main.Settings.Language) ?? Languages[0];
        SelectedZoom = ZoomModes.FirstOrDefault(z => z.Value == main.Settings.DefaultZoom) ?? ZoomModes[0];
        CustomForeground = main.Settings.CustomForeground;
        CustomBackground = main.Settings.CustomBackground;
        _ready = true;
    }

    private AppSettings S => _main.Settings;
    public UpdateService Updates => _main.Updates;

    [ObservableProperty] public partial int SelectedPage { get; set; }
    public bool IsGeneralPage { get => SelectedPage == 0; set { if (value) SelectedPage = 0; } }
    public bool IsAppearancePage { get => SelectedPage == 1; set { if (value) SelectedPage = 1; } }
    public bool IsUpdatesPage { get => SelectedPage == 2; set { if (value) SelectedPage = 2; } }

    partial void OnSelectedPageChanged(int value)
    {
        OnPropertyChanged(nameof(IsGeneralPage));
        OnPropertyChanged(nameof(IsAppearancePage));
        OnPropertyChanged(nameof(IsUpdatesPage));
    }

    // --- General -------------------------------------------------------------------------

    public IReadOnlyList<Choice<string>> Languages { get; }
    [ObservableProperty] public partial Choice<string> SelectedLanguage { get; set; }
    public bool LanguageChanged => SelectedLanguage.Value != _initialLanguage;

    partial void OnSelectedLanguageChanged(Choice<string> value)
    {
        if (value is null || !_ready)
            return;
        S.Language = value.Value;
        _main.SaveSettings();
        OnPropertyChanged(nameof(LanguageChanged));
    }

    public bool DeveloperMode
    {
        get => S.DeveloperMode;
        set { S.DeveloperMode = value; _main.SaveSettings(); OnPropertyChanged(); }
    }

    [RelayCommand]
    private Task RunDiagnostics()
    {
        _main.IsSettingsOpen = false;
        return _main.RunSelfDiagnosisAsync();
    }

    [RelayCommand]
    private void OpenDiagnosticsFolder() => Services.SelfDiagnostics.OpenReportFolder();

    public bool RestoreSession
    {
        get => S.RestoreSession;
        set { S.RestoreSession = value; _main.SaveSettings(); OnPropertyChanged(); }
    }

    public IReadOnlyList<Choice<ZoomMode>> ZoomModes { get; }
    [ObservableProperty] public partial Choice<ZoomMode> SelectedZoom { get; set; }

    partial void OnSelectedZoomChanged(Choice<ZoomMode> value)
    {
        if (value is null || !_ready)
            return;
        S.DefaultZoom = value.Value;
        _main.SaveSettings();
    }

    // --- Appearance ------------------------------------------------------------------------

    public bool ThemeSystem { get => S.Theme == AppTheme.System; set { if (value) SetTheme(AppTheme.System); } }
    public bool ThemeLight { get => S.Theme == AppTheme.Light; set { if (value) SetTheme(AppTheme.Light); } }
    public bool ThemeDark { get => S.Theme == AppTheme.Dark; set { if (value) SetTheme(AppTheme.Dark); } }

    private void SetTheme(AppTheme theme)
    {
        S.Theme = theme;
        _main.SaveSettings();
        _main.ApplyTheme();
        OnPropertyChanged(nameof(ThemeSystem));
        OnPropertyChanged(nameof(ThemeLight));
        OnPropertyChanged(nameof(ThemeDark));
    }

    public bool PageNormal { get => S.PageColorMode == PageColorMode.Normal; set { if (value) SetPageMode(PageColorMode.Normal); } }
    public bool PageDark { get => S.PageColorMode == PageColorMode.Dark; set { if (value) SetPageMode(PageColorMode.Dark); } }
    public bool PageSepia { get => S.PageColorMode == PageColorMode.Sepia; set { if (value) SetPageMode(PageColorMode.Sepia); } }
    public bool PageCustom { get => S.PageColorMode == PageColorMode.Custom; set { if (value) SetPageMode(PageColorMode.Custom); } }

    private void SetPageMode(PageColorMode mode)
    {
        S.PageColorMode = mode;
        _main.SaveSettings();
        _main.RefreshAllPageColors();
        NotifyPageModes();
    }

    public void NotifyPageModes()
    {
        OnPropertyChanged(nameof(PageNormal));
        OnPropertyChanged(nameof(PageDark));
        OnPropertyChanged(nameof(PageSepia));
        OnPropertyChanged(nameof(PageCustom));
    }

    public bool KeepImagesNatural
    {
        get => S.KeepImagesNatural;
        set
        {
            S.KeepImagesNatural = value;
            _main.SaveSettings();
            _main.RefreshAllPageColors();
            OnPropertyChanged();
        }
    }

    [ObservableProperty] public partial string CustomForeground { get; set; }
    [ObservableProperty] public partial string CustomBackground { get; set; }

    partial void OnCustomForegroundChanged(string value) => ApplyCustomColor(value, foreground: true);
    partial void OnCustomBackgroundChanged(string value) => ApplyCustomColor(value, foreground: false);

    private void ApplyCustomColor(string value, bool foreground)
    {
        if (!_ready || !ColorParser.TryParse(value, out var argb))
            return;
        if (foreground)
            S.CustomForeground = ColorParser.ToHex(argb);
        else
            S.CustomBackground = ColorParser.ToHex(argb);
        _main.SaveSettings();
        _main.RefreshAllPageColors();
    }

    /// <summary>Quick picks for the custom theme: (text, background).</summary>
    public IReadOnlyList<CustomColorPreset> Presets { get; } =
    [
        new("#E8E2D0", "#26303A"),
        new("#D6E4D3", "#1E2B22"),
        new("#1B1B1B", "#E8F0E0"),
        new("#FFD27F", "#101010"),
        new("#1A2A44", "#DCE8F5"),
    ];

    [RelayCommand]
    private void ApplyPreset(CustomColorPreset preset)
    {
        CustomForeground = preset.Foreground;
        CustomBackground = preset.Background;
        SetPageMode(PageColorMode.Custom);
    }

    // --- Updates -------------------------------------------------------------------------

    public bool CheckOnStartup
    {
        get => S.CheckForUpdatesOnStartup;
        set { S.CheckForUpdatesOnStartup = value; _main.SaveSettings(); OnPropertyChanged(); }
    }

    public bool ChannelStable { get => S.UpdateChannel == UpdateChannel.Stable; set { if (value) SetChannel(UpdateChannel.Stable); } }
    public bool ChannelBeta { get => S.UpdateChannel == UpdateChannel.Beta; set { if (value) SetChannel(UpdateChannel.Beta); } }

    private void SetChannel(UpdateChannel channel)
    {
        S.UpdateChannel = channel;
        _main.SaveSettings();
        OnPropertyChanged(nameof(ChannelStable));
        OnPropertyChanged(nameof(ChannelBeta));
    }

    [RelayCommand] private Task CheckForUpdates() => Updates.CheckAsync(userInitiated: true);
    [RelayCommand] private void RestartToInstall() => Updates.RestartToInstall();
    [RelayCommand] private void Close() => _main.IsSettingsOpen = false;
}

public sealed record CustomColorPreset(string Foreground, string Background)
{
    public Avalonia.Media.IBrush ForegroundBrush => Avalonia.Media.Brush.Parse(Foreground);
    public Avalonia.Media.IBrush BackgroundBrush => Avalonia.Media.Brush.Parse(Background);
}
