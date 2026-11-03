using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PdfReader.App.Localization;
using PdfReader.App.ViewModels;
using PdfReader.Core.Settings;

namespace PdfReader.App.Controls;

/// <summary>
/// The tool bar: grouped tools (icon + name), each opening a menu of its tools, plus undo and zoom.
/// Drag the grip to dock it left / right / top or let it float; drag its edge to resize it.
/// Narrow (below ~110 px) it shows icons only.
/// </summary>
public sealed class ToolbarView : UserControl
{
    private const double RowHeight = 34;
    private const double CompactBelow = 110;
    private const double MinWidthPx = 48;
    private const double MaxWidthPx = 260;

    private readonly StackPanel _stack = new() { Spacing = 2 };
    private readonly Border _grip;
    private readonly Border _frame;
    private readonly Border _handle;
    private readonly Grid _root = new();
    private readonly List<Row> _rows = [];
    private readonly List<(ToolGroupViewModel Group, Row Row)> _groupRows = [];
    private Row? _zoomRow;
    private Row? _undoRow;
    private DocumentViewModel? _vm;
    private bool _dragging;
    private Point _dragOffset;
    private bool _resizing;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private double _width = 160;

    private sealed record Row(Button Button, Icon Icon, TextBlock Label, Icon Chevron, bool HasChevron);

    public ToolbarView()
    {
        var gripIcon = new Icon { Width = 22, Height = 10, StrokeThickness = 2.2, HorizontalAlignment = HorizontalAlignment.Center };
        gripIcon.Bind(Icon.DataProperty, this.GetResourceObservable("IconGrip"));
        _grip = new Border { Height = 14, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeAll), Child = gripIcon, Margin = new Thickness(0, 0, 0, 2) };
        ToolTip.SetTip(_grip, Loc.Get("ToolbarGripTip"));
        Avalonia.Automation.AutomationProperties.SetName(_grip, Loc.Get("ToolbarGripTip"));
        _grip.PointerPressed += OnGripPressed;
        _grip.PointerMoved += OnGripMoved;
        _grip.PointerReleased += OnGripReleased;

        _frame = new Border
        {
            Classes = { "toolbar" },
            Padding = new Thickness(6),
            Child = new ScrollViewer
            {
                Content = _stack,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            },
        };

