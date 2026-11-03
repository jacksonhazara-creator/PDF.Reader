using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfReader.App.Localization;
using PdfReader.App.Services;
using PdfReader.Core.Settings;
using PdfReader.Rendering;

namespace PdfReader.App.ViewModels;

/// <summary>What the view layer provides to the view models (implemented by MainWindow).</summary>
public interface IAppShell
{
    Task<IReadOnlyList<string>> PickPdfFilesAsync();
    Task<string?> PickSavePathAsync(string suggestedFileName);
    void CloseWindow();
    Task<string?> PickAnyFileAsync();
    Task CopyToClipboardAsync(string text);
    Task<PrintJob?> ShowPrintDialogAsync(int pageCount, int currentPage);
    void SetFullScreen(bool fullScreen);
    void FocusSearch();
    void ApplyTheme(AppTheme theme);
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly JsonStore _store;
    private readonly IAppShell _shell;
    private CancellationTokenSource? _toastCts;

    public MainViewModel(JsonStore store, AppSettings settings, AppState state, IAppShell shell)
    {
        _store = store;
        _shell = shell;
        Settings = settings;
        State = state;
        ReadAloud = new ReadAloudService();
        Updates = new UpdateService(settings);
        SettingsPage = new SettingsViewModel(this);
        Markup = new MarkupViewModel(this);
        Commands = BuildCommands();
        Palette = new CommandPaletteViewModel(() => Commands, () => IsCommandPaletteOpen = false);
        ToolGroups = BuildToolGroups();
        Documents.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDocuments));
        RefreshRecentFiles();
    }

    public AppSettings Settings { get; }
    public AppState State { get; }
    public ReadAloudService ReadAloud { get; }
    public UpdateService Updates { get; }
    public SettingsViewModel SettingsPage { get; }
    public MarkupViewModel Markup { get; }
    public CommandPaletteViewModel Palette { get; }
    public IReadOnlyList<AppCommand> Commands { get; }
    public IReadOnlyList<ToolGroup> ToolGroups { get; }

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];
    public ObservableCollection<RecentFileViewModel> RecentFiles { get; } = [];
    public bool HasDocuments => Documents.Count > 0;
    public bool HasRecentFiles => RecentFiles.Count > 0;

    [ObservableProperty] public partial DocumentViewModel? ActiveDocument { get; set; }
    [ObservableProperty] public partial bool IsToolsPanelOpen { get; set; }
    [ObservableProperty] public partial bool IsSettingsOpen { get; set; }
    [ObservableProperty] public partial bool IsCommandPaletteOpen { get; set; }
    [ObservableProperty] public partial bool IsReadingMode { get; set; }
    [ObservableProperty] public partial DialogViewModel? Dialog { get; set; }
    [ObservableProperty] public partial string? Toast { get; set; }
    [ObservableProperty] public partial string? ProgressText { get; set; }

    public bool IsChromeVisible => !IsReadingMode;

    [ObservableProperty] public partial SignatureDialogViewModel? SignatureDialog { get; set; }

    partial void OnIsReadingModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsChromeVisible));
        _shell.SetFullScreen(value);
    }

    partial void OnIsCommandPaletteOpenChanged(bool value)
    {
        if (value)
        {
            IsToolsPanelOpen = false;
            Palette.Query = "";
            Palette.Refresh();
        }
    }

    partial void OnActiveDocumentChanged(DocumentViewModel? value)
    {
        NotifyPageColors();
        SaveState();
    }

    // ---------------------------------------------------------------------------------------
    // Opening & closing
    // ---------------------------------------------------------------------------------------

    [RelayCommand]
    public async Task OpenDialogAsync()
    {
        var files = await _shell.PickPdfFilesAsync();
        await OpenFilesAsync(files);
    }

    public async Task OpenFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            await OpenFileAsync(path);
    }

    public async Task<DocumentViewModel?> OpenFileAsync(string path, bool activate = true)
    {
        try { path = Path.GetFullPath(path); }
        catch (Exception) { return null; }

        var existing = Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActiveDocument = existing;
            return existing;
        }

        string? password = null;
        PdfDocument? document = null;
        while (document is null)
        {
            try
            {
                document = await PdfDocument.OpenAsync(path, password);
            }
            catch (PdfException ex) when (ex.Error == PdfError.PasswordRequired)
            {
                password = await AskPasswordAsync(path, retry: password is not null);
                if (password is null)
                    return null;
            }
            catch (PdfException ex)
            {
                if (ex.Error == PdfError.FileNotFound)
                    RemoveRecent(path);
                await ShowMessageAsync(Loc.Get("CannotOpenTitle"), $"{Path.GetFileName(path)}\n\n{ex.Message}");
                return null;
            }
        }

        var vm = new DocumentViewModel(this, document, path, State.GetDocumentState(path), password);
        Documents.Add(vm);
        if (activate || ActiveDocument is null)
            ActiveDocument = vm;

        State.AddRecent(path, document.PageCount);
        RefreshRecentFiles();
        SaveState();
        _ = vm.ScanSecurityAsync();
        _ = RecentThumbnails.EnsureAsync(document, path);
        return vm;
    }

    private async Task<string?> AskPasswordAsync(string path, bool retry)
    {
        var dialog = new DialogViewModel
        {
            Title = Loc.Get("PasswordTitle"),
            Message = Loc.Format("PasswordMessage", Path.GetFileName(path)),
            PrimaryText = Loc.Get("Open"),
            SecondaryText = Loc.Get("Cancel"),
            HasInput = true,
            IsPassword = true,
            ErrorText = retry ? Loc.Get("PasswordWrong") : null,
        };
        return await ShowDialogAsync(dialog) ? dialog.InputText : null;
    }

    [RelayCommand]
    public async Task CloseDocumentAsync(DocumentViewModel? document) => await TryCloseDocumentAsync(document);

    /// <returns>false when the user cancelled because of unsaved changes.</returns>
    public async Task<bool> TryCloseDocumentAsync(DocumentViewModel? document)
    {
        document ??= ActiveDocument;
        if (document is null)
            return true;
        if (document.IsModified && !await ConfirmUnsavedAsync(document))
            return false;

        document.SaveViewState(State.GetDocumentState(document.FilePath));
        var index = Documents.IndexOf(document);
        Documents.Remove(document);
        if (ActiveDocument == document)
            ActiveDocument = Documents.Count == 0 ? null : Documents[Math.Clamp(index, 0, Documents.Count - 1)];
        if (Documents.Count == 0)
            IsReadingMode = false;
        SaveState();
        await document.DisposeAsync();
        return true;
    }

    /// <summary>"Save changes?" → Save / Don't save / Cancel. Returns false for Cancel (or a failed save).</summary>
    private async Task<bool> ConfirmUnsavedAsync(DocumentViewModel document)
    {
        ActiveDocument = document;
        var dialog = new DialogViewModel
        {
            Title = Loc.Get("UnsavedTitle"),
            Message = Loc.Format("UnsavedMessage", document.Title),
            PrimaryText = Loc.Get("Save"),
            SecondaryText = Loc.Get("DontSave"),
            TertiaryText = Loc.Get("Cancel"),
        };
        await ShowDialogAsync(dialog);
        return await dialog.Choice switch
        {
            0 => await SaveAsync(document, saveAs: false),
            1 => true,
            _ => false,
        };
    }

    /// <summary>Before the window closes: resolve every document with unsaved changes.</summary>
    public async Task<bool> ConfirmCloseAllAsync()
    {
        foreach (var doc in Documents.Where(d => d.IsModified).ToList())
            if (!await ConfirmUnsavedAsync(doc))
                return false;
        return true;
    }

    [RelayCommand] public Task SaveActiveAsync() => ActiveDocument is { } d ? SaveAsync(d, saveAs: false) : Task.CompletedTask;
    [RelayCommand] public Task SaveActiveAsAsync() => ActiveDocument is { } d ? SaveAsync(d, saveAs: true) : Task.CompletedTask;

    /// <summary>Saves (in place, as an incremental update) or saves a copy under a new name and switches the tab to it.</summary>
    public async Task<bool> SaveAsync(DocumentViewModel document, bool saveAs)
    {
        var path = document.FilePath;
        if (saveAs)
        {
            if (await _shell.PickSavePathAsync(Path.GetFileName(document.FilePath)) is not { } chosen)
                return false;
            path = chosen;
        }

        try
        {
            ProgressText = Loc.Get("Saving");
            var edits = document.OutlineEdits.ToList();
            var password = document.Password;
            await document.Document.SaveAsync(path, edits.Count == 0 ? null : temp => Core.Bookmarks.OutlineEditor.Apply(temp, edits, password));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PdfException or ObjectDisposedException)
        {
            await ShowMessageAsync(Loc.Get("SaveFailed"), ex.Message);
            return false;
        }
        finally
        {
            ProgressText = null;
        }

        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(document.FilePath), StringComparison.OrdinalIgnoreCase))
        {
            await document.AfterSaveAsync();
        }
        else
        {
            // Continue in the new file, in the same tab and at the same page.
            var index = Documents.IndexOf(document);
            document.SaveViewState(State.GetDocumentState(path));
            Documents.Remove(document);
            await document.DisposeAsync();
            if (await OpenFileAsync(path) is { } reopened && index >= 0 && index < Documents.Count)
                Documents.Move(Documents.IndexOf(reopened), index);
        }
        ShowToast(Loc.Get("Saved"));
        return true;
    }

    public void CycleTabs(int direction)
    {
        if (Documents.Count < 2 || ActiveDocument is null)
            return;
        var index = Documents.IndexOf(ActiveDocument);
        ActiveDocument = Documents[(index + direction + Documents.Count) % Documents.Count];
    }

    public async Task RestoreSessionAsync()
    {
        if (!Settings.RestoreSession || State.Session.Count == 0)
            return;
        var tabs = State.Session.ToList();
        var activeIndex = State.SessionActiveIndex;
        DocumentViewModel? active = null;
        for (var i = 0; i < tabs.Count; i++)
        {
            if (!File.Exists(tabs[i].Path))
                continue;
            var vm = await OpenFileAsync(tabs[i].Path, activate: i == activeIndex);
            if (i == activeIndex)
                active = vm;
        }
        if (active is not null)
            ActiveDocument = active;
    }

    public void RemoveRecent(RecentFileViewModel item) => RemoveRecent(item.Path);

    public void RemoveRecent(string path)
    {
        State.RemoveRecent(path);
        RefreshRecentFiles();
        SaveState();
    }

    private void RefreshRecentFiles()
    {
        RecentFiles.Clear();
        foreach (var r in State.RecentFiles)
            RecentFiles.Add(new RecentFileViewModel(this, r.Path, r.PageCount, r.LastOpenedUtc));
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    public void SaveState()
    {
        foreach (var d in Documents)
            d.SaveViewState(State.GetDocumentState(d.FilePath));
        State.Session = Documents.Select(d => new SessionTab { Path = d.FilePath }).ToList();
        State.SessionActiveIndex = ActiveDocument is null ? 0 : Math.Max(0, Documents.IndexOf(ActiveDocument));
        _store.SaveState(State);
    }

    public void SaveSettings() => _store.SaveSettings(Settings);

    // ---------------------------------------------------------------------------------------
    // Dialogs, toasts, clipboard
    // ---------------------------------------------------------------------------------------

    public async Task<bool> ShowDialogAsync(DialogViewModel dialog)
    {
        Dialog = dialog;
        try { return await dialog.Result; }
        finally { if (Dialog == dialog) Dialog = null; }
    }

    public Task<bool> ShowMessageAsync(string title, string message)
        => ShowDialogAsync(new DialogViewModel { Title = title, Message = message });

    public async void ShowToast(string message)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        Toast = message;
        try
        {
            await Task.Delay(1800, cts.Token);
            Toast = null;
        }
        catch (OperationCanceledException) { }
    }

    public async Task CopyTextAsync(string text)
    {
        await _shell.CopyToClipboardAsync(text);
        ShowToast(Loc.Get("Copied"));
    }

    // ---------------------------------------------------------------------------------------
    // Links (never followed automatically)
    // ---------------------------------------------------------------------------------------

    public async Task ActivateLinkAsync(DocumentViewModel document, PdfLink link)
    {
        switch (link.Kind)
        {
            case LinkKind.GoToPage when link.PageIndex >= 0:
                document.GoToPage(link.PageIndex);
                break;

            case LinkKind.Uri when LinkPolicy.IsAllowedUri(link.Target, out var uri):
                var confirmed = await ShowDialogAsync(new DialogViewModel
                {
                    Title = Loc.Get("OpenLinkTitle"),
                    Message = Loc.Format("OpenLinkMessage", uri!.AbsoluteUri),
                    PrimaryText = Loc.Get("OpenLink"),
                    SecondaryText = Loc.Get("Cancel"),
                });
                if (confirmed)
                    LinkPolicy.Open(uri!);
                break;

            case LinkKind.Uri:
                await ShowMessageAsync(Loc.Get("BlockedLinkTitle"), Loc.Format("BlockedScheme", link.Target ?? ""));
                break;

            case LinkKind.Launch or LinkKind.RemoteFile:
                await ShowMessageAsync(Loc.Get("BlockedLinkTitle"), Loc.Format("BlockedLaunch", link.Target ?? ""));
                break;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Actions
    // ---------------------------------------------------------------------------------------

    [RelayCommand]
    public async Task PrintAsync()
    {
        if (ActiveDocument is not { } doc)
            return;
        var job = await _shell.ShowPrintDialogAsync(doc.PageCount, doc.CurrentPage);
        if (job is null)
            return;
        try
        {
            var total = job.Pages.Count;
            ProgressText = Loc.Format("Printing", 0, total);
            var progress = new Progress<int>(n => ProgressText = Loc.Format("Printing", n, total));
            await doc.Document.PrintAsync(job.DeviceContext, doc.Title, job.Pages, progress);
        }
        catch (Exception ex) when (ex is IOException or PdfException or ObjectDisposedException)
        {
            await ShowMessageAsync(Loc.Get("PrintFailed"), ex.Message);
        }
        finally
        {
            job.Dispose();
            ProgressText = null;
        }
    }

    public async Task GoToPagePromptAsync()
    {
        if (ActiveDocument is not { } doc)
            return;
        var dialog = new DialogViewModel
        {
            Title = Loc.Get("GoToPageTitle"),
            Message = Loc.Format("GoToPageMessage", doc.PageCount),
            PrimaryText = Loc.Get("OK"),
            SecondaryText = Loc.Get("Cancel"),
            HasInput = true,
            InputText = doc.CurrentPageNumber.ToString(System.Globalization.CultureInfo.CurrentCulture),
        };
        if (await ShowDialogAsync(dialog) && int.TryParse(dialog.InputText.Trim(), out var page))
            doc.GoToPage(page - 1);
    }

    public void ToggleReadAloud()
    {
        if (ReadAloud.IsSpeaking)
        {
            ReadAloud.Stop();
            return;
        }
        if (ActiveDocument is not { } doc)
            return;
        if (doc.HasSelection)
        {
            doc.ReadSelection();
            return;
        }
        ReadAloud.SpeakPages(
            (page, ct) => doc.Document.GetPageTextAsync(page, WorkPriority.Background, ct),
            doc.CurrentPage,
            doc.PageCount,
            page => doc.GoToPage(page));
    }

    public void SetPageColorMode(PageColorMode mode)
    {
        if (ActiveDocument is { ColorOverride: not null } doc)
        {
            doc.ColorOverride = mode;
        }
        else
        {
            Settings.PageColorMode = mode;
            SaveSettings();
            foreach (var d in Documents)
                d.RefreshColors();
        }
        NotifyPageColors();
    }

    /// <summary>The mode in effect for the active document (override or global).</summary>
    public PageColorMode PageColorMode => ActiveDocument?.ColorOverride ?? Settings.PageColorMode;

    // Two-way bindable views of the page color state, for the top bar flyout.
    public bool PageModeNormal { get => PageColorMode == PageColorMode.Normal; set { if (value) SetPageColorMode(PageColorMode.Normal); } }
    public bool PageModeDark { get => PageColorMode == PageColorMode.Dark; set { if (value) SetPageColorMode(PageColorMode.Dark); } }
    public bool PageModeSepia { get => PageColorMode == PageColorMode.Sepia; set { if (value) SetPageColorMode(PageColorMode.Sepia); } }
    public bool PageModeCustom { get => PageColorMode == PageColorMode.Custom; set { if (value) SetPageColorMode(PageColorMode.Custom); } }

    public bool KeepImagesNatural
    {
        get => Settings.KeepImagesNatural;
        set => SettingsPage.KeepImagesNatural = value;
    }

    public bool PageColorsOnlyThisDocument
    {
        get => ActiveDocument?.ColorOverride is not null;
        set
        {
            if (ActiveDocument is not { } doc)
                return;
            doc.ColorOverride = value ? Settings.PageColorMode : null;
            NotifyPageColors();
        }
    }

    public IBrush CustomPageForeground => Brush.Parse(Settings.CustomForeground);
    public IBrush CustomPageBackground => Brush.Parse(Settings.CustomBackground);

    private void NotifyPageColors()
    {
        OnPropertyChanged(nameof(PageColorMode));
        OnPropertyChanged(nameof(PageModeNormal));
        OnPropertyChanged(nameof(PageModeDark));
        OnPropertyChanged(nameof(PageModeSepia));
        OnPropertyChanged(nameof(PageModeCustom));
        OnPropertyChanged(nameof(KeepImagesNatural));
        OnPropertyChanged(nameof(PageColorsOnlyThisDocument));
        OnPropertyChanged(nameof(CustomPageForeground));
        OnPropertyChanged(nameof(CustomPageBackground));
        SettingsPage.NotifyPageModes();
    }

    public void CyclePageColors()
    {
        var next = PageColorMode switch
        {
            PageColorMode.Normal => PageColorMode.Dark,
            PageColorMode.Dark => PageColorMode.Sepia,
            PageColorMode.Sepia => PageColorMode.Custom,
            _ => PageColorMode.Normal,
        };
        SetPageColorMode(next);
    }

    public void RefreshAllPageColors()
    {
        foreach (var d in Documents)
            d.RefreshColors();
        NotifyPageColors();
    }

    public void OpenSettings(int page = 0)
    {
        IsToolsPanelOpen = false;
        IsCommandPaletteOpen = false;
        SettingsPage.SelectedPage = page;
        IsSettingsOpen = true;
    }

    /// <summary>Esc closes the topmost transient UI. Returns false when there was nothing to close.</summary>
    public bool CloseTopmost()
    {
        if (Dialog is { } d) { d.Dismiss(); return true; }
        if (SignatureDialog is { } sd) { sd.Cancel(); return true; }
        if (IsCommandPaletteOpen) { IsCommandPaletteOpen = false; return true; }
        if (IsSettingsOpen) { IsSettingsOpen = false; return true; }
        if (IsToolsPanelOpen) { IsToolsPanelOpen = false; return true; }
        // Reading mode first: with the chrome hidden, Esc must always get you out.
        if (IsReadingMode) { IsReadingMode = false; return true; }
        if (ActiveDocument is { HasSelection: true } doc) { doc.Selection = null; return true; }
        if (Markup.Tool != ToolKind.Select) { Markup.Tool = ToolKind.Select; return true; }
        if (ReadAloud.IsSpeaking) { ReadAloud.Stop(); return true; }
        return false;
    }

    public void ApplyTheme() => _shell.ApplyTheme(Settings.Theme);

    // ---------------------------------------------------------------------------------------
    // Bookmarks, signatures, attachments
    // ---------------------------------------------------------------------------------------

    public async Task AddBookmarkPromptAsync(DocumentViewModel document)
    {
        var dialog = new DialogViewModel
        {
            Title = Loc.Get("CmdAddBookmark"),
            Message = Loc.Format("AddBookmarkMessage", document.CurrentPageNumber),
            PrimaryText = Loc.Get("Add"),
            SecondaryText = Loc.Get("Cancel"),
            HasInput = true,
            InputText = Loc.Format("PageN", document.CurrentPageNumber),
        };
        if (await ShowDialogAsync(dialog))
        {
            document.AddBookmark(dialog.InputText);
            document.ShowSidebar(SidebarTab.Outline);
        }
    }

    /// <summary>Opens the signature pad. Returns the saved signature (or null if cancelled).</summary>
    public async Task<SignatureData?> EditSignatureAsync(bool initials)
    {
        var dialog = new SignatureDialogViewModel(initials, initials ? State.Initials : State.Signature);
        SignatureDialog = dialog;
        try
        {
            var result = await dialog.Result;
            if (result is null)
                return null;
            if (initials) State.Initials = result; else State.Signature = result;
            SaveState();
            Markup.NotifySignatures();
            return result;
        }
        finally
        {
            SignatureDialog = null;
        }
    }

    public Task<string?> PickAttachmentAsync() => _shell.PickAnyFileAsync();

    // ---------------------------------------------------------------------------------------
    // Command registry
    // ---------------------------------------------------------------------------------------

    private List<AppCommand> BuildCommands()
    {
        bool HasDoc() => ActiveDocument is not null;
        void Doc(Action<DocumentViewModel> a) { if (ActiveDocument is { } d) a(d); }

        return
        [
            new("open", "CmdOpen", "IconFolder", () => _ = OpenDialogAsync(), null, "Ctrl+O"),
            new("save", "CmdSave", "IconSave", () => _ = SaveActiveAsync(), HasDoc, "Ctrl+S"),
            new("saveAs", "CmdSaveAs", "IconSave", () => _ = SaveActiveAsAsync(), HasDoc, "Ctrl+Shift+S"),
            new("undo", "CmdUndo", "IconUndo", () => Doc(d => _ = d.UndoAsync()), () => ActiveDocument?.CanUndo == true, "Ctrl+Z"),
            new("toolSelect", "ToolSelect", "IconSelect", () => Markup.Tool = ToolKind.Select, HasDoc),
            new("toolMarker", "ToolHighlight", "IconMarker", () => Markup.Toggle(ToolKind.Highlight), HasDoc, "Ctrl+Shift+H"),
            new("toolComment", "ToolComment", "IconComment", () => Markup.Toggle(ToolKind.Comment), HasDoc, "Ctrl+Shift+M"),
            new("toolDraw", "ToolDraw", "IconDraw", () => Markup.Toggle(ToolKind.Draw), HasDoc, "Ctrl+Shift+F"),
            new("toolText", "ToolTypeText", "IconTypeText", () => Markup.Toggle(ToolKind.TypeText), HasDoc, "Ctrl+Shift+T"),
            new("toolSign", "ToolSignature", "IconSignature", () => Markup.Toggle(ToolKind.Signature), HasDoc),
            new("toolStamp", "ToolStamp", "IconStamp", () => Markup.Toggle(ToolKind.Stamp), HasDoc),
            new("toolPan", "ToolPan", "IconPan", () => Markup.Toggle(ToolKind.Pan), HasDoc),
            new("comments", "CmdComments", "IconComment", () => Doc(d => d.ShowSidebar(SidebarTab.Comments)), HasDoc),
            new("addBookmark", "CmdAddBookmark", "IconBookmarkAdd", () => Doc(d => _ = AddBookmarkPromptAsync(d)), HasDoc),
            new("exit", "CmdExit", "IconClose", () => _shell.CloseWindow()),
            new("close", "CmdClose", "IconClose", () => _ = CloseDocumentAsync(null), HasDoc, "Ctrl+W", "Ctrl+F4"),
            new("nextTab", "CmdNextTab", "IconChevronRight", () => CycleTabs(+1), () => Documents.Count > 1, "Ctrl+Tab"),
            new("prevTab", "CmdPrevTab", "IconChevronLeft", () => CycleTabs(-1), () => Documents.Count > 1, "Ctrl+Shift+Tab"),
            new("print", "CmdPrint", "IconPrint", () => _ = PrintAsync(), HasDoc, "Ctrl+P"),
            new("find", "CmdFind", "IconSearch", _shell.FocusSearch, HasDoc, "Ctrl+F"),
            new("findNext", "CmdFindNext", "IconArrowDown", () => Doc(d => d.NextHit(+1)), HasDoc, "F3"),
            new("findPrev", "CmdFindPrev", "IconArrowUp", () => Doc(d => d.NextHit(-1)), HasDoc, "Shift+F3"),
            new("goto", "CmdGoToPage", "IconArrowRight", () => _ = GoToPagePromptAsync(), HasDoc, "Ctrl+G"),
            new("firstPage", "CmdFirstPage", "IconFirst", () => Doc(d => d.GoToPage(0)), HasDoc, "Ctrl+Home"),
            new("lastPage", "CmdLastPage", "IconLast", () => Doc(d => d.GoToPage(d.PageCount - 1)), HasDoc, "Ctrl+End"),
            new("nextPage", "CmdNextPage", "IconChevronDown", () => Doc(d => d.GoToPage(d.CurrentPage + 1)), HasDoc, "Ctrl+PageDown"),
            new("prevPage", "CmdPrevPage", "IconChevronUp", () => Doc(d => d.GoToPage(d.CurrentPage - 1)), HasDoc, "Ctrl+PageUp"),
            new("zoomIn", "CmdZoomIn", "IconPlus", () => Doc(d => d.Request(new ViewerRequest.ZoomStep(+1))), HasDoc, "Ctrl+OemPlus", "Ctrl+Add"),
            new("zoomOut", "CmdZoomOut", "IconMinus", () => Doc(d => d.Request(new ViewerRequest.ZoomStep(-1))), HasDoc, "Ctrl+OemMinus", "Ctrl+Subtract"),
            new("fitWidth", "CmdFitWidth", "IconFitWidth", () => Doc(d => d.Request(new ViewerRequest.SetZoomMode(ZoomMode.FitWidth))), HasDoc, "Ctrl+D0", "Ctrl+NumPad0"),
            new("actualSize", "CmdActualSize", "IconActualSize", () => Doc(d => d.Request(new ViewerRequest.SetZoomMode(ZoomMode.ActualSize))), HasDoc, "Ctrl+D1", "Ctrl+NumPad1"),
            new("fitPage", "CmdFitPage", "IconFitPage", () => Doc(d => d.Request(new ViewerRequest.SetZoomMode(ZoomMode.FitPage))), HasDoc, "Ctrl+D2", "Ctrl+NumPad2"),
            new("sidebar", "CmdToggleSidebar", "IconSidebar", () => Doc(d => d.IsSidebarOpen = !d.IsSidebarOpen), HasDoc, "F4"),
            new("thumbnails", "CmdThumbnails", "IconGrid", () => Doc(d => d.ShowSidebar(SidebarTab.Thumbnails)), HasDoc),
            new("outline", "CmdOutline", "IconList", () => Doc(d => d.ShowSidebar(SidebarTab.Outline)), HasDoc),
            new("readingMode", "CmdReadingMode", "IconBook", () => IsReadingMode = !IsReadingMode, HasDoc, "F11"),
            new("pageColors", "CmdPageColorCycle", "IconDrop", CyclePageColors, null, "Ctrl+Shift+D"),
            new("pageNormal", "CmdPageNormal", "IconDrop", () => SetPageColorMode(PageColorMode.Normal)),
            new("pageDark", "CmdPageDark", "IconDrop", () => SetPageColorMode(PageColorMode.Dark)),
            new("pageSepia", "CmdPageSepia", "IconDrop", () => SetPageColorMode(PageColorMode.Sepia)),
            new("pageCustom", "CmdPageCustom", "IconDrop", () => SetPageColorMode(PageColorMode.Custom)),
            new("readAloud", "CmdReadAloud", "IconSpeaker", ToggleReadAloud, HasDoc, "Ctrl+Shift+Y"),
            new("stopReading", "CmdStopReading", "IconStop", ReadAloud.Stop, () => ReadAloud.IsSpeaking),
            new("copy", "CmdCopy", "IconCopy", () => Doc(d => _ = d.CopySelectionAsync()), () => ActiveDocument?.HasSelection == true, "Ctrl+C"),
            new("selectAll", "CmdSelectAll", "IconSelect", () => Doc(d => _ = d.SelectAllOnPageAsync()), HasDoc, "Ctrl+A"),
            new("palette", "CmdPalette", "IconCommand", () => IsCommandPaletteOpen = !IsCommandPaletteOpen, null, "Ctrl+K", "Ctrl+Shift+P"),
            new("tools", "CmdTools", "IconMenu", () => IsToolsPanelOpen = !IsToolsPanelOpen, null, "Ctrl+T"),
            new("settings", "CmdSettings", "IconSettings", () => OpenSettings(), null, "Ctrl+OemComma"),
            new("updates", "CmdUpdates", "IconRefresh", () => { OpenSettings(2); _ = Updates.CheckAsync(userInitiated: true); }),
        ];
    }

    private List<ToolGroup> BuildToolGroups()
    {
        AppCommand C(string id) => Commands.First(c => c.Id == id);
        IBrush Brush(string key) => Application.Current?.TryGetResource(key, null, out var b) == true && b is IBrush brush ? brush : Brushes.Gray;

        return
        [
            new("GroupView", Brush("ToolBlueBrush"), [C("readingMode"), C("pageColors"), C("fitWidth"), C("fitPage"), C("toolPan")]),
            new("GroupMarkup", Brush("ToolRedBrush"), [C("toolMarker"), C("toolComment"), C("toolDraw"), C("toolText"), C("toolStamp"), C("toolSign"), C("undo")]),
            new("GroupPanels", Brush("ToolTealBrush"), [C("thumbnails"), C("outline"), C("comments"), C("addBookmark")]),
            new("GroupRead", Brush("ToolPurpleBrush"), [C("find"), C("goto"), C("readAloud"), C("selectAll")]),
            new("GroupDocument", Brush("ToolGreenBrush"), [C("open"), C("save"), C("saveAs"), C("print"), C("close")]),
            new("GroupApp", Brush("ToolOrangeBrush"), [C("palette"), C("settings"), C("updates")]),
        ];
    }
}
