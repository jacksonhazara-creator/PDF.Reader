using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfReader.App.Localization;
using PdfReader.App.Services;
using PdfReader.Core.Bookmarks;
using PdfReader.Core.Markup;
using PdfReader.Core.Search;
using PdfReader.Core.Settings;
using PdfReader.Rendering;

namespace PdfReader.App.ViewModels;

public enum SidebarTab { Thumbnails, Outline, Comments }

public sealed partial class DocumentViewModel : ObservableObject, IAsyncDisposable
{
    private static long s_nextId;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _scanCts;
    private List<ThumbnailViewModel>? _thumbnails;
    private Dictionary<int, List<SearchHit>> _hitsByPage = new();
    private bool _outlineLoaded;

    public DocumentViewModel(MainViewModel main, PdfDocument document, string filePath, DocumentViewState state, string? password = null)
    {
        Password = password;
        _main = main;
        Document = document;
        FilePath = filePath;
        CurrentPage = Math.Clamp(state.PageIndex, 0, Math.Max(0, document.PageCount - 1));
        SavedPageOffset = state.PageOffset;
        ZoomMode = state.ZoomMode;
        Zoom = state.Zoom > 0 ? state.Zoom : 1;
        ColorOverride = state.PageColorOverride;
        IsSidebarOpen = main.Settings.RightPanelOpen;
        SidebarTab = Enum.TryParse<SidebarTab>(main.Settings.RightPanelTab, out var tab) ? tab : SidebarTab.Thumbnails;
        Security = document.Security;
        _revisions = new int[document.PageCount];
        UpdateSecurityBanner();
    }

    public long Id { get; } = Interlocked.Increment(ref s_nextId);
    /// <summary>Kept in memory only (needed to edit bookmarks of protected files on save).</summary>
    public string? Password { get; }
    public PdfDocument Document { get; }
    public string FilePath { get; }
    public string Title => Path.GetFileName(FilePath);

    /// <summary>Used as the accessible name of list items (tabs).</summary>
    public override string ToString() => Title;
    public int PageCount => Document.PageCount;
    public MainViewModel Main => _main;

    /// <summary>Raised for scroll/zoom changes the viewer must perform.</summary>
    public event Action<ViewerRequest>? ViewerRequested;
    public void Request(ViewerRequest request) => ViewerRequested?.Invoke(request);

    // --- Position & zoom (written by the viewer) -------------------------------------------

    [ObservableProperty] public partial int CurrentPage { get; set; }
    [ObservableProperty] public partial double Zoom { get; set; }
    [ObservableProperty] public partial ZoomMode ZoomMode { get; set; }

    /// <summary>Scroll position inside <see cref="CurrentPage"/>, 0..1, kept so tabs restore exactly.</summary>
    public double SavedPageOffset { get; set; }

    public int CurrentPageNumber => CurrentPage + 1;
    public string PageCountText => Loc.Format("PageOf", PageCount);
    public string ZoomText => $"{Zoom * 100:0}%";

