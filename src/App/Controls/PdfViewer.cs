using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PdfReader.App.Localization;
using PdfReader.App.Services;
using PdfReader.App.ViewModels;
using PdfReader.Core.Markup;
using PdfReader.Core.Settings;
using PdfReader.Rendering;

namespace PdfReader.App.Controls;

/// <summary>
/// Continuous-scroll page viewer. Pages are laid out from their sizes alone (instant open),
/// then drawn progressively: a low-res preview per page first, then sharp 512px tiles for just
/// the visible area plus a small prefetch margin. While zooming, the previous zoom level's tiles
/// are stretched as a placeholder. All rendering happens on the PDFium thread; this control only
/// schedules work and composites bitmaps, so the UI thread never waits.
/// </summary>
public sealed class PdfViewer : UserControl
{
    private const int TileSize = 512;
    private const double PageGap = 12;
    private const double PagePadding = 16;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 64;
    private const double PreviewMaxPixels = 360;
    private const double PointsToDip = 96.0 / 72.0;

    private static readonly Cursor IBeamCursor = new(StandardCursorType.Ibeam);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private readonly ScrollViewer _scroll;
    private readonly PageCanvas _canvas;
    private readonly Canvas _overlay;
    private readonly Border _selectionPopup;
    private readonly Dictionary<TileKey, CancellationTokenSource> _pending = new();
    private readonly TileCache _cache = TileCache.Shared;

    private DocumentViewModel? _vm;
    private Rect[] _pageRects = [];
    private double _zoom = 1;
    private RenderConfig _config;
    private RenderConfig? _fallback;
    private bool _configHasTiles;
    private bool _restorePending;
    private bool _updateQueued;
    private bool _firstPageReported;
    private Size _lastViewport;
    private DispatcherTimer? _refitTimer;

    // Selection / hover state
    private bool _selecting;
    private int _selectionPage = -1;
    private int _anchorChar = -1;
    private Point _pressPoint;
    private Point? _pendingDragPoint;
    private bool _dragQueryInFlight;
    private bool _hoverQueryInFlight;
    private Point? _pendingHoverPoint;

