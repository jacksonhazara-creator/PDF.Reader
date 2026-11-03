using System.Collections.ObjectModel;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfReader.App.Localization;
using PdfReader.Core.Commands;

namespace PdfReader.App.ViewModels;

/// <summary>An action reachable from shortcuts, the command palette and the All tools panel.</summary>
public sealed class AppCommand
{
    public AppCommand(string id, string titleKey, string iconKey, Action execute, Func<bool>? canExecute = null, params string[] gestures)
    {
        Id = id;
        TitleKey = titleKey;
        IconKey = iconKey;
        Execute = execute;
        CanExecute = canExecute ?? (static () => true);
        Gestures = gestures.Select(KeyGesture.Parse).ToArray();
        GestureText = gestures.Length == 0 ? "" : LocalizeGesture(gestures[0]);
    }

    public string Id { get; }
    public string TitleKey { get; }
    public string Title => Loc.Get(TitleKey);
    public string IconKey { get; }
    public Action Execute { get; }
    public Func<bool> CanExecute { get; }
    public IReadOnlyList<KeyGesture> Gestures { get; }
    public string GestureText { get; }
    public override string ToString() => Title;

    public object? Icon => Avalonia.Application.Current?.TryGetResource(IconKey, null, out var g) == true ? g : null;

    private static string LocalizeGesture(string gesture)
    {
        var text = gesture.Replace("OemPlus", "+").Replace("OemMinus", "-").Replace("OemComma", ",").Replace("D0", "0").Replace("D1", "1").Replace("D2", "2");
        if (Loc.Language == "de")
            text = text.Replace("Ctrl", "Strg").Replace("Shift", "Umschalt");
        return text;
    }
}

public sealed record ToolGroup(string TitleKey, IBrush Color, IReadOnlyList<AppCommand> Tools)
{
    public string Title => Loc.Get(TitleKey);
}

public sealed partial class CommandPaletteViewModel(Func<IReadOnlyList<AppCommand>> commands, Action close) : ObservableObject
{
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial AppCommand? SelectedCommand { get; set; }
    public ObservableCollection<AppCommand> Results { get; } = [];
    public bool HasNoResults => Results.Count == 0;

    partial void OnQueryChanged(string value) => Refresh();

    public void Refresh()
    {
        var query = Query;
        var matches = commands()
            .Where(c => c.CanExecute())
            .Select(c => (Command: c, Score: CommandMatcher.Score(c.Title, query)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Command)
            .Take(30)
            .ToList();
        Results.Clear();
        foreach (var m in matches)
            Results.Add(m);
        SelectedCommand = Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoResults));
    }

    public void Move(int delta)
    {
        if (Results.Count == 0)
            return;
        var index = SelectedCommand is null ? -1 : Results.IndexOf(SelectedCommand);
        SelectedCommand = Results[(index + delta + Results.Count) % Results.Count];
    }

    [RelayCommand]
    public void Run(AppCommand? command = null)
    {
        command ??= SelectedCommand;
        close();
        if (command is not null && command.CanExecute())
            command.Execute();
    }
}

/// <summary>An in-window modal dialog (message, confirmation or text input).</summary>
public sealed partial class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _choice = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required string Title { get; init; }
    public required string Message { get; init; }
    public string PrimaryText { get; init; } = Loc.Get("OK");
    public string? SecondaryText { get; init; }
    public bool HasSecondary => SecondaryText is not null;
    /// <summary>Optional third button (e.g. Cancel in Save / Don't save / Cancel).</summary>
    public string? TertiaryText { get; init; }
    public bool HasTertiary => TertiaryText is not null;
    public bool HasInput { get; init; }
    /// <summary>Long monospace text in a scroll area (self-diagnosis report).</summary>
    public bool IsReport { get; init; }
    public double DialogWidth => IsReport ? 680 : 440;
    public double MessageMaxHeight => IsReport ? 460 : double.PositiveInfinity;
    public Avalonia.Media.FontFamily MessageFont => IsReport ? new("Cascadia Mono, Consolas, Courier New") : Avalonia.Media.FontFamily.Default;
    public double MessageFontSize => IsReport ? 12 : 14;
    public bool IsPassword { get; init; }
    public char PasswordChar => IsPassword ? '●' : '\0';

    [ObservableProperty] public partial string InputText { get; set; } = "";
    [ObservableProperty] public partial string? ErrorText { get; set; }
    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public Task<bool> Result => _result.Task;
    /// <summary>0 = primary, 1 = secondary, 2 = tertiary.</summary>
    public Task<int> Choice => _choice.Task;

    [RelayCommand] public void Primary() => Complete(0);
    [RelayCommand] public void Secondary() => Complete(1);
    [RelayCommand] public void Tertiary() => Complete(2);

    /// <summary>Esc: the safest choice (Cancel when there is one).</summary>
    public void Dismiss() => Complete(HasTertiary ? 2 : 1);

    private void Complete(int choice)
    {
        _choice.TrySetResult(choice);
        _result.TrySetResult(choice == 0);
    }
}

public sealed partial class RecentFileViewModel(MainViewModel main, string path, int pageCount, DateTime lastOpenedUtc) : ObservableObject
{
    private Bitmap? _thumbnail;
    private bool _loaded;

    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path);
    public string Folder { get; } = System.IO.Path.GetDirectoryName(path) ?? "";
    public string Details { get; } = (pageCount == 1 ? Loc.Get("OnePage") : Loc.Format("Pages", pageCount)) + " · " + lastOpenedUtc.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture);

    public Bitmap? Thumbnail
    {
        get
        {
            if (!_loaded)
            {
                _loaded = true;
                _ = LoadAsync();
            }
            return _thumbnail;
        }
    }

    private async Task LoadAsync()
    {
        var file = Core.Settings.AppPaths.ThumbnailPathFor(Path);
        try
        {
            _thumbnail = await Task.Run(() => File.Exists(file) ? new Bitmap(file) : null);
            OnPropertyChanged(nameof(Thumbnail));
        }
        catch (Exception) { /* cache miss or corrupt thumbnail: show the placeholder */ }
    }

    [RelayCommand] private Task Open() => main.OpenFileAsync(Path);
    [RelayCommand] private void Remove() => main.RemoveRecent(this);
}