    partial void OnCurrentPageChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentPageNumber));
        if (_thumbnails is not null)
            foreach (var t in _thumbnails)
                t.IsCurrent = t.PageIndex == value;
    }

    partial void OnZoomChanged(double value) => OnPropertyChanged(nameof(ZoomText));

    [RelayCommand] private void ZoomIn() => Request(new ViewerRequest.ZoomStep(+1));
    [RelayCommand] private void ZoomOut() => Request(new ViewerRequest.ZoomStep(-1));
    [RelayCommand] private void FitWidth() => Request(new ViewerRequest.SetZoomMode(ZoomMode.FitWidth));
    [RelayCommand] private void FitPage() => Request(new ViewerRequest.SetZoomMode(ZoomMode.FitPage));
    [RelayCommand] private void ActualSize() => Request(new ViewerRequest.SetZoomMode(ZoomMode.ActualSize));

    public void GoToPage(int pageIndex)
    {
        if (PageCount == 0)
            return;
        Request(new ViewerRequest.GoToPage(Math.Clamp(pageIndex, 0, PageCount - 1)));
    }

    // --- Page colors -------------------------------------------------------------------

    [ObservableProperty] public partial PageColorMode? ColorOverride { get; set; }

    public PageColors Colors => _main.Settings.ResolvePageColors(ColorOverride);

    partial void OnColorOverrideChanged(PageColorMode? value) => RefreshColors();

    public void RefreshColors()
    {
        OnPropertyChanged(nameof(Colors));
        if (_thumbnails is not null)
            foreach (var t in _thumbnails)
                t.Reset();
    }

    // --- Right panel: pages, bookmarks, comments ------------------------------------------

    [ObservableProperty] public partial bool IsSidebarOpen { get; set; }
    [ObservableProperty] public partial SidebarTab SidebarTab { get; set; }
    [ObservableProperty] public partial IReadOnlyList<OutlineItemViewModel> Outline { get; set; } = [];

    public bool HasOutline => Bookmarks.Count > 0;
    public bool IsThumbnailsTab { get => SidebarTab == SidebarTab.Thumbnails; set { if (value) SidebarTab = SidebarTab.Thumbnails; } }
    public bool IsOutlineTab { get => SidebarTab == SidebarTab.Outline; set { if (value) SidebarTab = SidebarTab.Outline; } }
    public bool IsCommentsTab { get => SidebarTab == SidebarTab.Comments; set { if (value) SidebarTab = SidebarTab.Comments; } }

    public IReadOnlyList<ThumbnailViewModel> Thumbnails
    {
        get
        {
            if (_thumbnails is null)
            {
                _thumbnails = new List<ThumbnailViewModel>(PageCount);
                for (var i = 0; i < PageCount; i++)
                    _thumbnails.Add(new ThumbnailViewModel(this, i) { IsCurrent = i == CurrentPage });
            }
            return _thumbnails;
        }
    }

    partial void OnSidebarTabChanged(SidebarTab value)
    {
        OnPropertyChanged(nameof(IsThumbnailsTab));
        OnPropertyChanged(nameof(IsOutlineTab));
        OnPropertyChanged(nameof(IsCommentsTab));
        _main.Settings.RightPanelTab = value.ToString();
        _main.SaveSettings();
        LoadTab();
    }

    partial void OnIsSidebarOpenChanged(bool value)
    {
        _main.Settings.RightPanelOpen = value;
        _main.SaveSettings();
        if (value)
            LoadTab();
    }

    private void LoadTab()
    {
        if (SidebarTab == SidebarTab.Outline)
            _ = LoadOutlineAsync();
        else if (SidebarTab == SidebarTab.Comments)
            _ = LoadAllCommentsAsync();
    }

    public void ShowSidebar(SidebarTab tab)
    {
        SidebarTab = tab;
        IsSidebarOpen = true;
        LoadTab();
    }

    // Bookmarks: flattened for the list; edits are applied to the file on save (PDFium can't edit outlines).

    public ObservableCollection<BookmarkItemViewModel> Bookmarks { get; } = [];
    private readonly List<OutlineEdit> _outlineEdits = [];
    public bool HasOutlineEdits => _outlineEdits.Count > 0;
    public IReadOnlyList<OutlineEdit> OutlineEdits => _outlineEdits;

    public async Task LoadOutlineAsync(bool force = false)
    {
        if (_outlineLoaded && !force)
            return;
        _outlineLoaded = true;
        try
        {
            var items = await Document.GetOutlineAsync();
            Outline = items.Select(i => new OutlineItemViewModel(this, i)).ToList();
            Bookmarks.Clear();
            void Add(IReadOnlyList<OutlineItem> list, int depth, ImmutablePath path)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var p = path.Append(i);
                    Bookmarks.Add(new BookmarkItemViewModel(this, list[i].Title, list[i].PageIndex, depth, p.Items));
                    Add(list[i].Children, depth + 1, p);
                }
            }
            Add(items, 0, ImmutablePath.Empty);
            OnPropertyChanged(nameof(HasOutline));
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException)
        {
            Outline = [];
        }
    }

    public void DeleteBookmark(BookmarkItemViewModel bookmark)
    {
        // Remove it and its children from the list; one edit removes the subtree in the file.
        var index = Bookmarks.IndexOf(bookmark);
        if (index < 0)
            return;
        Bookmarks.RemoveAt(index);
        while (index < Bookmarks.Count && Bookmarks[index].Depth > bookmark.Depth)
            Bookmarks.RemoveAt(index);
        if (bookmark.Path is { Count: > 0 } path)
            _outlineEdits.Add(new OutlineEdit.Remove(path));
        else
            _outlineEdits.RemoveAll(e => e is OutlineEdit.Add a && a.Title == bookmark.Title && a.PageIndex == bookmark.PageIndex);
        IsModified = true;
        OnPropertyChanged(nameof(HasOutline));
    }

    public void AddBookmark(string title)
    {
        title = string.IsNullOrWhiteSpace(title) ? Loc.Format("PageN", CurrentPageNumber) : title.Trim();
        _outlineEdits.Add(new OutlineEdit.Add(title, CurrentPage));
        Bookmarks.Add(new BookmarkItemViewModel(this, title, CurrentPage, 0, []));
        IsModified = true;
        OnPropertyChanged(nameof(HasOutline));
    }

    [RelayCommand] private Task AddBookmarkPrompt() => _main.AddBookmarkPromptAsync(this);

    /// <summary>After saving: bookmark edits are in the file; reload them from it.</summary>
    public async Task AfterSaveAsync()
    {
        _outlineEdits.Clear();
        IsModified = Document.IsModified;
        await LoadOutlineAsync(force: true);
    }

    // Comments (all pages)

    public ObservableCollection<CommentItemViewModel> AllComments { get; } = [];
    public bool HasComments => AllComments.Count > 0;
    private bool _allCommentsLoading;

    /// <summary>Asks the viewer to scroll to a comment and show its card.</summary>
    public event Action<PdfComment>? CommentFocusRequested;
    public void FocusComment(PdfComment comment) => CommentFocusRequested?.Invoke(comment);

    public async Task LoadAllCommentsAsync()
    {
        if (_allCommentsLoading)
            return;
        _allCommentsLoading = true;
        try
        {
            for (var page = 0; page < PageCount; page++)
            {
                var list = _comments.TryGetValue(page, out var cached) ? cached : await Document.GetCommentsAsync(page);
                _comments[page] = list;
                ReplaceCommentItems(page, list);
            }
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException or OperationCanceledException) { }
        finally
        {
            _allCommentsLoading = false;
        }
    }

    private void ReplaceCommentItems(int page, IReadOnlyList<PdfComment> list)
    {
        for (var i = AllComments.Count - 1; i >= 0; i--)
            if (AllComments[i].Comment.PageIndex == page)
                AllComments.RemoveAt(i);
        var insertAt = 0;
        while (insertAt < AllComments.Count && AllComments[insertAt].Comment.PageIndex < page)
            insertAt++;
        foreach (var c in list)
            AllComments.Insert(insertAt++, new CommentItemViewModel(this, c));
        OnPropertyChanged(nameof(HasComments));
    }

    // --- Search ---------------------------------------------------------------------------

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool MatchCase { get; set; }
    [ObservableProperty] public partial bool WholeWord { get; set; }
    [ObservableProperty] public partial bool IsSearching { get; set; }
    [ObservableProperty] public partial string SearchStatus { get; set; } = "";

    public List<SearchHit> Hits { get; private set; } = [];
    public int CurrentHitIndex { get; private set; } = -1;
    public SearchHit? CurrentHit => CurrentHitIndex >= 0 && CurrentHitIndex < Hits.Count ? Hits[CurrentHitIndex] : null;
    public IReadOnlyList<SearchHit>? HitsOnPage(int page) => _hitsByPage.GetValueOrDefault(page);

    /// <summary>Raised when hits or the current hit change, so the viewer can repaint highlights.</summary>
    public event Action? HighlightsChanged;

    partial void OnSearchTextChanged(string value) => _ = SearchDebouncedAsync();
    partial void OnMatchCaseChanged(bool value) => _ = SearchAsync();
    partial void OnWholeWordChanged(bool value) => _ = SearchAsync();

    private async Task SearchDebouncedAsync()
    {
        var text = SearchText;
        await Task.Delay(220);
        if (text == SearchText)
            await SearchAsync();
    }

    public async Task SearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        Hits = [];
        _hitsByPage = new Dictionary<int, List<SearchHit>>();
        CurrentHitIndex = -1;
        HighlightsChanged?.Invoke();

        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            SearchStatus = "";
            IsSearching = false;
            return;
        }

        IsSearching = true;
        SearchStatus = Loc.Get("Searching");
        try
        {
            // Pages are searched starting at the current page, so the first hit found is the one
            // to show; later batches are merged in reading order around it.
            SearchHit? anchor = null;
            await foreach (var batch in DocumentSearch.SearchAsync(Document, query, new SearchOptions(MatchCase, WholeWord), CurrentPage, cts.Token))
            {
                Hits.AddRange(batch);
                _hitsByPage[batch[0].PageIndex] = batch.ToList();
                Hits.Sort(DocumentSearch.Compare);
                if (anchor is null)
                {
                    anchor = batch[0];
                    CurrentHitIndex = Hits.IndexOf(anchor);
                    RevealCurrentHit();
                }
                else if (CurrentHit is { } current)
                {
                    CurrentHitIndex = Hits.IndexOf(current);
                }
                UpdateSearchStatus();
                HighlightsChanged?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (cts == _searchCts)
        {
            IsSearching = false;
            UpdateSearchStatus();
            HighlightsChanged?.Invoke();
        }
    }

    private void UpdateSearchStatus()
        => SearchStatus = Hits.Count == 0
            ? (IsSearching ? Loc.Get("Searching") : Loc.Get("NoResults"))
            : Loc.Format("ResultOf", CurrentHitIndex + 1, Hits.Count) + (IsSearching ? "…" : "");

    public void NextHit(int direction)
    {
        if (Hits.Count == 0)
        {
            if (SearchText.Length > 0 && !IsSearching)
                _ = SearchAsync();
            return;
        }
        CurrentHitIndex = (CurrentHitIndex + direction + Hits.Count) % Hits.Count;
        UpdateSearchStatus();
        RevealCurrentHit();
        HighlightsChanged?.Invoke();
    }

    private void RevealCurrentHit()
    {
        if (CurrentHit is { } hit && hit.Rects.Count > 0)
            Request(new ViewerRequest.Reveal(hit.PageIndex, hit.Rects[0]));
    }

    public void ClearSearch()
    {
        SearchText = "";
    }

    // --- Selection -----------------------------------------------------------------------

    [ObservableProperty] public partial TextSelection? Selection { get; set; }
    public bool HasSelection => Selection is { IsEmpty: false };
    partial void OnSelectionChanged(TextSelection? value) => OnPropertyChanged(nameof(HasSelection));

    [RelayCommand]
    public async Task CopySelectionAsync()
    {
        if (Selection is { IsEmpty: false } s)
            await _main.CopyTextAsync(s.Text);
    }

    [RelayCommand]
    public void ReadSelection()
    {
        if (Selection is { IsEmpty: false } s)
            _main.ReadAloud.SpeakText(s.Text);
    }

    public async Task SelectAllOnPageAsync()
    {
        try
        {
            var count = await Document.GetCharCountAsync(CurrentPage);
            Selection = count > 0 ? await Document.GetSelectionAsync(CurrentPage, 0, count - 1) : null;
        }
        catch (PdfException) { }
    }

    // --- Security ---------------------------------------------------------------------------

    [ObservableProperty] public partial SecurityReport Security { get; set; }
    [ObservableProperty] public partial bool IsSecurityBannerVisible { get; set; }
    [ObservableProperty] public partial string SecurityMessage { get; set; } = "";

    partial void OnSecurityChanged(SecurityReport value) => UpdateSecurityBanner();

    private void UpdateSecurityBanner()
    {
        var parts = new List<string>();
        if (Security.HasScripts) parts.Add(Loc.Get("SecScripts"));
        if (Security.HasAttachments) parts.Add(Loc.Get("SecAttachments"));
        if (Security.HasLaunchActions) parts.Add(Loc.Get("SecLaunch"));
        if (parts.Count == 0)
        {
            IsSecurityBannerVisible = false;
            return;
        }
        var list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + $" {Loc.Get("And")} " + parts[^1];
        SecurityMessage = Loc.Format("SecurityBanner", list);
        IsSecurityBannerVisible = true;
    }

    [RelayCommand] private void DismissSecurityBanner() => IsSecurityBannerVisible = false;

    /// <summary>Scans pages for launch actions in the background (never blocks reading).</summary>
    public async Task ScanSecurityAsync()
    {
        _scanCts = new CancellationTokenSource();
        try
        {
            Security = await Document.ScanActiveContentAsync(_scanCts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or PdfException) { }
    }

    public Task ActivateLinkAsync(PdfLink link) => _main.ActivateLinkAsync(this, link);

    // --- Editing: marker, drawing, comments ---------------------------------------------------

    private readonly Stack<(int Page, string Id)> _undo = new();
    private readonly int[] _revisions;
    private readonly Dictionary<int, IReadOnlyList<PdfComment>> _comments = new();
    private readonly HashSet<int> _commentsLoading = new();

    [ObservableProperty] public partial bool IsModified { get; set; }

    /// <summary>Tab text; a dot marks unsaved changes.</summary>
    public string TabTitle => IsModified ? "● " + Title : Title;
    partial void OnIsModifiedChanged(bool value) => OnPropertyChanged(nameof(TabTitle));

    public bool CanUndo => _undo.Count > 0;

    /// <summary>Bumped whenever a page's annotations change, so the viewer re-renders it.</summary>
    public int RevisionOf(int page) => (uint)page < (uint)_revisions.Length ? _revisions[page] : 0;

    public event Action<int>? PageContentChanged;
    public event Action? CommentsChanged;

    /// <summary>Comments on a page, or null while they are loading (the viewer asks again on CommentsChanged).</summary>
    public IReadOnlyList<PdfComment>? CommentsOn(int page)
    {
        if (_comments.TryGetValue(page, out var list))
            return list;
        if (_commentsLoading.Add(page))
            _ = LoadCommentsAsync(page);
        return null;
    }

    private async Task LoadCommentsAsync(int page)
    {
        try
        {
            _comments[page] = await Document.GetCommentsAsync(page);
            CommentsChanged?.Invoke();
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException)
        {
            _comments[page] = [];
        }
        finally
        {
            _commentsLoading.Remove(page);
        }
    }

    public Task HighlightAsync(TextSelection selection) => EditAsync(selection.PageIndex, async () =>
    {
        if (selection.IsEmpty || selection.Rects.Count == 0)
            return null;
        Selection = null;
        return await Document.AddHighlightAsync(selection.PageIndex, selection.Rects, _main.Settings.ResolveMarkerArgb());
    });

    public Task DrawAsync(int page, IReadOnlyList<PdfPoint> points) => EditAsync(page, async () =>
    {
        if (points.Count < 2)
            return null;
        var s = _main.Settings;
        return await Document.AddInkAsync(page, [points], s.ResolveMarkerArgb(), s.MarkerThickness);
    });

    /// <summary>Adds a comment marker next to the first or last word of the selection.</summary>
    public Task AddCommentAsync(TextSelection selection, string text) => EditAsync(selection.PageIndex, async () =>
    {
        if (selection.Rects.Count == 0 || string.IsNullOrWhiteSpace(text))
            return null;
        var s = _main.Settings;
        const double size = 16;
        var onLast = s.CommentOnLastWord;
        var word = onLast ? selection.Rects[^1] : selection.Rects[0];
        var pageSize = Document.PageSizes[selection.PageIndex];
        var x = Math.Clamp(onLast ? word.Right + 1 : word.X - size - 1, 0, pageSize.Width - size);
        var y = Math.Clamp(word.Y - size * 0.55, 0, pageSize.Height - size);
        Selection = null;
        return await Document.AddCommentAsync(selection.PageIndex, new PdfRect(x, y, size, size), text.Trim(), selection.Text.Trim(),
            selection.Rects, s.CommentShape, ColorParser.ParseOrDefault(s.CommentColor, 0xFFFFB020));
    });

    public async Task DeleteCommentAsync(PdfComment comment)
    {
        try
        {
            if (await Document.RemoveAnnotationAsync(comment.PageIndex, comment.Id))
                MarkPageChanged(comment.PageIndex);
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException) { }
    }

    public async Task UndoAsync()
    {
        if (_undo.Count == 0)
            return;
        var (page, id) = _undo.Pop();
        try
        {
            await Document.RemoveAnnotationAsync(page, id);
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException) { }
        MarkPageChanged(page);
    }

    private async Task EditAsync(int page, Func<Task<string?>> edit)
    {
        try
        {
            if (await edit() is { } id)
            {
                _undo.Push((page, id));
                MarkPageChanged(page);
            }
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException or ArgumentException)
        {
            await _main.ShowMessageAsync(Loc.Get("EditFailed"), ex.Message);
        }
    }

    private void MarkPageChanged(int page)
    {
        if ((uint)page < (uint)_revisions.Length)
            _revisions[page]++;
        _comments.Remove(page);
        IsModified = Document.IsModified || HasOutlineEdits;
        if (AllComments.Count > 0 || SidebarTab == SidebarTab.Comments)
            _ = RefreshPageCommentsAsync(page);
        OnPropertyChanged(nameof(CanUndo));
        if (_thumbnails is not null && page < _thumbnails.Count)
            _thumbnails[page].Reset();
        PageContentChanged?.Invoke(page);
        CommentsChanged?.Invoke();
    }

    private async Task RefreshPageCommentsAsync(int page)
    {
        try
        {
            var list = await Document.GetCommentsAsync(page);
            _comments[page] = list;
            ReplaceCommentItems(page, list);
            CommentsChanged?.Invoke();
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException) { }
    }

    public Task MarkTextAsync(TextSelection selection, TextMarkupKind kind) => EditAsync(selection.PageIndex, async () =>
    {
        if (selection.IsEmpty || selection.Rects.Count == 0)
            return null;
        Selection = null;
        return await Document.AddTextMarkupAsync(selection.PageIndex, kind, selection.Rects, _main.Settings.ResolveMarkerArgb());
    });

    /// <summary>Strikes the selected text and records the replacement as a comment on it.</summary>
    public Task ReplaceTextAsync(TextSelection selection, string replacement) => EditAsync(selection.PageIndex, async () =>
    {
        if (selection.Rects.Count == 0 || string.IsNullOrWhiteSpace(replacement))
            return null;
        Selection = null;
        return await Document.AddTextMarkupAsync(selection.PageIndex, TextMarkupKind.StrikeOut, selection.Rects,
            ColorParser.ParseOrDefault(_main.Settings.CommentColor, 0xFFD9363E) | 0xFF000000, replacement.Trim(), "Replace", selection.Text.Trim());
    });

    public Task InsertTextAsync(int page, PdfPoint at, string text) => EditAsync(page, async () =>
        string.IsNullOrWhiteSpace(text) ? null
            : await Document.AddInsertAsync(page, new PdfRect(at.X - 5, at.Y - 9, 10, 10), text.Trim(), ColorParser.ParseOrDefault(_main.Settings.CommentColor, 0xFF2F6FEB)));

    public Task AttachFileAsync(int page, PdfPoint at, string filePath) => EditAsync(page, async () =>
    {
        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length > 50 * 1024 * 1024)
            throw new ArgumentException(Loc.Get("AttachTooLarge"));
        var data = await File.ReadAllBytesAsync(filePath);
        return await Document.AddFileAttachmentAsync(page, new PdfRect(at.X - 8, at.Y - 10, 16, 20), info.Name, data,
            ColorParser.ParseOrDefault(_main.Settings.CommentColor, 0xFF2F6FEB));
    });

    public Task InkAsync(int page, IReadOnlyList<IReadOnlyList<PdfPoint>> strokes, uint argb, double width) => EditAsync(page, async () =>
        strokes.Count == 0 || strokes.All(s => s.Count < 2) ? null : await Document.AddInkAsync(page, strokes, argb, width));

    public Task ShapeAsync(int page, ShapeKind kind, PdfRect rect, uint argb, double width) => EditAsync(page, async () =>
        rect.Width < 2 || rect.Height < 2 ? null : await Document.AddShapeAsync(page, kind, rect, argb, width));

    public Task StampAsync(int page, IReadOnlyList<StampPart> parts, string tag, string? contents = null) => EditAsync(page, async () =>
        parts.Count == 0 ? null : await Document.AddStampAsync(page, parts, tag, contents));

    // --- Right-click actions on existing marks --------------------------------------------

    public async Task DeleteAnnotationAsync(PdfAnnotationHit hit)
    {
        try
        {
            if (await Document.RemoveAnnotationAsync(hit.PageIndex, hit.Id))
                MarkPageChanged(hit.PageIndex);
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException) { }
    }

    public async Task RecolorAnnotationAsync(PdfAnnotationHit hit, uint rgb)
    {
        try
        {
            if (await Document.SetAnnotationColorAsync(hit.PageIndex, hit.Id, rgb))
                MarkPageChanged(hit.PageIndex);
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException) { }
    }

    public async Task EditAnnotationTextAsync(PdfAnnotationHit hit, string text)
    {
        try
        {
            if (await Document.SetAnnotationTextAsync(hit.PageIndex, hit.Id, text.Trim()))
                MarkPageChanged(hit.PageIndex);
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException) { }
    }

    // --- Persistence -------------------------------------------------------------------------
    public void SaveViewState(DocumentViewState state)
    {
        state.PageIndex = CurrentPage;
        state.PageOffset = SavedPageOffset;
        state.ZoomMode = ZoomMode;
        state.Zoom = Zoom;
        state.PageColorOverride = ColorOverride;
        state.LastViewedUtc = DateTime.UtcNow;
    }

    public async ValueTask DisposeAsync()
    {
        _searchCts?.Cancel();
        _scanCts?.Cancel();
        if (_thumbnails is not null)
            foreach (var t in _thumbnails)
                t.Reset();
        TileCache.Shared.RemoveDocument(Id);
        await Document.DisposeAsync();
    }
}

