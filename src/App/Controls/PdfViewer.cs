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
            CancelAllPending();
        }

        _vm = vm;
        _pageRects = [];
        _fallback = null;
        _configHasTiles = false;
        _firstPageReported = false;
        HideSelectionPopup();

        if (vm is not null)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.ViewerRequested += OnViewerRequested;
            vm.HighlightsChanged += OnHighlightsChanged;
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
                    HideSelectionPopup();
                _canvas.InvalidateVisual();
                break;
        }
    }

    private void OnHighlightsChanged() => _canvas.InvalidateVisual();

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
        if (_vm.ZoomMode is ZoomMode.FitWidth or ZoomMode.FitPage && (widthChanged || _vm.ZoomMode == ZoomMode.FitPage))
            SetZoom(ZoomForMode(_vm.ZoomMode), null, _vm.ZoomMode, force: true);
        else
            Relayout(keepPosition: true);
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

    private TileKey PreviewKey(int page) => new(_vm!.Id, page, -1, _config.Colors, 0, 0);

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
                var key = new TileKey(_vm.Id, page, config.ScaleKey, config.Colors, tx, ty);
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

        for (var page = first; page <= last; page++)
        {
            var r = _pageRects[page];
            ctx.DrawRectangle(pageBrush, null, new RoundedRect(r, 2), PageShadow);

            using (ctx.PushClip(r))
            {
                if (_cache.Get(PreviewKey(page)) is { } preview)
                    ctx.DrawImage(preview, new Rect(preview.Size), r);

                if (_fallback is { } fallback)
                    DrawTiles(ctx, page, fallback, vp, onlyCached: true);
                DrawTiles(ctx, page, _config, vp, onlyCached: false);
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
        }
    }

    private void DrawTiles(DrawingContext ctx, int page, RenderConfig config, Rect vp, bool onlyCached)
    {
        foreach (var (key, _, docRect) in TilesFor(page, config, vp))
        {
            if (_cache.Get(key, touch: !onlyCached) is { } bitmap)
                ctx.DrawImage(bitmap, new Rect(bitmap.Size), docRect);
        }
    }

    private static Rect ToDoc(Rect page, PdfRect rect, double dip)
        => new(page.X + rect.X * dip, page.Y + rect.Y * dip, rect.Width * dip, rect.Height * dip);

    // ---------------------------------------------------------------------------------------
    // Pointer: selection, links, hover cursor
    // ---------------------------------------------------------------------------------------

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null || !e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed)
            return;
        _canvas.Focus();
        var point = e.GetPosition(_canvas);
        _pressPoint = point;
        var page = PageAt(point);
        HideSelectionPopup();
        _vm.Selection = null;
        if (page < 0)
            return;

        _selecting = true;
        _selectionPage = page;
        _anchorChar = -1;
        e.Pointer.Capture(_canvas);
        var pt = ToPagePoints(page, point);
        _ = SetAnchorAsync(page, pt);
        e.Handled = true;
    }

    private async Task SetAnchorAsync(int page, Point pt)
    {
        try
        {
            _anchorChar = await _vm!.Document.GetCharIndexAtAsync(page, pt.X, pt.Y, tolerance: 12);
        }
        catch (Exception ex) when (ex is PdfException or ObjectDisposedException) { }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_vm is null)
            return;
        var point = e.GetPosition(_canvas);
        if (_selecting)
        {
            _pendingDragPoint = point;
            if (!_dragQueryInFlight)
                _ = UpdateDragSelectionAsync();
        }
        else
        {
            _pendingHoverPoint = point;
            if (!_hoverQueryInFlight)
                _ = UpdateHoverAsync();
        }
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
                var link = await vm.Document.GetLinkAtAsync(page, pt.X, pt.Y);
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

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_vm is not { } vm || !_selecting)
            return;
        _selecting = false;
        e.Pointer.Capture(null);
        var point = e.GetPosition(_canvas);
        var isClick = Math.Abs(point.X - _pressPoint.X) < 4 && Math.Abs(point.Y - _pressPoint.Y) < 4;
        if (isClick)
        {
            vm.Selection = null;
            _ = ActivateLinkAtAsync(_selectionPage, ToPagePoints(_selectionPage, point));
            return;
        }
        // Let the last drag query land before showing the popup.
        Dispatcher.UIThread.Post(ShowSelectionPopup, DispatcherPriority.Background);
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

    // ---------------------------------------------------------------------------------------
    // Selection popup (Copy, Read aloud)
    // ---------------------------------------------------------------------------------------

    private Border BuildSelectionPopup()
    {
        Button Item(string iconKey, string textKey, Action onClick)
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
            ToolTip.SetTip(button, Loc.Get(textKey));
            button.Click += (_, _) => { onClick(); HideSelectionPopup(); };
            return button;
        }

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
                    Item("IconCopy", "Copy", () => _ = _vm?.CopySelectionAsync()),
                    Item("IconSpeaker", "ReadAloud", () => _vm?.ReadSelection()),
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
        if (!_selectionPopup.IsVisible || _vm?.Selection is not { Rects.Count: > 0 } selection
            || (uint)selection.PageIndex >= (uint)_pageRects.Length)
            return;
        var page = _pageRects[selection.PageIndex];
        var last = ToDoc(page, selection.Rects[^1], DipPerPoint);
        _selectionPopup.Measure(Size.Infinity);
        var size = _selectionPopup.DesiredSize;
        var x = Math.Clamp(last.Right - _scroll.Offset.X - size.Width / 2, 8, Math.Max(8, Bounds.Width - size.Width - 8));
        var y = last.Bottom - _scroll.Offset.Y + 8;
        if (y + size.Height > Bounds.Height - 8)
            y = last.Top - _scroll.Offset.Y - size.Height - 8;
        Canvas.SetLeft(_selectionPopup, x);
        Canvas.SetTop(_selectionPopup, y);
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