        _handle = new Border { Width = 6, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeWestEast) };
        ToolTip.SetTip(_handle, Loc.Get("ResizeTip"));
        Avalonia.Automation.AutomationProperties.SetName(_handle, Loc.Get("ResizeTip"));
        _handle.PointerPressed += OnHandlePressed;
        _handle.PointerMoved += OnHandleMoved;
        _handle.PointerReleased += OnHandleReleased;

        _root.Children.Add(_frame);
        _root.Children.Add(_handle);
        Content = _root;
    }

    /// <summary>Raised while dragging and when dropped: the bar's new top-left and the pointer, relative to the document view.</summary>
    public event Action<Point, Point, bool>? Dragged;

    public ToolbarDock Dock { get; private set; } = ToolbarDock.Left;

    public void SetDock(ToolbarDock dock)
    {
        Dock = dock;
        var horizontal = dock == ToolbarDock.Top;
        _stack.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        _frame.Classes.Set("floating", dock == ToolbarDock.Floating);
        _frame.Classes.Set("docked", dock != ToolbarDock.Floating);
        _frame.BorderThickness = dock switch
        {
            ToolbarDock.Left => new Thickness(0, 0, 1, 0),
            ToolbarDock.Right => new Thickness(1, 0, 0, 0),
            ToolbarDock.Top => new Thickness(0, 0, 0, 1),
            _ => new Thickness(1),
        };
        if (_frame.Child is ScrollViewer sv)
        {
            sv.HorizontalScrollBarVisibility = horizontal ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
            sv.VerticalScrollBarVisibility = horizontal ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        }

        // Resize handle on the edge that faces the document.
        _handle.IsVisible = !horizontal;
        _handle.HorizontalAlignment = dock == ToolbarDock.Right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        _grip.Height = horizontal ? double.NaN : 14;
        _grip.Width = horizontal ? 14 : double.NaN;
        _grip.Margin = horizontal ? new Thickness(0, 0, 2, 0) : new Thickness(0, 0, 0, 2);
        ((Icon)_grip.Child!).RenderTransform = horizontal ? new RotateTransform(90) : null;
        foreach (var child in _stack.Children)
            if (child is Rectangle sep)
            {
                sep.Width = horizontal ? 1 : double.NaN;
                sep.Height = horizontal ? double.NaN : 1;
                sep.Margin = horizontal ? new Thickness(4, 6) : new Thickness(6, 4);
            }
        ApplyWidth();
    }

    private bool Compact => _width < CompactBelow;

    private void ApplyWidth()
    {
        var horizontal = Dock == ToolbarDock.Top;
        _frame.Width = horizontal ? double.NaN : _width;
        var compact = Compact;
        foreach (var row in _rows)
        {
            row.Label.IsVisible = !compact;
            row.Chevron.IsVisible = !compact && row.HasChevron;
            row.Icon.Margin = compact ? default : new Thickness(0, 0, 10, 0);
            row.Button.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            row.Button.Width = horizontal ? double.NaN : _width - 12;
            row.Button.Padding = compact ? new Thickness(0) : new Thickness(10, 0);
            ToolTip.SetTip(row.Button, row.Label.Text);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Main.Markup.PropertyChanged -= OnMarkupChanged;
            _vm.PropertyChanged -= OnDocumentChanged;
        }
        _vm = DataContext as DocumentViewModel;
        if (_vm is not null)
            _width = Math.Clamp(_vm.Main.Settings.ToolbarWidth, MinWidthPx, MaxWidthPx);
        Build();
        if (_vm is not null)
        {
            _vm.Main.Markup.PropertyChanged += OnMarkupChanged;
            _vm.PropertyChanged += OnDocumentChanged;
        }
    }

    private void Build()
    {
        _stack.Children.Clear();
        _rows.Clear();
        _groupRows.Clear();
        if (_vm is null)
            return;
        _stack.Children.Add(_grip);
        foreach (var group in _vm.Main.Markup.Groups)
        {
            var row = MakeRow(null, group.Title, chevron: true);
            row.Icon.Data = group.Icon;
            row.Button.Flyout = GroupMenu(group);
            _groupRows.Add((group, row));
            _stack.Children.Add(row.Button);
        }
        _stack.Children.Add(new Rectangle { Classes = { "toolsep" }, Height = 1 });
        _undoRow = MakeRow("IconUndo", Loc.Get("Undo"), chevron: false);
        _undoRow.Button.Click += (_, _) => _ = _vm?.UndoAsync();
        _stack.Children.Add(_undoRow.Button);
        _zoomRow = MakeRow("IconZoom", _vm.ZoomText, chevron: true);
        _zoomRow.Button.Flyout = ZoomMenu();
        _stack.Children.Add(_zoomRow.Button);
        UpdateState();
        SetDock(Dock);
    }

    private Row MakeRow(string? iconKey, string text, bool chevron)
    {
        var icon = new Icon { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        if (iconKey is not null && Application.Current?.TryGetResource(iconKey, null, out var g) == true)
            icon.Data = g;
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var chev = new Icon { Width = 10, Height = 10, Margin = new Thickness(6, 0, 0, 0), Opacity = 0.55, IsVisible = chevron, VerticalAlignment = VerticalAlignment.Center };
        if (Application.Current?.TryGetResource("IconChevronRight", null, out var c) == true)
            chev.Data = c;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(icon);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        Grid.SetColumn(chev, 2);
        grid.Children.Add(chev);
        var button = new Button { Classes = { "toolgroup" }, Content = grid, Height = RowHeight, VerticalContentAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(button, text);
        var row = new Row(button, icon, label, chev, chevron);
        _rows.Add(row);
        return row;
    }

    private MenuFlyout GroupMenu(ToolGroupViewModel group)
    {
        var menu = new MenuFlyout();
        foreach (var item in group.Items)
        {
            var mi = new MenuItem { Header = item.Title, Icon = new Icon { Data = item.Icon }, Tag = item };
            mi.Click += (_, _) => { if (_vm is not null) _vm.Main.Markup.Tool = item.Kind; };
            menu.Items.Add(mi);
        }
        menu.Opening += (_, _) =>
        {
            menu.Placement = Placement();
            foreach (var mi in menu.Items.OfType<MenuItem>())
                mi.FontWeight = mi.Tag is ToolItem t && t.Kind == _vm?.Main.Markup.Tool ? FontWeight.SemiBold : FontWeight.Normal;
        };
        return menu;
    }

    private PlacementMode Placement() => Dock switch
    {
        ToolbarDock.Right => PlacementMode.LeftEdgeAlignedTop,
        ToolbarDock.Top => PlacementMode.BottomEdgeAlignedLeft,
        _ => PlacementMode.RightEdgeAlignedTop,
    };

    private MenuFlyout ZoomMenu()
    {
        var menu = new MenuFlyout();
        void Add(string key, string? iconKey, Action a)
        {
            var mi = new MenuItem { Header = Loc.Get(key) };
            if (iconKey is not null && Application.Current?.TryGetResource(iconKey, null, out var g) == true)
                mi.Icon = new Icon { Data = g };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        Add("ZoomIn", "IconPlus", () => _vm?.Request(new ViewerRequest.ZoomStep(+1)));
        Add("ZoomOut", "IconMinus", () => _vm?.Request(new ViewerRequest.ZoomStep(-1)));
        Add("FitWidth", "IconFitWidth", () => _vm?.Request(new ViewerRequest.SetZoomMode(ZoomMode.FitWidth)));
        Add("FitPage", "IconFitPage", () => _vm?.Request(new ViewerRequest.SetZoomMode(ZoomMode.FitPage)));
        Add("ActualSize", "IconActualSize", () => _vm?.Request(new ViewerRequest.SetZoomMode(ZoomMode.ActualSize)));
        menu.Items.Add(new Separator());
        foreach (var z in new[] { 0.5, 1, 1.5, 2, 4 })
        {
            var mi = new MenuItem { Header = $"{z * 100:0}%" };
            mi.Click += (_, _) => _vm?.Request(new ViewerRequest.SetZoom(z));
            menu.Items.Add(mi);
        }
        menu.Opening += (_, _) => menu.Placement = Placement();
        return menu;
    }

    private void OnMarkupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MarkupViewModel.Tool))
            UpdateState();
    }

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.ZoomText) or nameof(DocumentViewModel.CanUndo))
            UpdateState();
    }

    private void UpdateState()
    {
        foreach (var (group, row) in _groupRows)
        {
            row.Button.Classes.Set("active", group.IsActive);
            row.Icon.Data = group.Icon;
        }
        if (_zoomRow is not null && _vm is not null)
        {
            _zoomRow.Label.Text = _vm.ZoomText;
            Avalonia.Automation.AutomationProperties.SetName(_zoomRow.Button, _vm.ZoomText);
            ToolTip.SetTip(_zoomRow.Button, _vm.ZoomText);
        }
        if (_undoRow is not null && _vm is not null)
            _undoRow.Button.IsEnabled = _vm.CanUndo;
    }

    // --- Moving the bar --------------------------------------------------------------------

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragging = true;
        _dragOffset = e.GetPosition(this);
        e.Pointer.Capture(_grip);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || this.FindAncestorOfType<Views.DocumentView>() is not { } host)
            return;
        var p = e.GetPosition(host);
        Dragged?.Invoke(new Point(p.X - _dragOffset.X, p.Y - _dragOffset.Y), p, false);
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        e.Pointer.Capture(null);
        if (this.FindAncestorOfType<Views.DocumentView>() is not { } host)
            return;
        var p = e.GetPosition(host);
        Dragged?.Invoke(new Point(p.X - _dragOffset.X, p.Y - _dragOffset.Y), p, true);
    }

    // --- Resizing the bar ------------------------------------------------------------------

    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _resizing = true;
        _resizeStartX = e.GetPosition(null).X;
        _resizeStartWidth = _width;
        e.Pointer.Capture(_handle);
        e.Handled = true;
    }

    private void OnHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing)
            return;
        var dx = e.GetPosition(null).X - _resizeStartX;
        _width = Math.Clamp(_resizeStartWidth + (Dock == ToolbarDock.Right ? -dx : dx), MinWidthPx, MaxWidthPx);
        ApplyWidth();
    }

    private void OnHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing)
            return;
        _resizing = false;
        e.Pointer.Capture(null);
        if (_vm is not null)
        {
            _vm.Main.Settings.ToolbarWidth = _width;
            _vm.Main.SaveSettings();
        }
    }
}

internal static class VisualAncestorExtensions
{
    public static T? FindAncestorOfType<T>(this Visual visual) where T : class
    {
        for (var v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(visual); v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
            if (v is T t)
                return t;
        return null;
    }
}