public sealed partial class OutlineItemViewModel(DocumentViewModel owner, OutlineItem item)
{
    public string Title { get; } = item.Title;
    public int PageIndex { get; } = item.PageIndex;
    public override string ToString() => Title;
    public IReadOnlyList<OutlineItemViewModel> Children { get; } = item.Children.Select(c => new OutlineItemViewModel(owner, c)).ToList();

    [RelayCommand]
    private void Navigate()
    {
        if (PageIndex >= 0)
            owner.GoToPage(PageIndex);
    }
}

public sealed partial class ThumbnailViewModel : ObservableObject
{
    public const double ThumbWidth = 112;
    private readonly DocumentViewModel _owner;
    private Bitmap? _image;
    private bool _requested;
    private int _generation;

    public ThumbnailViewModel(DocumentViewModel owner, int pageIndex)
    {
        _owner = owner;
        PageIndex = pageIndex;
        var size = owner.Document.PageSizes[pageIndex];
        Height = Math.Round(ThumbWidth * size.Height / size.Width);
    }

    public int PageIndex { get; }
    public string Label => (PageIndex + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
    public override string ToString() => Loc.Format("PageN", Label);
    public double Width => ThumbWidth;
    public double Height { get; }

    [ObservableProperty] public partial bool IsCurrent { get; set; }

    /// <summary>Rendered lazily the first time a (virtualized) list item binds to it.</summary>
    public Bitmap? Image
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                _ = LoadAsync(_generation);
            }
            return _image;
        }
    }

    [RelayCommand] private void Navigate() => _owner.GoToPage(PageIndex);

    private async Task LoadAsync(int generation)
    {
        try
        {
            var size = _owner.Document.PageSizes[PageIndex];
            var scale = ThumbWidth * 2 / size.Width; // 2x for high-DPI screens
            var pixels = await _owner.Document.RenderPageAsync(PageIndex, scale, _owner.Colors, WorkPriority.Background);
            var bitmap = await Task.Run(() => BitmapFactory.Create(pixels));
            if (generation != _generation)
            {
                bitmap.Dispose();
                return;
            }
            _image = bitmap;
            OnPropertyChanged(nameof(Image));
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException or OperationCanceledException) { }
    }

    public void Reset()
    {
        _generation++;
        _requested = false;
        var old = _image;
        _image = null;
        OnPropertyChanged(nameof(Image));
        old?.Dispose();
    }
}

