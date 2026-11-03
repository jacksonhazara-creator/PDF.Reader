using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfReader.App.Localization;
using PdfReader.App.Services;
using PdfReader.Core.Search;
using PdfReader.Core.Settings;
using PdfReader.Rendering;

namespace PdfReader.App.ViewModels;

public enum SidebarTab { Thumbnails, Outline }

public sealed partial class DocumentViewModel : ObservableObject, IAsyncDisposable
{
    private static long s_nextId;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _scanCts;
    private List<ThumbnailViewModel>? _thumbnails;
    private Dictionary<int, List<SearchHit>> _hitsByPage = new();
    private bool _outlineLoaded;

    public DocumentViewModel(MainViewModel main, PdfDocument document, string filePath, DocumentViewState state)
    {
        _main = main;
        Document = document;
        FilePath = filePath;
        CurrentPage = Math.Clamp(state.PageIndex, 0, Math.Max(0, document.PageCount - 1));
        SavedPageOffset = state.PageOffset;
        ZoomMode = state.ZoomMode;
        Zoom = state.Zoom > 0 ? state.Zoom : 1;
        ColorOverride = state.PageColorOverride;
        IsSidebarOpen = main.State.SidebarVisible;
        Security = document.Security;
        _revisions = new int[document.PageCount];
        UpdateSecurityBanner();
    }

    public long Id { get; } = Interlocked.Increment(ref s_nextId);
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

    // --- Sidebar: thumbnails & outline ----------------------------------------------------

    [ObservableProperty] public partial bool IsSidebarOpen { get; set; }
    [ObservableProperty] public partial SidebarTab SidebarTab { get; set; }
    [ObservableProperty] public partial IReadOnlyList<OutlineItemViewModel> Outline { get; set; } = [];

    public bool HasOutline => Outline.Count > 0;
    public bool IsThumbnailsTab { get => SidebarTab == SidebarTab.Thumbnails; set { if (value) SidebarTab = SidebarTab.Thumbnails; } }
    public bool IsOutlineTab { get => SidebarTab == SidebarTab.Outline; set { if (value) SidebarTab = SidebarTab.Outline; } }

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
        if (value == SidebarTab.Outline)
            _ = LoadOutlineAsync();
    }

    partial void OnIsSidebarOpenChanged(bool value)
    {
        _main.State.SidebarVisible = value;
        if (value && SidebarTab == SidebarTab.Outline)
            _ = LoadOutlineAsync();
    }

    partial void OnOutlineChanged(IReadOnlyList<OutlineItemViewModel> value) => OnPropertyChanged(nameof(HasOutline));

    public void ShowSidebar(SidebarTab tab)
    {
        SidebarTab = tab;
        IsSidebarOpen = true;
    }

    public async Task LoadOutlineAsync()
    {
        if (_outlineLoaded)
            return;
        _outlineLoaded = true;
        try
        {
            var items = await Document.GetOutlineAsync();
            Outline = items.Select(i => new OutlineItemViewModel(this, i)).ToList();
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException)
        {
            Outline = [];
        }
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
        IsModified = Document.IsModified;
        OnPropertyChanged(nameof(CanUndo));
        if (_thumbnails is not null && page < _thumbnails.Count)
            _thumbnails[page].Reset();
        PageContentChanged?.Invoke(page);
        CommentsChanged?.Invoke();
    }

    /// <summary>After saving: the file now holds every change.</summary>
    public void MarkSaved() => IsModified = Document.IsModified;

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