    public PdfViewer()
    {
        Focusable = true;
        _canvas = new PageCanvas(this) { Focusable = true };
        _scroll = new ScrollViewer
        {
            Content = _canvas,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        _selectionPopup = BuildSelectionPopup();
        _overlay = new Canvas { Children = { _selectionPopup } };
        Content = new Panel { Children = { _scroll, _overlay } };

        _scroll.ScrollChanged += (_, _) => OnScrolled();
        _scroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty)
                OnViewportChanged();
        };
        _scroll.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerExited += (_, _) => _canvas.Cursor = Cursor.Default;
        _overlay.Children.Add(_commentEditor);
    }

    /// <summary>Raised once, when the first screenful of pages is fully sharp (used for startup benchmarks).</summary>
    public event Action? FirstPageRendered;

    private double DipPerPoint => _zoom * PointsToDip;
    private double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
    private Rect Viewport => new(_scroll.Offset.X, _scroll.Offset.Y, _scroll.Viewport.Width, _scroll.Viewport.Height);

    // ---------------------------------------------------------------------------------------
    // View model attachment
    // ---------------------------------------------------------------------------------------

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Attach(DataContext as DocumentViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Attach(null);
    }

    private void Attach(DocumentViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm))
            return;

        if (_vm is not null)
        {
            SaveScrollPosition();
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.ViewerRequested -= OnViewerRequested;
            _vm.HighlightsChanged -= OnHighlightsChanged;
            _vm.PageContentChanged -= OnPageContentChanged;
            _vm.CommentsChanged -= OnHighlightsChanged;
            _vm.CommentFocusRequested -= OnCommentFocusRequested;
            _vm.Main.Markup.PropertyChanged -= OnMarkupChanged;
            CancelAllPending();
        }

        _vm = vm;
        _pageRects = [];
        _fallback = null;
        _configHasTiles = false;
        _firstPageReported = false;
        HideSelectionPopup();
        HideCommentCard(force: true);
        HideCommentEditor();

        if (vm is not null)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.ViewerRequested += OnViewerRequested;
            vm.HighlightsChanged += OnHighlightsChanged;
            vm.PageContentChanged += OnPageContentChanged;
            vm.CommentsChanged += OnHighlightsChanged;
            vm.CommentFocusRequested += OnCommentFocusRequested;
            vm.Main.Markup.PropertyChanged += OnMarkupChanged;
            _zoom = Math.Clamp(vm.Zoom, MinZoom, MaxZoom);
            _restorePending = true;
            TryRestore();
        }
        _canvas.InvalidateVisual();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DocumentViewModel.Colors):
                UpdateConfig();
                ScheduleUpdate();
                _canvas.InvalidateVisual();
                break;
            case nameof(DocumentViewModel.Selection):
                if (_vm?.Selection is null)
                {
                    HideSelectionPopup();
                    HideCommentEditor();
                }
                _canvas.InvalidateVisual();
                break;
        }
    }

    private void OnHighlightsChanged() => _canvas.InvalidateVisual();

    private void OnMarkupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MarkupViewModel.Tool))
            ResetToolState();
    }

    private void OnPageContentChanged(int page)
    {
        ScheduleUpdate();
        _canvas.InvalidateVisual();
    }

    private void OnViewerRequested(ViewerRequest request)
    {
        if (_vm is null)
            return;
        switch (request)
        {
            case ViewerRequest.GoToPage g:
                ScrollToPage(g.PageIndex, g.Offset);
                break;
            case ViewerRequest.ZoomStep z:
                SetZoom(NextZoomStep(_zoom, z.Direction), null, ZoomMode.Custom);
                break;
            case ViewerRequest.SetZoom z:
                SetZoom(z.Zoom, null, ZoomMode.Custom);
                break;
            case ViewerRequest.SetZoomMode m:
                SetZoom(ZoomForMode(m.Mode), null, m.Mode);
                break;
            case ViewerRequest.Reveal r:
                Reveal(r.PageIndex, r.Rect);
                break;
            case ViewerRequest.ScrollByPage s:
                _scroll.Offset = new Vector(_scroll.Offset.X, _scroll.Offset.Y + s.Direction * _scroll.Viewport.Height * 0.9);
                break;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Layout, zoom, scrolling
    // ---------------------------------------------------------------------------------------

    private void OnViewportChanged()
    {
        var viewport = _scroll.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0)
            return;
        if (TryRestore())
            return;
        if (_vm is null || viewport == _lastViewport)
            return;
        var widthChanged = Math.Abs(viewport.Width - _lastViewport.Width) > 0.5;
        _lastViewport = viewport;
        Relayout(keepPosition: true);
        if (_vm.ZoomMode is ZoomMode.FitWidth or ZoomMode.FitPage && (widthChanged || _vm.ZoomMode == ZoomMode.FitPage))
        {
            // While a side panel animates, the size changes every frame: shift the pages now and
            // re-fit once it settles, instead of re-rendering at a new zoom on every frame.
            _refitTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(90), DispatcherPriority.Background, (_, _) =>
            {
                _refitTimer!.Stop();
                if (_vm is { ZoomMode: ZoomMode.FitWidth or ZoomMode.FitPage } v)
                    SetZoom(ZoomForMode(v.ZoomMode), null, v.ZoomMode, force: true);
            });
            _refitTimer.Stop();
            _refitTimer.Start();
        }
    }

    /// <summary>Applies the saved zoom and scroll position once the viewport size is known.</summary>
    private bool TryRestore()
    {
        if (!_restorePending || _vm is null || _scroll.Viewport.Width <= 0)
            return false;
        _restorePending = false;
        _lastViewport = _scroll.Viewport;
        if (_vm.ZoomMode != ZoomMode.Custom)
            _zoom = ZoomForMode(_vm.ZoomMode);
        Relayout(keepPosition: false);
        ScrollToPage(_vm.CurrentPage, _vm.SavedPageOffset);
        _vm.Zoom = _zoom;
        return true;
    }

    private void Relayout(bool keepPosition)
    {
        if (_vm is null)
            return;
        var anchor = keepPosition ? CaptureAnchor(new Point(0, 0)) : default;

        var sizes = _vm.Document.PageSizes;
        var dip = DipPerPoint;
        var scaling = RenderScaling;
        var rects = new Rect[sizes.Count];
        double maxWidth = 0, y = PagePadding;
        for (var i = 0; i < sizes.Count; i++)
        {
            var w = sizes[i].Width * dip;
            var h = sizes[i].Height * dip;
            rects[i] = new Rect(0, y, w, h);
            maxWidth = Math.Max(maxWidth, w);
            y += h + PageGap;
        }

        var extentWidth = Math.Max(_scroll.Viewport.Width, maxWidth + 2 * PagePadding);
        var extentHeight = Math.Max(y - PageGap + PagePadding, _scroll.Viewport.Height);
        for (var i = 0; i < rects.Length; i++)
        {
            var x = Math.Round((extentWidth - rects[i].Width) / 2 * scaling) / scaling; // device-pixel aligned
            rects[i] = rects[i].WithX(x);
        }

        _pageRects = rects;
        _canvas.Width = extentWidth;
        _canvas.Height = extentHeight;
        _scroll.UpdateLayout();
        UpdateConfig();

        if (keepPosition && anchor.Page >= 0)
            RestoreAnchor(anchor, new Point(0, 0));
        ScheduleUpdate();
        _canvas.InvalidateVisual();
    }

    private void SetZoom(double zoom, Point? anchorInViewport, ZoomMode mode, bool force = false)
    {
        if (_vm is null)
            return;
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _vm.ZoomMode = mode;
        if (!force && Math.Abs(zoom - _zoom) < 1e-4 && _pageRects.Length > 0)
            return;

        var anchorPoint = anchorInViewport ?? new Point(_scroll.Viewport.Width / 2, mode is ZoomMode.Custom ? _scroll.Viewport.Height / 2 : 0);
        var anchor = CaptureAnchor(anchorPoint);
        _zoom = zoom;
        Relayout(keepPosition: false);
        if (anchor.Page >= 0)
            RestoreAnchor(anchor, anchorPoint);
        _vm.Zoom = zoom;
    }

    private double ZoomForMode(ZoomMode mode)
    {
        if (_vm is null || _vm.PageCount == 0)
            return _zoom;
        var viewport = _scroll.Viewport;
        var page = _vm.Document.PageSizes[Math.Clamp(_vm.CurrentPage, 0, _vm.PageCount - 1)];
        var maxWidth = _vm.Document.PageSizes.Max(s => s.Width);
        var fitWidth = (viewport.Width - 2 * PagePadding) / (maxWidth * PointsToDip);
        return mode switch
        {
            ZoomMode.FitWidth => Math.Clamp(fitWidth, MinZoom, MaxZoom),
            ZoomMode.FitPage => Math.Clamp(Math.Min((viewport.Width - 2 * PagePadding) / (page.Width * PointsToDip),
                (viewport.Height - 2 * PagePadding) / (page.Height * PointsToDip)), MinZoom, MaxZoom),
            ZoomMode.ActualSize => 1.0,
            _ => _zoom,
        };
    }

    private static readonly double[] ZoomSteps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5, 6.4, 8, 12, 16, 24, 32, 48, 64];

    private static double NextZoomStep(double current, int direction)
        => direction > 0
            ? ZoomSteps.FirstOrDefault(s => s > current * 1.01, MaxZoom)
            : ZoomSteps.LastOrDefault(s => s < current * 0.99, MinZoom);

    private readonly record struct Anchor(int Page, double FractionX, double FractionY);

    private Anchor CaptureAnchor(Point viewportPoint)
    {
        if (_pageRects.Length == 0)
            return new Anchor(-1, 0, 0);
        var doc = new Point(_scroll.Offset.X + viewportPoint.X, _scroll.Offset.Y + viewportPoint.Y);
        var page = PageNearest(doc.Y);
        var r = _pageRects[page];
        return new Anchor(page, (doc.X - r.X) / r.Width, (doc.Y - r.Y) / r.Height);
    }

    private void RestoreAnchor(Anchor anchor, Point viewportPoint)
    {
        var r = _pageRects[anchor.Page];
        var x = r.X + anchor.FractionX * r.Width - viewportPoint.X;
        var y = r.Y + anchor.FractionY * r.Height - viewportPoint.Y;
        _scroll.Offset = new Vector(Math.Max(0, x), Math.Max(0, y));
    }

    private void ScrollToPage(int pageIndex, double offsetFraction)
    {
        if (_pageRects.Length == 0)
            return;
        pageIndex = Math.Clamp(pageIndex, 0, _pageRects.Length - 1);
        var r = _pageRects[pageIndex];
        var y = offsetFraction <= 0 ? r.Y - PageGap / 2 : r.Y + offsetFraction * r.Height;
        _scroll.Offset = new Vector(_scroll.Offset.X, Math.Max(0, y));
        if (_vm is not null)
            _vm.CurrentPage = pageIndex;
    }

    /// <summary>Scrolls so a rectangle (page points) is comfortably visible, e.g. a search hit.</summary>
    private void Reveal(int pageIndex, PdfRect rect)
    {
        if ((uint)pageIndex >= (uint)_pageRects.Length)
            return;
        var page = _pageRects[pageIndex];
        var dip = DipPerPoint;
        var target = new Rect(page.X + rect.X * dip, page.Y + rect.Y * dip, rect.Width * dip, rect.Height * dip);
        var vp = Viewport;
        var margin = 80.0;
        var x = vp.X;
        var y = vp.Y;
        if (target.Y < vp.Y + margin || target.Bottom > vp.Bottom - margin)
            y = target.Y - vp.Height / 3;
        if (target.X < vp.X + margin || target.Right > vp.Right - margin)
            x = target.X - vp.Width / 3;
        _scroll.Offset = new Vector(Math.Max(0, x), Math.Max(0, y));
        _canvas.InvalidateVisual();
    }

    private void OnScrolled()
    {
        UpdateCurrentPage();
        SaveScrollPosition();
        PositionSelectionPopup();
        ScheduleUpdate();
        _canvas.InvalidateVisual();
    }

    private void UpdateCurrentPage()
    {
        if (_vm is null || _pageRects.Length == 0)
            return;
        var vp = Viewport;
        // The page occupying the upper third of the viewport is "current".
        var page = PageNearest(vp.Y + vp.Height / 3);
        if (_vm.CurrentPage != page)
            _vm.CurrentPage = page;
    }

    private void SaveScrollPosition()
    {
        if (_vm is null || _pageRects.Length == 0)
            return;
        var page = Math.Clamp(_vm.CurrentPage, 0, _pageRects.Length - 1);
        var r = _pageRects[page];
        _vm.SavedPageOffset = Math.Clamp((_scroll.Offset.Y - r.Y) / r.Height, 0, 1);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0 || _vm is null)
            return;
        e.Handled = true;
        var factor = Math.Pow(1.15, e.Delta.Y);
        SetZoom(_zoom * factor, e.GetPosition(_scroll), ZoomMode.Custom);
    }

    // ---------------------------------------------------------------------------------------
    // Tile scheduling
    // ---------------------------------------------------------------------------------------

    private readonly record struct RenderConfig(long ScaleKey, double Scale, PageColors Colors);

    private void UpdateConfig()
    {
        if (_vm is null)
            return;
        var scale = DipPerPoint * RenderScaling;
        var config = new RenderConfig((long)Math.Round(scale * 1000), scale, _vm.Colors);
        if (config == _config)
            return;
        // Keep the old config's tiles as a stretched placeholder while the new ones render.
        if (_configHasTiles && config.Colors == _config.Colors)
            _fallback = _config;
        else if (config.Colors != _config.Colors)
            _fallback = null;
        _config = config;
        _configHasTiles = false;
    }

    private void ScheduleUpdate()
    {
        if (_updateQueued)
            return;
        _updateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _updateQueued = false;
            UpdateRequests();
        }, DispatcherPriority.Background);
    }

    private TileKey PreviewKey(int page) => new(_vm!.Id, page, -1, _config.Colors, 0, 0, _vm.RevisionOf(page));

    private void UpdateRequests()
    {
        if (_vm is null || _pageRects.Length == 0 || _vm.Document.IsDisposed)
            return;

        var vp = Viewport;
        if (vp.Width <= 0 || vp.Height <= 0)
            return;
        var prefetch = new Rect(vp.X, vp.Y - vp.Height * 0.5, vp.Width, vp.Height * 2.5);
        var previewZone = new Rect(vp.X, vp.Y - vp.Height * 2, vp.Width, vp.Height * 5);
        var center = vp.Center;
        var wanted = new List<(TileKey Key, RenderRequest Request, WorkPriority Priority, double Distance)>();

        var (first, last) = PagesIntersecting(previewZone);
        for (var page = first; page <= last; page++)
        {
            var r = _pageRects[page];
            var visible = r.Intersects(vp);
            var distance = Math.Abs(r.Center.Y - center.Y);

            var previewKey = PreviewKey(page);
            if (!_cache.Contains(previewKey))
            {
                var size = _vm.Document.PageSizes[page];
                var previewScale = PreviewMaxPixels / Math.Max(size.Width, size.Height);
                var (pw, ph) = _vm.Document.GetPixelSize(page, previewScale);
                wanted.Add((previewKey, new RenderRequest(page, previewScale, 0, 0, pw, ph, _config.Colors),
                    visible ? WorkPriority.Visible : WorkPriority.Prefetch, distance - 1e6));
            }

            if (!r.Intersects(prefetch))
                continue;
            foreach (var (key, request, tileRect) in TilesFor(page, _config, prefetch))
            {
                if (_cache.Contains(key))
                    continue;
                var tileVisible = tileRect.Intersects(vp);
                wanted.Add((key, request, tileVisible ? WorkPriority.Visible : WorkPriority.Prefetch,
                    Math.Abs(tileRect.Center.Y - center.Y)));
            }
        }

        var wantedKeys = new HashSet<TileKey>(wanted.Count);
        foreach (var w in wanted)
            wantedKeys.Add(w.Key);

        // Cancel work that scrolled out of range (PDFium stops mid-page via the pause callback).
        foreach (var (key, cts) in _pending.ToList())
        {
            if (!wantedKeys.Contains(key))
            {
                cts.Cancel();
                _pending.Remove(key);
            }
        }

        foreach (var w in wanted.OrderBy(w => w.Priority).ThenBy(w => w.Distance))
        {
            if (_pending.ContainsKey(w.Key))
                continue;
            StartRender(w.Key, w.Request, w.Priority);
        }
    }

    private void StartRender(TileKey key, RenderRequest request, WorkPriority priority)
    {
        var vm = _vm!;
        var cts = new CancellationTokenSource();
        _pending[key] = cts;
        vm.Document.RenderAsync(request, priority, cts.Token).ContinueWith(task =>
        {
            Bitmap? bitmap = null;
            if (task.IsCompletedSuccessfully)
            {
                try { bitmap = BitmapFactory.Create(task.Result); }
                catch (Exception) { /* out of memory etc.: leave the tile empty */ }
            }
            else if (task.IsFaulted)
            {
                _ = task.Exception; // observed; damaged pages just stay blank
            }

            Dispatcher.UIThread.Post(() => OnTileRendered(vm, key, cts, bitmap));
        }, TaskScheduler.Default);
    }

    private void OnTileRendered(DocumentViewModel vm, TileKey key, CancellationTokenSource cts, Bitmap? bitmap)
    {
        if (_pending.TryGetValue(key, out var current) && current == cts)
            _pending.Remove(key);
        cts.Dispose();

        if (bitmap is null)
            return;
        if (vm.Document.IsDisposed)
        {
            bitmap.Dispose();
            return;
        }

        _cache.Add(key, bitmap);
        if (vm == _vm && key.ScaleKey == _config.ScaleKey && key.Colors == _config.Colors)
            _configHasTiles = true;
        if (vm == _vm)
        {
            _canvas.InvalidateVisual();
            if (!_firstPageReported && key.ScaleKey >= 0 && AllVisibleTilesReady())
            {
                _firstPageReported = true;
                FirstPageRendered?.Invoke();
            }
        }
    }

    private bool AllVisibleTilesReady()
    {
        var vp = Viewport;
        var (first, last) = PagesIntersecting(vp);
        for (var page = first; page <= last; page++)
            foreach (var (key, _, _) in TilesFor(page, _config, vp))
                if (!_cache.Contains(key))
                    return false;
        return true;
    }

    private void CancelAllPending()
    {
        foreach (var cts in _pending.Values)
            cts.Cancel();
        _pending.Clear();
    }

    /// <summary>Tiles of one page (in the given config's pixel grid) that intersect a document-space area.</summary>
    private IEnumerable<(TileKey Key, RenderRequest Request, Rect DocRect)> TilesFor(int page, RenderConfig config, Rect area)
    {
        var r = _pageRects[page];
        var (pw, ph) = _vm!.Document.GetPixelSize(page, config.Scale);
        var toDip = r.Width / pw;
        var local = area.Intersect(r);
        if (local.Width <= 0 || local.Height <= 0)
            yield break;

        var x0 = Math.Max(0, (int)((local.X - r.X) / toDip) / TileSize);
        var y0 = Math.Max(0, (int)((local.Y - r.Y) / toDip) / TileSize);
        var x1 = Math.Min((pw - 1) / TileSize, (int)((local.Right - r.X) / toDip) / TileSize);
        var y1 = Math.Min((ph - 1) / TileSize, (int)((local.Bottom - r.Y) / toDip) / TileSize);

        for (var ty = y0; ty <= y1; ty++)
        {
            for (var tx = x0; tx <= x1; tx++)
            {
                var px = tx * TileSize;
                var py = ty * TileSize;
                var w = Math.Min(TileSize, pw - px);
                var h = Math.Min(TileSize, ph - py);
                var key = new TileKey(_vm.Id, page, config.ScaleKey, config.Colors, tx, ty, _vm.RevisionOf(page));
                var docRect = new Rect(r.X + px * toDip, r.Y + py * toDip, w * toDip, h * toDip);
                yield return (key, new RenderRequest(page, config.Scale, px, py, w, h, config.Colors), docRect);
            }
        }
    }

    private (int First, int Last) PagesIntersecting(Rect area)
    {
        if (_pageRects.Length == 0)
            return (0, -1);
        var first = LowerBound(area.Top);
        var last = first;
        while (last + 1 < _pageRects.Length && _pageRects[last + 1].Top <= area.Bottom)
            last++;
        return (first, last);
    }

    /// <summary>First page whose bottom is at or below <paramref name="y"/>.</summary>
    private int LowerBound(double y)
    {
        int lo = 0, hi = _pageRects.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_pageRects[mid].Bottom < y) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private int PageNearest(double y) => _pageRects.Length == 0 ? -1 : LowerBound(y - PageGap);

    private int PageAt(Point docPoint)
    {
        if (_pageRects.Length == 0)
            return -1;
        var page = LowerBound(docPoint.Y);
        return _pageRects[page].Contains(docPoint) ? page : -1;
    }

    private Point ToPagePoints(int page, Point docPoint)
    {
        var r = _pageRects[page];
        var dip = DipPerPoint;
        return new Point((docPoint.X - r.X) / dip, (docPoint.Y - r.Y) / dip);
    }

    // ---------------------------------------------------------------------------------------
    // Drawing
    // ---------------------------------------------------------------------------------------

    private static readonly BoxShadows PageShadow = BoxShadows.Parse("0 1 4 0 #33000000");
    private static readonly IBrush SearchBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xD5, 0x00));
    private static readonly IBrush CurrentSearchBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0x8C, 0x00));

    private void RenderPages(DrawingContext ctx)
    {
        if (_vm is null || _pageRects.Length == 0)
            return;

        var vp = Viewport;
        var colors = _config.Colors;
        var pageBrush = new SolidColorBrush(Color.FromUInt32(colors.Background));
        var selectionBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x33, 0x88, 0xFF));
        var dip = DipPerPoint;
        var (first, last) = PagesIntersecting(vp);
        var missing = 0;

        for (var page = first; page <= last; page++)
        {
            var r = _pageRects[page];
            ctx.DrawRectangle(pageBrush, null, new RoundedRect(r, 2), PageShadow);

            using (ctx.PushClip(r))
            {
                var previewKey = PreviewKey(page);
                if ((_cache.Get(previewKey) ?? _cache.Get(previewKey with { Revision = previewKey.Revision - 1 })) is { } preview)
                    ctx.DrawImage(preview, new Rect(preview.Size), r);

                if (_fallback is { } fallback)
                    DrawTiles(ctx, page, fallback, vp, onlyCached: true);
                missing += DrawTiles(ctx, page, _config, vp, onlyCached: false);
            }

            // Search highlights
            if (_vm.HitsOnPage(page) is { } hits)
            {
                var current = _vm.CurrentHit;
                foreach (var hit in hits)
                {
                    var brush = ReferenceEquals(hit, current) ? CurrentSearchBrush : SearchBrush;
                    foreach (var rect in hit.Rects)
                        ctx.FillRectangle(brush, ToDoc(r, rect, dip).Inflate(1));
                }
            }

            // Text selection
            if (_vm.Selection is { PageIndex: var selPage } selection && selPage == page)
                foreach (var rect in selection.Rects)
                    ctx.FillRectangle(selectionBrush, ToDoc(r, rect, dip));

            DrawEditOverlays(ctx, page, r, dip);
        }

        TrackBlur(missing);
    }

    private long _blurrySince;

    /// <summary>Diagnostics: how long the viewport showed placeholders instead of sharp tiles.</summary>
    private void TrackBlur(int missingTiles)
    {
        if (!PerfLog.Enabled)
            return;
        if (missingTiles > 0 && _blurrySince == 0)
            _blurrySince = System.Diagnostics.Stopwatch.GetTimestamp();
        else if (missingTiles == 0 && _blurrySince != 0)
        {
            PerfLog.Blurry(System.Diagnostics.Stopwatch.GetElapsedTime(_blurrySince).TotalMilliseconds);
            _blurrySince = 0;
        }
    }

    /// <returns>Number of tiles not yet rendered.</returns>
    private int DrawTiles(DrawingContext ctx, int page, RenderConfig config, Rect vp, bool onlyCached)
    {
        var missing = 0;
        foreach (var (key, _, docRect) in TilesFor(page, config, vp))
        {
            if (_cache.Get(key, touch: !onlyCached) is { } bitmap)
            {
                ctx.DrawImage(bitmap, new Rect(bitmap.Size), docRect);
                continue;
            }
            missing++;
            // Just edited: keep showing the previous version until the new tile arrives (no flash).
            if (!onlyCached && _cache.Get(key with { Revision = key.Revision - 1 }, touch: false) is { } previous)
                ctx.DrawImage(previous, new Rect(previous.Size), docRect);
        }
        return missing;
    }

    private static Rect ToDoc(Rect page, PdfRect rect, double dip)
        => new(page.X + rect.X * dip, page.Y + rect.Y * dip, rect.Width * dip, rect.Height * dip);

    // ---------------------------------------------------------------------------------------
    // Pointer: every tool (select, pan, text marks, freehand, shapes, click-to-place), links, hover
    // ---------------------------------------------------------------------------------------

    private enum DragMode { None, Text, PendingMarker, Freehand, Shape, Pan }

    private DragMode _drag;
    private bool _textDragIsMarker;
    private readonly List<PdfPoint> _inkPoints = [];
    private PdfPoint _shapeStart, _shapeEnd;
    private readonly List<PdfPoint> _multiPoints = [];
    private int _multiPage = -1;
    private PdfPoint? _multiHover;
    private Point _panStart;
    private Vector _panOffset;
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor PanCursor = new(StandardCursorType.SizeAll);

    private MarkupViewModel? Markup => _vm?.Main.Markup;
    private ToolKind Tool => Markup?.Tool ?? ToolKind.Select;
    private ToolInput Input => Markup?.Input ?? ToolInput.Select;

    /// <summary>Called when the tool changes: drop half-finished shapes.</summary>
    public void ResetToolState()
    {
        _multiPoints.Clear();
        _multiPage = -1;
        _multiHover = null;
        _inkPoints.Clear();
        _drag = DragMode.None;
        HideCommentEditor();
        _canvas.Cursor = Input switch { ToolInput.Pan => PanCursor, ToolInput.Select or ToolInput.TextSelect => IBeamCursor, _ => CrossCursor };
        _canvas.InvalidateVisual();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is not { } vm)
            return;
        var props = e.GetCurrentPoint(_canvas).Properties;
        var point = e.GetPosition(_canvas);

        if (props.IsRightButtonPressed)
        {
            _ = ShowContextMenuAsync(point);
            e.Handled = true;
            return;
        }

        // Middle button always pans, as does the Pan tool.
        if (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && Input == ToolInput.Pan))
        {
            _drag = DragMode.Pan;
            _panStart = e.GetPosition(this);
            _panOffset = _scroll.Offset;
            e.Pointer.Capture(_canvas);
            _canvas.Cursor = PanCursor;
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed)
            return;
        _canvas.Focus();
        _pressPoint = point;

        if (Input is ToolInput.Select or ToolInput.TextSelect && CommentAt(point) is { } comment)
        {
            ShowCommentCard(comment, pinned: true);
            e.Handled = true;
            return;
        }
        HideCommentCard(force: true);
        HideSelectionPopup();
        if (Input != ToolInput.Click)
            HideCommentEditor();
        vm.Selection = null;

        var page = PageAt(point);
        if (page < 0)
            return;
        var pt = ToPagePoints(page, point);
        e.Pointer.Capture(_canvas);
        _selectionPage = page;
        _anchorChar = -1;

        switch (Input)
        {
            case ToolInput.Select:
            case ToolInput.TextSelect when Tool != ToolKind.Highlight:
                _drag = DragMode.Text;
                _textDragIsMarker = Input == ToolInput.TextSelect;
                _selecting = true;
                _ = SetAnchorAsync(page, pt);
                break;

            case ToolInput.TextSelect: // Highlight: on text mark it, elsewhere (images) draw freehand
                _drag = DragMode.PendingMarker;
                _inkPoints.Clear();
                _inkPoints.Add(new PdfPoint(pt.X, pt.Y));
                _ = DecideMarkerModeAsync(page, pt);
                break;

            case ToolInput.Freehand:
                _drag = DragMode.Freehand;
                _inkPoints.Clear();
                _inkPoints.Add(new PdfPoint(pt.X, pt.Y));
                break;

            case ToolInput.DragShape:
                _drag = DragMode.Shape;
                _shapeStart = _shapeEnd = new PdfPoint(pt.X, pt.Y);
                break;

            case ToolInput.MultiClick:
                if (_multiPage != page)
                {
                    _multiPoints.Clear();
                    _multiPage = page;
                }
                if (e.ClickCount >= 2 && _multiPoints.Count >= 2)
                {
                    _ = FinishMultiClickAsync();
                }
                else
                {
                    _multiPoints.Add(new PdfPoint(pt.X, pt.Y));
                    _canvas.InvalidateVisual();
                }
                e.Pointer.Capture(null);
                break;

            case ToolInput.Click:
                e.Pointer.Capture(null);
                _ = PlaceAtAsync(page, new PdfPoint(pt.X, pt.Y), point);
                break;
        }
        e.Handled = true;
    }

    private async Task DecideMarkerModeAsync(int page, Point pt)
    {
        try
        {
            var index = await _vm!.Document.GetCharIndexAtAsync(page, pt.X, pt.Y, tolerance: 2);
            if (_drag != DragMode.PendingMarker)
                return;
            if (index >= 0)
            {
                _drag = DragMode.Text;
                _textDragIsMarker = true;
                _selecting = true;
                _anchorChar = index;
                _inkPoints.Clear();
                if (_pendingDragPoint is null && _lastMovePoint is { } last)
                    _pendingDragPoint = last;
                if (!_dragQueryInFlight)
                    _ = UpdateDragSelectionAsync();
            }
            else
            {
                _drag = DragMode.Freehand;
            }
            _canvas.InvalidateVisual();
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException) { _drag = DragMode.Freehand; }
    }

    private async Task SetAnchorAsync(int page, Point pt)
    {
        try
        {
            _anchorChar = await _vm!.Document.GetCharIndexAtAsync(page, pt.X, pt.Y, tolerance: 12);
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException) { }
    }

    private Point? _lastMovePoint;

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_vm is null)
            return;
        var point = e.GetPosition(_canvas);
        _lastMovePoint = point;
        switch (_drag)
        {
            case DragMode.Pan:
                var now = e.GetPosition(this);
                _scroll.Offset = new Vector(Math.Max(0, _panOffset.X - (now.X - _panStart.X)), Math.Max(0, _panOffset.Y - (now.Y - _panStart.Y)));
                return;
            case DragMode.Text when _selecting:
                _pendingDragPoint = point;
                if (!_dragQueryInFlight)
                    _ = UpdateDragSelectionAsync();
                return;
            case DragMode.PendingMarker or DragMode.Freehand:
                var pt = ClampToPage(_selectionPage, point);
                var lastPt = _inkPoints[^1];
                if (Math.Abs(pt.X - lastPt.X) + Math.Abs(pt.Y - lastPt.Y) > 0.6)
                {
                    _inkPoints.Add(new PdfPoint(pt.X, pt.Y));
                    _canvas.InvalidateVisual();
                }
                return;
            case DragMode.Shape:
                var sp = ClampToPage(_selectionPage, point);
                _shapeEnd = new PdfPoint(sp.X, sp.Y);
                _canvas.InvalidateVisual();
                return;
        }

        if (Input == ToolInput.MultiClick && _multiPage >= 0 && _multiPoints.Count > 0)
        {
            var mp = ClampToPage(_multiPage, point);
            _multiHover = new PdfPoint(mp.X, mp.Y);
            _canvas.InvalidateVisual();
        }

        // Hovering: comment markers first (instant, from cache), then links (async).
        if (Input is ToolInput.Select or ToolInput.TextSelect && CommentAt(point) is { } comment)
        {
            _canvas.Cursor = HandCursor;
            ToolTip.SetTip(_canvas, null);
            ShowCommentCard(comment, pinned: false);
            return;
        }
        HideCommentCard(force: false);
        if (Input is not (ToolInput.Select or ToolInput.TextSelect))
        {
            _canvas.Cursor = Input == ToolInput.Pan ? PanCursor : PageAt(point) >= 0 ? CrossCursor : Cursor.Default;
            return;
        }
        _pendingHoverPoint = point;
        if (!_hoverQueryInFlight)
            _ = UpdateHoverAsync();
    }

    private async Task UpdateDragSelectionAsync()
    {
        _dragQueryInFlight = true;
        try
        {
            while (_pendingDragPoint is { } point && _selecting && _vm is { } vm)
            {
                _pendingDragPoint = null;
                if (_anchorChar < 0)
                {
                    // Pressed outside text: anchor on the first character the drag reaches.
                    var startPt = ToPagePoints(_selectionPage, _pressPoint);
                    _anchorChar = await vm.Document.GetCharIndexAtAsync(_selectionPage, startPt.X, startPt.Y, tolerance: 30);
                    if (_anchorChar < 0)
                    {
                        var pt0 = ClampToPage(_selectionPage, point);
                        _anchorChar = await vm.Document.GetCharIndexAtAsync(_selectionPage, pt0.X, pt0.Y, tolerance: 30);
                        if (_anchorChar < 0)
                            continue;
                    }
                }
                var pt = ClampToPage(_selectionPage, point);
                var current = await vm.Document.GetCharIndexAtAsync(_selectionPage, pt.X, pt.Y, tolerance: 30);
                if (current < 0 || !_selecting)
                    continue;
                vm.Selection = await vm.Document.GetSelectionAsync(_selectionPage, _anchorChar, current);
            }
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException) { }
        finally
        {
            _dragQueryInFlight = false;
        }
    }

    private Point ClampToPage(int page, Point docPoint)
    {
        var r = _pageRects[page];
        var clamped = new Point(Math.Clamp(docPoint.X, r.X, r.Right), Math.Clamp(docPoint.Y, r.Y, r.Bottom));
        return ToPagePoints(page, clamped);
    }

    private async Task UpdateHoverAsync()
    {
        _hoverQueryInFlight = true;
        try
        {
            while (_pendingHoverPoint is { } point && _vm is { } vm)
            {
                _pendingHoverPoint = null;
                var page = PageAt(point);
                if (page < 0)
                {
                    _canvas.Cursor = Cursor.Default;
                    ToolTip.SetTip(_canvas, null);
                    continue;
                }
                var pt = ToPagePoints(page, point);
                var link = Input == ToolInput.Select ? await vm.Document.GetLinkAtAsync(page, pt.X, pt.Y) : null;
                _canvas.Cursor = link is null ? IBeamCursor : HandCursor;
                ToolTip.SetTip(_canvas, link?.Kind switch
                {
                    LinkKind.Uri => link.Target,
                    LinkKind.Launch or LinkKind.RemoteFile => $"⚠ {link.Target}",
                    _ => null,
                });
            }
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException) { }
        finally
        {
            _hoverQueryInFlight = false;
        }
    }

    private async void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_vm is not { } vm || _drag == DragMode.None)
            return;
        var mode = _drag;
        _drag = DragMode.None;
        e.Pointer.Capture(null);
        var point = e.GetPosition(_canvas);
        var isClick = Math.Abs(point.X - _pressPoint.X) < 4 && Math.Abs(point.Y - _pressPoint.Y) < 4;
        var page = _selectionPage;

        switch (mode)
        {
            case DragMode.Pan:
                ResetToolState();
                return;

            case DragMode.PendingMarker or DragMode.Freehand:
            {
                var points = _inkPoints.ToList();
                _inkPoints.Clear();
                _canvas.InvalidateVisual();
                if (isClick || points.Count < 2 || Markup is not { } m)
                    return;
                if (Tool == ToolKind.Highlight)
                    await vm.InkAsync(page, [points], m.MarkerArgb, _vm.Main.Settings.MarkerThickness);
                else
                    await vm.InkAsync(page, [points], m.DrawArgb, m.DrawThickness);
                return;
            }

            case DragMode.Shape:
                _canvas.InvalidateVisual();
                if (!isClick)
                    await FinishShapeAsync(page, _shapeStart, _shapeEnd);
                return;
        }

        // Text drag
        _selecting = false;
        if (isClick && Input == ToolInput.Select)
        {
            vm.Selection = null;
            _ = ActivateLinkAtAsync(page, ToPagePoints(page, point));
            return;
        }
        while (_dragQueryInFlight)
            await Task.Delay(10);
        if (vm.Selection is not { IsEmpty: false } selection)
            return;

        switch (Tool)
        {
            case ToolKind.Highlight: await vm.MarkTextAsync(selection, TextMarkupKind.Highlight); break;
            case ToolKind.Underline: await vm.MarkTextAsync(selection, TextMarkupKind.Underline); break;
            case ToolKind.StrikeOut: await vm.MarkTextAsync(selection, TextMarkupKind.StrikeOut); break;
            case ToolKind.Comment: ShowCommentEditor(); break;
            case ToolKind.ReplaceText:
                ShowEditor("ToolReplace", "ReplacePlaceholder", "IconReplace", selection.Text, SelectionAnchor(selection),
                    text => vm.ReplaceTextAsync(selection, text));
                break;
            default: ShowSelectionPopup(); break;
        }
    }

    private async Task ActivateLinkAtAsync(int page, Point pt)
    {
        try
        {
            if (_vm is { } vm && await vm.Document.GetLinkAtAsync(page, pt.X, pt.Y) is { } link)
                await vm.ActivateLinkAsync(link);
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException) { }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Input == ToolInput.MultiClick && _multiPoints.Count > 0)
        {
            if (e.Key == Key.Enter) { _ = FinishMultiClickAsync(); e.Handled = true; }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Building marks from tool input
    // ---------------------------------------------------------------------------------------

    private async Task FinishShapeAsync(int page, PdfPoint a, PdfPoint b)
    {
        if (_vm is not { } vm || Markup is not { } m)
            return;
        var rect = PdfRect.FromEdges(a.X, a.Y, b.X, b.Y);
        var color = m.DrawArgb;
        var width = m.DrawThickness;
        switch (Tool)
        {
            case ToolKind.Line or ToolKind.Crossout:
                await vm.InkAsync(page, [[a, b]], color, width);
                break;
            case ToolKind.LineArrow:
                await vm.InkAsync(page, [[a, b], ShapeBuilder.ArrowHead(a, b, Math.Max(8, width * 4))], color, width);
                break;
            case ToolKind.Arrow:
                await vm.StampAsync(page, [new StampPath(ShapeBuilder.BlockArrow(a, b, Math.Max(6, width * 3)), true, color, 0.8, color)], "Arrow");
                break;
            case ToolKind.Rectangle:
                await vm.ShapeAsync(page, ShapeKind.Rectangle, rect, color, width);
                break;
            case ToolKind.Circle or ToolKind.CircleAround:
                await vm.ShapeAsync(page, ShapeKind.Ellipse, rect, color, width);
                break;
            case ToolKind.Cloud:
                await vm.InkAsync(page, [ShapeBuilder.Cloud(rect, Math.Max(10, width * 5))], color, width);
                break;
            case ToolKind.Callout:
                // Drag from the thing you point at to where the text box should be.
                var anchorDoc = ToDoc(_pageRects[page], new PdfRect(b.X, b.Y, 1, 1), DipPerPoint);
                ShowEditor("ToolCallout", "TextPlaceholder", "IconCallout", null, anchorDoc,
                    text => PlaceCalloutAsync(page, a, b, text));
                break;
        }
    }

    private async Task FinishMultiClickAsync()
    {
        if (_vm is not { } vm || Markup is not { } m || _multiPoints.Count < 2)
            return;
        var points = _multiPoints.ToList();
        var page = _multiPage;
        _multiPoints.Clear();
        _multiPage = -1;
        _multiHover = null;
        _canvas.InvalidateVisual();
        if (Tool == ToolKind.Polygon)
            points.Add(points[0]);
        await vm.InkAsync(page, [points], m.DrawArgb, m.DrawThickness);
    }

    private async Task PlaceAtAsync(int page, PdfPoint at, Point docPoint)
    {
        if (_vm is not { } vm || Markup is not { } m)
            return;
        var anchor = new Rect(docPoint, new Size(1, 1));
        switch (Tool)
        {
            case ToolKind.InsertText:
                ShowEditor("ToolInsert", "InsertPlaceholder", "IconInsert", null, anchor, text => vm.InsertTextAsync(page, at, text));
                break;
            case ToolKind.TextComment:
                ShowEditor("ToolTextComment", "CommentPlaceholder", "IconTextComment", null, anchor, text => PlaceTextAsync(page, at, text, boxed: true));
                break;
            case ToolKind.TypeText:
                ShowEditor("ToolTypeText", "TextPlaceholder", "IconTypeText", null, anchor, text => PlaceTextAsync(page, at, text, boxed: false));
                break;
            case ToolKind.AttachFile:
                if (await vm.Main.PickAttachmentAsync() is { } file)
                    await vm.AttachFileAsync(page, at, file);
                break;
            case ToolKind.Checkmark:
                await vm.StampAsync(page, [new StampPath(ShapeBuilder.CheckMark(at, 14), false, m.DrawArgb, Math.Max(1.5, m.DrawThickness))], "Checkmark");
                break;
            case ToolKind.Crossmark:
                await vm.StampAsync(page, ShapeBuilder.CrossMark(at, 14).Select(s => (StampPart)new StampPath(s, false, m.DrawArgb, Math.Max(1.5, m.DrawThickness))).ToList(), "Crossmark");
                break;
            case ToolKind.Dot:
                await vm.StampAsync(page, [new StampPath(ShapeBuilder.Ellipse(new PdfRect(at.X - 3, at.Y - 3, 6, 6), 16), true, m.DrawArgb, 0.5, m.DrawArgb)], "Dot");
                break;
            case ToolKind.Stamp:
                await PlaceStampAsync(page, at, m.CurrentStamp);
                break;
            case ToolKind.Signature or ToolKind.Initials:
                await PlaceSignatureAsync(page, at, Tool == ToolKind.Initials);
                break;
        }
    }

    private async Task PlaceTextAsync(int page, PdfPoint at, string text, bool boxed)
    {
        if (_vm is not { } vm || Markup is not { } m || string.IsNullOrWhiteSpace(text))
            return;
        var size = m.TextSize;
        var lines = text.Replace("\r", "").Split('\n');
        var widest = 0.0;
        foreach (var line in lines)
            widest = Math.Max(widest, await vm.Document.MeasureTextAsync(line, size));
        var parts = new List<StampPart>();
        var pad = boxed ? 5 : 0;
        var color = boxed ? 0xFF1B1B1Bu : m.DrawArgb | 0xFF000000;
        if (boxed)
        {
            var box = new PdfRect(at.X, at.Y, widest + 2 * pad, lines.Length * size * 1.25 + 2 * pad - size * 0.25);
            parts.Add(new StampPath(ShapeBuilder.RoundedRect(box, 3), true, m.DrawArgb | 0xFF000000, 1, 0xFFFFF8D0));
        }
        for (var i = 0; i < lines.Length; i++)
            parts.Add(new StampText(lines[i], new PdfPoint(at.X + pad, at.Y + pad + size * (0.8 + i * 1.25)), size, color));
        await vm.StampAsync(page, parts, boxed ? "TextComment" : "Text", boxed ? text.Trim() : null);
    }

    private async Task PlaceCalloutAsync(int page, PdfPoint target, PdfPoint boxAt, string text)
    {
        if (_vm is not { } vm || Markup is not { } m || string.IsNullOrWhiteSpace(text))
            return;
        var size = m.TextSize;
        var lines = text.Replace("\r", "").Split('\n');
        var widest = 0.0;
        foreach (var line in lines)
            widest = Math.Max(widest, await vm.Document.MeasureTextAsync(line, size));
        const double pad = 5;
        var box = new PdfRect(boxAt.X, boxAt.Y, widest + 2 * pad, lines.Length * size * 1.25 + 2 * pad - size * 0.25);
        // Leader line from the nearest box edge point to the target.
        var edge = new PdfPoint(Math.Clamp(target.X, box.X, box.Right), Math.Clamp(target.Y, box.Y, box.Bottom));
        var color = m.DrawArgb | 0xFF000000;
        var parts = new List<StampPart>
        {
            new StampPath([edge, target], false, color, Math.Max(1, m.DrawThickness * 0.6)),
            new StampPath(ShapeBuilder.ArrowHead(edge, target, 7), false, color, Math.Max(1, m.DrawThickness * 0.6)),
            new StampPath(ShapeBuilder.RoundedRect(box, 2), true, color, 1.2, 0xFFFFFFFF),
        };
        for (var i = 0; i < lines.Length; i++)
            parts.Add(new StampText(lines[i], new PdfPoint(box.X + pad, box.Y + pad + size * (0.8 + i * 1.25)), size, 0xFF1B1B1B));
        await vm.StampAsync(page, parts, "Callout", text.Trim());
    }

    private async Task PlaceStampAsync(int page, PdfPoint center, StampChoice stamp)
    {
        if (_vm is not { } vm)
            return;
        var label = stamp.Label.ToUpper(System.Globalization.CultureInfo.CurrentCulture);
        const double size = 18;
        var width = await vm.Document.MeasureTextAsync(label, size, "Helvetica-Bold");
        var date = DateTime.Now.ToString("d", System.Globalization.CultureInfo.CurrentCulture);
        var box = new PdfRect(center.X - width / 2 - 10, center.Y - 18, width + 20, 38);
        var dateWidth = await vm.Document.MeasureTextAsync(date, 8);
        await vm.StampAsync(page,
        [
            new StampPath(ShapeBuilder.RoundedRect(box, 5), true, stamp.Argb, 2.4, (stamp.Argb & 0xFFFFFF) | 0x1A000000),
            new StampText(label, new PdfPoint(center.X - width / 2, box.Y + 21), size, stamp.Argb, "Helvetica-Bold"),
            new StampText(date, new PdfPoint(center.X - dateWidth / 2, box.Y + 33), 8, stamp.Argb),
        ], "Stamp", stamp.Label);
    }

    private async Task PlaceSignatureAsync(int page, PdfPoint center, bool initials)
    {
        if (_vm is not { } vm)
            return;
        var data = initials ? vm.Main.State.Initials : vm.Main.State.Signature;
        data ??= await vm.Main.EditSignatureAsync(initials);
        if (data is null)
            return;
        var ink = 0xFF1A2A6C; // signature blue
        if (data.Typed is { Length: > 0 } typed)
        {
            var font = FontProvider.Handwriting;
            var size = initials ? 20 : 28;
            var width = await vm.Document.MeasureTextAsync(typed, size, "Helvetica-Oblique", font);
            await vm.StampAsync(page, [new StampText(typed, new PdfPoint(center.X - width / 2, center.Y + size * 0.3), size, ink, "Helvetica-Oblique", font)],
                initials ? "Initials" : "Signature");
            return;
        }
        var strokes = data.Strokes.Select(s => (IReadOnlyList<PdfPoint>)Enumerable.Range(0, s.Length / 2).Select(i => new PdfPoint(s[2 * i], s[2 * i + 1])).ToList()).ToList();
        var placed = ShapeBuilder.PlaceStrokes(strokes, center, initials ? 60 : 150, data.Aspect);
        await vm.StampAsync(page, placed.Select(s => (StampPart)new StampPath(s, false, ink, initials ? 1.4 : 1.8)).ToList(), initials ? "Initials" : "Signature");
    }

    // ---------------------------------------------------------------------------------------
    // Edit overlays: live strokes & shapes, hovered comment's text
    // ---------------------------------------------------------------------------------------

    private void DrawEditOverlays(DrawingContext ctx, int page, Rect pageRect, double dip)
    {
        _ = _vm!.CommentsOn(page); // loads comments for visible pages (for hover)
        if (Markup is not { } m)
            return;

        Point D(PdfPoint p) => new(pageRect.X + p.X * dip, pageRect.Y + p.Y * dip);
        void Poly(IReadOnlyList<PdfPoint> points, uint argb, double width, bool closed = false)
        {
            if (points.Count < 2)
                return;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(D(points[0]), false);
                foreach (var p in points.Skip(1))
                    g.LineTo(D(p));
                g.EndFigure(closed);
            }
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromUInt32(argb)), Math.Max(1, width * dip), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }

        if (_drag is DragMode.Freehand or DragMode.PendingMarker && page == _selectionPage && _inkPoints.Count > 1)
        {
            var marker = Tool == ToolKind.Highlight;
            Poly(_inkPoints, marker ? m.MarkerArgb : m.DrawArgb, marker ? _vm.Main.Settings.MarkerThickness : m.DrawThickness);
        }

        if (_drag == DragMode.Shape && page == _selectionPage)
        {
            var a = _shapeStart;
            var b = _shapeEnd;
            var rect = PdfRect.FromEdges(a.X, a.Y, b.X, b.Y);
            var c = m.DrawArgb;
            var w = m.DrawThickness;
            switch (Tool)
            {
                case ToolKind.Line or ToolKind.Crossout: Poly([a, b], c, w); break;
                case ToolKind.LineArrow: Poly([a, b], c, w); Poly(ShapeBuilder.ArrowHead(a, b, Math.Max(8, w * 4)), c, w); break;
                case ToolKind.Arrow: Poly(ShapeBuilder.BlockArrow(a, b, Math.Max(6, w * 3)), c, 1, closed: true); break;
                case ToolKind.Rectangle: Poly([new(rect.X, rect.Y), new(rect.Right, rect.Y), new(rect.Right, rect.Bottom), new(rect.X, rect.Bottom)], c, w, closed: true); break;
                case ToolKind.Circle or ToolKind.CircleAround: Poly(ShapeBuilder.Ellipse(rect), c, w); break;
                case ToolKind.Cloud: Poly(ShapeBuilder.Cloud(rect, Math.Max(10, w * 5)), c, w); break;
                case ToolKind.Callout: Poly([a, b], c, Math.Max(1, w * 0.6)); Poly(ShapeBuilder.ArrowHead(b, a, 7), c, Math.Max(1, w * 0.6)); break;
            }
        }

        if (page == _multiPage && _multiPoints.Count > 0)
        {
            var points = _multiPoints.ToList();
            if (_multiHover is { } hover)
                points.Add(hover);
            Poly(points, m.DrawArgb, m.DrawThickness, closed: Tool == ToolKind.Polygon && points.Count > 2);
        }

        if (_contextHit is { } ch && ch.PageIndex == page)
        {
            var accent = this.TryFindResource("SystemAccentColor", out var ac) && ac is Color c0 ? c0 : Colors.DodgerBlue;
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(accent), 1.5, dashStyle: DashStyle.Dash), ToDoc(pageRect, ch.Bounds, dip).Inflate(3), 3, 3);
        }

        if (_cardComment is { } cc && cc.PageIndex == page)
        {
            var outline = new Pen(new SolidColorBrush(Color.FromUInt32(cc.Color)), 1.5, dashStyle: DashStyle.Dash);
            var fill = new SolidColorBrush(Color.FromUInt32((cc.Color & 0xFFFFFF) | 0x30000000));
            foreach (var rect in cc.TextRects)
                ctx.DrawRectangle(fill, outline, ToDoc(pageRect, rect, dip).Inflate(1.5), 2, 2);
        }
    }

    /// <summary>The comment marker at (or very near) a document point.</summary>
    private PdfComment? CommentAt(Point docPoint)
    {
        if (_vm is null || _pageRects.Length == 0)
            return null;
        var page = PageNearest(docPoint.Y);
        if (page < 0)
            return null;
        var dip = DipPerPoint;
        foreach (var candidate in new[] { page, page - 1, page + 1 })
        {
            if ((uint)candidate >= (uint)_pageRects.Length || _vm.CommentsOn(candidate) is not { } comments)
                continue;
            foreach (var c in comments)
                if (ToDoc(_pageRects[candidate], c.Marker, dip).Inflate(6).Contains(docPoint))
                    return c;
        }
        return null;
    }

    /// <summary>Scroll to a comment (from the Comments list) and pin its card.</summary>
    private void OnCommentFocusRequested(PdfComment comment)
    {
        Reveal(comment.PageIndex, comment.Marker);
        Dispatcher.UIThread.Post(() => ShowCommentCard(comment, pinned: true), DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------------------------------
    // Popups: selection actions, comment card, text editor
    // ---------------------------------------------------------------------------------------

    private Button PopupButton(string iconKey, string textKey, Action onClick)
    {
        var icon = new Icon { Width = 16, Height = 16 };
        icon.Bind(Icon.DataProperty, this.GetResourceObservable(iconKey));
        var button = new Button
        {
            Classes = { "popup" },
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { icon, new TextBlock { Text = Loc.Get(textKey), VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        Avalonia.Automation.AutomationProperties.SetName(button, Loc.Get(textKey));
        button.Click += (_, _) => onClick();
        return button;
    }

    private Border BuildSelectionPopup()
    {
        return new Border
        {
            Classes = { "floating" },
            IsVisible = false,
            Padding = new Thickness(4),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Children =
                {
                    PopupButton("IconCopy", "Copy", () => { _ = _vm?.CopySelectionAsync(); HideSelectionPopup(); }),
                    PopupButton("IconMarker", "Highlight", () => { if (_vm?.Selection is { } s) _ = _vm.MarkTextAsync(s, TextMarkupKind.Highlight); HideSelectionPopup(); }),
                    PopupButton("IconComment", "Comment", () => { HideSelectionPopup(); ShowCommentEditor(); }),
                    PopupButton("IconSpeaker", "ReadAloud", () => { _vm?.ReadSelection(); HideSelectionPopup(); }),
                },
            },
        };
    }

    private void ShowSelectionPopup()
    {
        if (_vm?.Selection is not { IsEmpty: false })
            return;
        _selectionPopup.IsVisible = true;
        PositionSelectionPopup();
    }

    private void HideSelectionPopup() => _selectionPopup.IsVisible = false;

    private void PositionSelectionPopup()
    {
        PositionNearSelection(_selectionPopup);
        PositionEditor();
        if (_cardComment is { } c)
            PositionCard(c);
    }

    private Rect SelectionAnchor(TextSelection selection)
        => (uint)selection.PageIndex < (uint)_pageRects.Length && selection.Rects.Count > 0
            ? ToDoc(_pageRects[selection.PageIndex], selection.Rects[^1], DipPerPoint)
            : default;

    private void PositionNearSelection(Border popup)
    {
        if (!popup.IsVisible || _vm?.Selection is not { Rects.Count: > 0 } selection)
            return;
        PositionAt(popup, SelectionAnchor(selection));
    }

    private void PositionAt(Border popup, Rect anchorDoc)
    {
        popup.Measure(Size.Infinity);
        var size = popup.DesiredSize;
        var x = Math.Clamp(anchorDoc.Right - _scroll.Offset.X - size.Width / 2, 8, Math.Max(8, Bounds.Width - size.Width - 8));
        var y = anchorDoc.Bottom - _scroll.Offset.Y + 8;
        if (y + size.Height > Bounds.Height - 8)
            y = Math.Max(8, anchorDoc.Top - _scroll.Offset.Y - size.Height - 8);
        Canvas.SetLeft(popup, x);
        Canvas.SetTop(popup, y);
    }

    // --- Comment card (hover: date, text, quoted words; click: pinned with Delete) ----------------

    private Border? _card;
    private TextBlock? _cardDate, _cardText, _cardQuote;
    private Button? _cardDelete;
    private PdfComment? _cardComment;
    private bool _cardPinned;

    private Border Card()
    {
        if (_card is not null)
            return _card;
        _cardDate = new TextBlock { FontSize = 10.5, Classes = { "secondary" } };
        _cardText = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 280, Margin = new Thickness(0, 3, 0, 0) };
        _cardQuote = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 280, FontStyle = FontStyle.Italic, FontSize = 11.5, Classes = { "secondary" }, Margin = new Thickness(0, 6, 0, 0) };
        _cardDelete = PopupButton("IconTrash", "Delete", () =>
        {
            if (_cardComment is { } c && _vm is { } vm)
                _ = vm.DeleteCommentAsync(c);
            HideCommentCard(force: true);
        });
        _cardDelete.HorizontalAlignment = HorizontalAlignment.Right;
        _cardDelete.Margin = new Thickness(0, 6, 0, 0);
        _card = new Border
        {
            Classes = { "floating" },
            Padding = new Thickness(12, 8),
            IsVisible = false,
            Child = new StackPanel { Children = { _cardDate, _cardText, _cardQuote, _cardDelete } },
        };
        _overlay.Children.Add(_card);
        return _card;
    }

    private void ShowCommentCard(PdfComment comment, bool pinned)
    {
        if (_cardPinned && !pinned && _cardComment?.Id != comment.Id)
            return;
        var card = Card();
        var samePinned = _cardPinned && _cardComment?.Id == comment.Id;
        _cardComment = comment;
        _cardPinned = pinned || samePinned;
        _cardDate!.Text = comment.CreatedUtc == DateTime.MinValue ? "" : comment.CreatedUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
        _cardText!.Text = comment.Kind switch
        {
            "Replace" => Loc.Format("ReplaceWith", comment.Text),
            "Insert" => Loc.Format("InsertWhat", comment.Text),
            _ => comment.Text,
        };
        _cardQuote!.Text = comment.Quote.Length == 0 ? "" : Loc.Format("CommentOn", comment.Quote.Length > 120 ? comment.Quote[..120] + "…" : comment.Quote);
        _cardQuote.IsVisible = comment.Quote.Length > 0;
        _cardDelete!.IsVisible = _cardPinned;
        card.IsVisible = true;
        PositionCard(comment);
        _canvas.InvalidateVisual();
    }

    private void PositionCard(PdfComment comment)
    {
        if (_card is not { IsVisible: true } card || (uint)comment.PageIndex >= (uint)_pageRects.Length)
            return;
        var marker = ToDoc(_pageRects[comment.PageIndex], comment.Marker, DipPerPoint);
        card.Measure(Size.Infinity);
        var size = card.DesiredSize;
        var x = Math.Clamp(marker.Right - _scroll.Offset.X + 6, 8, Math.Max(8, Bounds.Width - size.Width - 8));
        var y = marker.Bottom - _scroll.Offset.Y + 4;
        if (y + size.Height > Bounds.Height - 8)
            y = Math.Max(8, marker.Top - _scroll.Offset.Y - size.Height - 4);
        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, y);
    }

    private void HideCommentCard(bool force)
    {
        if (_card is null || (_cardPinned && !force))
            return;
        var wasVisible = _card.IsVisible;
        _card.IsVisible = false;
        _cardComment = null;
        _cardPinned = false;
        if (wasVisible)
            _canvas.InvalidateVisual();
    }

    // --- Text editor (comments, inserts, typed text, callouts, replacements) ----------------------

    private Border _commentEditor = new() { IsVisible = false };
    private TextBox? _commentBox;
    private TextBlock? _commentQuote, _editorTitle;
    private Icon? _commentShapeIcon;
    private Func<string, Task>? _editorCommit;
    private Rect _editorAnchor;

    private void EnsureCommentEditor()
    {
        if (_commentBox is not null)
            return;
        _commentBox = new TextBox { Width = 280, Height = 76, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        _commentBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                CommitEditor();
                e.Handled = true;
            }
        };
        _commentQuote = new TextBlock { Classes = { "secondary" }, FontStyle = FontStyle.Italic, MaxWidth = 280, TextTrimming = TextTrimming.CharacterEllipsis };
        _commentShapeIcon = new Icon { Width = 18, Height = 18, StrokeThickness = 2.2 };
        _editorTitle = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var cancel = new Button { Content = Loc.Get("Cancel"), MinWidth = 80 };
        cancel.Click += (_, _) => HideCommentEditor();
        var add = new Button { Content = Loc.Get("AddComment"), Classes = { "primary" }, MinWidth = 80, Padding = new Thickness(12, 6) };
        Avalonia.Automation.AutomationProperties.SetName(add, Loc.Get("AddComment"));
        add.Click += (_, _) => CommitEditor();
        _commentEditor = new Border
        {
            Classes = { "floating" },
            Padding = new Thickness(12, 10),
            IsVisible = false,
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _commentShapeIcon, _editorTitle } },
                    _commentQuote,
                    _commentBox,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, add } },
                },
            },
        };
        _overlay.Children.Add(_commentEditor);
    }

    private void ShowCommentEditor()
    {
        if (_vm?.Selection is not { IsEmpty: false } selection || Markup is not { } m)
            return;
        ShowEditor("Comment", "CommentPlaceholder", null, selection.Text, SelectionAnchor(selection), text => _vm.AddCommentAsync(selection, text));
        _commentShapeIcon!.Data = m.ShapeIcon;
        _commentShapeIcon.Foreground = m.CommentBrush;
    }

    private void ShowEditor(string titleKey, string placeholderKey, string? iconKey, string? quote, Rect anchorDoc, Func<string, Task> commit)
    {
        EnsureCommentEditor();
        _editorCommit = commit;
        _editorAnchor = anchorDoc;
        _editorTitle!.Text = Loc.Get(titleKey);
        _commentBox!.Text = "";
        _commentBox.PlaceholderText = Loc.Get(placeholderKey);
        Avalonia.Automation.AutomationProperties.SetName(_commentBox, Loc.Get(placeholderKey));
        var q = quote?.Trim() ?? "";
        _commentQuote!.Text = q.Length == 0 ? "" : Loc.Format("CommentOn", q.Length > 60 ? q[..60] + "…" : q);
        _commentQuote.IsVisible = q.Length > 0;
        if (iconKey is not null && this.TryFindResource(iconKey, out var g) && g is not null)
        {
            _commentShapeIcon!.Data = g;
            _commentShapeIcon.Foreground = Markup?.DrawPreview;
        }
        _commentEditor.IsVisible = true;
        PositionEditor();
        Dispatcher.UIThread.Post(() => _commentBox.Focus(), DispatcherPriority.Input);
    }

    private void PositionEditor()
    {
        if (_commentEditor.IsVisible)
            PositionAt(_commentEditor, _editorAnchor);
    }

    private void HideCommentEditor() => _commentEditor.IsVisible = false;

    private void CommitEditor()
    {
        if (_editorCommit is not { } commit || string.IsNullOrWhiteSpace(_commentBox?.Text))
            return;
        var text = _commentBox.Text;
        HideCommentEditor();
        _editorCommit = null;
        _ = commit(text);
    }

    // ---------------------------------------------------------------------------------------
    // Right-click menu: delete / recolor / edit what the user made; actions on selected text
    // ---------------------------------------------------------------------------------------

    private PdfAnnotationHit? _contextHit;

    private static readonly (string Hex, string NameKey)[] MenuColors =
    [
        ("#FFE600", "ColorYellow"), ("#7CFC00", "ColorGreen"), ("#00E5FF", "ColorCyan"), ("#FF6EC7", "ColorPink"),
        ("#FF9F1C", "ColorOrange"), ("#D9363E", "ColorRed"), ("#2F6FEB", "ColorBlue"), ("#8E5BE8", "ColorPurple"),
        ("#1F9D55", "ColorDarkGreen"), ("#1B1B1B", "ColorBlack"), ("#6B7280", "ColorGray"),
    ];

    private async Task ShowContextMenuAsync(Point docPoint)
    {
        if (_vm is not { } vm)
            return;
        var page = PageAt(docPoint);
        if (page < 0)
            return;
        var pt = ToPagePoints(page, docPoint);
        PdfAnnotationHit? hit = null;
        try
        {
            hit = await vm.Document.GetAnnotationAtAsync(page, pt.X, pt.Y, tolerance: Math.Max(2, 4 / DipPerPoint));
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException) { }

        var menu = new MenuFlyout();
        MenuItem Item(string textKey, string? iconKey, Action onClick, bool enabled = true)
        {
            var item = new MenuItem { Header = Loc.Get(textKey), IsEnabled = enabled };
            if (iconKey is not null && this.TryFindResource(iconKey, out var g))
                item.Icon = new Icon { Data = g };
            item.Click += (_, _) => onClick();
            return item;
        }

        if (hit is not null)
        {
            _contextHit = hit;
            _canvas.InvalidateVisual();
            HideCommentCard(force: true);
            menu.Items.Add(new MenuItem { Header = Loc.Get("Kind" + hit.Kind), IsEnabled = false, FontWeight = FontWeight.SemiBold });
            menu.Items.Add(new Separator());
            if (hit.IsNote)
            {
                menu.Items.Add(Item("EditComment", "IconComment", () =>
                {
                    var anchor = ToDoc(_pageRects[hit.PageIndex], hit.Bounds, DipPerPoint);
                    ShowEditor("EditComment", "CommentPlaceholder", "IconComment", null, anchor, text => vm.EditAnnotationTextAsync(hit, text));
                    _commentBox!.Text = hit.Contents;
                }));
            }
            var colors = new MenuItem { Header = Loc.Get("ChangeColor") };
            if (this.TryFindResource("IconDrop", out var drop))
                colors.Icon = new Icon { Data = drop };
            foreach (var (hex, nameKey) in MenuColors)
            {
                var argb = ColorParser.ParseOrDefault(hex, 0xFF000000);
                var swatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.FromUInt32(argb)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), BorderThickness = new Thickness(1) };
                var item = new MenuItem { Header = Loc.Get(nameKey), Icon = swatch };
                Avalonia.Automation.AutomationProperties.SetName(item, Loc.Get(nameKey));
                item.Click += (_, _) => _ = vm.RecolorAnnotationAsync(hit, argb & 0xFFFFFF);
                colors.Items.Add(item);
            }
            menu.Items.Add(colors);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Delete", "IconTrash", () => _ = vm.DeleteAnnotationAsync(hit)));
        }
        else if (vm.Selection is { IsEmpty: false } selection && selection.PageIndex == page
                 && selection.Rects.Any(r => new PdfRect(r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6).Contains(pt.X, pt.Y)))
        {
            HideSelectionPopup();
            menu.Items.Add(Item("Copy", "IconCopy", () => _ = vm.CopySelectionAsync()));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("ToolHighlight", "IconMarker", () => _ = vm.MarkTextAsync(selection, TextMarkupKind.Highlight)));
            menu.Items.Add(Item("ToolUnderline", "IconUnderline", () => _ = vm.MarkTextAsync(selection, TextMarkupKind.Underline)));
            menu.Items.Add(Item("ToolStrike", "IconStrike", () => _ = vm.MarkTextAsync(selection, TextMarkupKind.StrikeOut)));
            menu.Items.Add(Item("Comment", "IconComment", ShowCommentEditor));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("ReadAloud", "IconSpeaker", () => vm.ReadSelection()));
        }
        else
        {
            return;
        }

        menu.Closed += (_, _) =>
        {
            _contextHit = null;
            _canvas.InvalidateVisual();
        };
        menu.ShowAt(_canvas, showAtPointer: true);
    }

    /// <summary>The scrollable surface. Draws only what intersects the viewport.</summary>
    private sealed class PageCanvas(PdfViewer owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            // Transparent fill makes empty areas hit-testable (clicks clear the selection).
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
            owner.RenderPages(context);
        }
    }
}