/// <summary>One bookmark row in the right panel (flattened tree).</summary>
public sealed partial class BookmarkItemViewModel(DocumentViewModel owner, string title, int pageIndex, int depth, IReadOnlyList<int> path)
{
    public string Title { get; } = title;
    public int PageIndex { get; } = pageIndex;
    public int Depth { get; } = depth;
    public IReadOnlyList<int> Path { get; } = path;
    public string PageLabel => PageIndex >= 0 ? Loc.Format("PageShort", PageIndex + 1) : "";
    public Avalonia.Thickness Indent => new(Depth * 14, 0, 0, 0);
    public override string ToString() => Title;

    [RelayCommand] private void Go() { if (PageIndex >= 0) owner.GoToPage(PageIndex); }
    [RelayCommand] private void Delete() => owner.DeleteBookmark(this);
}

/// <summary>One comment row in the right panel: what, where (page) and when.</summary>
public sealed partial class CommentItemViewModel(DocumentViewModel owner, PdfComment comment)
{
    public PdfComment Comment { get; } = comment;
    public string Text => Comment.Kind switch
    {
        "Replace" => Loc.Format("ReplaceWith", Comment.Text),
        "Insert" => Loc.Format("InsertWhat", Comment.Text),
        "Attachment" => "📎 " + Comment.Text,
        _ => Comment.Text,
    };
    public string Quote => Comment.Quote.Length == 0 ? "" : "“" + (Comment.Quote.Length > 80 ? Comment.Quote[..80] + "…" : Comment.Quote) + "”";
    public bool HasQuote => Comment.Quote.Length > 0;
    public string PageLabel => Loc.Format("PageN", Comment.PageIndex + 1);
    public string DateText => Comment.CreatedUtc == DateTime.MinValue ? "" : Comment.CreatedUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
    public Avalonia.Media.IBrush Color { get; } = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromUInt32(comment.Color | 0xFF000000));
    public override string ToString() => Text;

    [RelayCommand] private void Go() => owner.FocusComment(Comment);
    [RelayCommand] private Task Delete() => owner.DeleteCommentAsync(Comment);
}

/// <summary>Index path into the outline tree.</summary>
public sealed record ImmutablePath(IReadOnlyList<int> Items)
{
    public static readonly ImmutablePath Empty = new(Array.Empty<int>());
    public ImmutablePath Append(int index) => new([.. Items, index]);
}