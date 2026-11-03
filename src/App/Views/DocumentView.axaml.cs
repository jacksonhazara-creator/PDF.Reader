using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using PdfReader.App.ViewModels;
using PdfReader.Core.Settings;

namespace PdfReader.App.Views;

public sealed partial class DocumentView : UserControl
{
    private const double SnapDistance = 70;

    public DocumentView()
    {
        InitializeComponent();
        Toolbar.Dragged += OnToolbarDragged;
        AttachedToVisualTree += (_, _) => ApplyDock(Settings?.ToolbarDock ?? ToolbarDock.Left);
    }

    private DocumentViewModel? Vm => DataContext as DocumentViewModel;
    private AppSettings? Settings => Vm?.Main.Settings;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Settings is { } s)
            ApplyDock(s.ToolbarDock);
    }

    /// <summary>Puts the toolbar in its docked position, or on the floating layer.</summary>
    private void ApplyDock(ToolbarDock dock)
    {
        (Toolbar.Parent as Panel)?.Children.Remove(Toolbar);
        if (dock == ToolbarDock.Floating)
        {
            FloatLayer.Children.Add(Toolbar);
            var s = Settings!;
            Canvas.SetLeft(Toolbar, Math.Clamp(s.ToolbarX, 0, Math.Max(0, Bounds.Width - 160)));
            Canvas.SetTop(Toolbar, Math.Clamp(s.ToolbarY, 0, Math.Max(0, Bounds.Height - 120)));
        }
        else
        {
            DockPanel.SetDock(Toolbar, dock switch { ToolbarDock.Right => Avalonia.Controls.Dock.Right, ToolbarDock.Top => Avalonia.Controls.Dock.Top, _ => Avalonia.Controls.Dock.Left });
            Dock.Children.Insert(0, Toolbar);
        }
        Toolbar.SetDock(dock);
    }

    private Border? _dragPreview;

    /// <summary>
    /// While the grip is dragged the toolbar itself stays put (moving it between containers would
    /// break the mouse capture); a translucent preview shows where it will go, snapped to the edge
    /// the pointer is near. On release the toolbar moves there.
    /// </summary>
    private void OnToolbarDragged(Point topLeft, Point pointer, bool dropped)
    {
        if (Settings is not { } s)
            return;
        var root = Root.Bounds;
        var size = Toolbar.Bounds.Size;
        var x = Math.Clamp(topLeft.X, 0, Math.Max(0, root.Width - size.Width));
        var y = Math.Clamp(topLeft.Y - root.Y, 0, Math.Max(0, root.Height - Math.Min(size.Height, root.Height)));
        var dock = SnapFor(pointer, root);

        if (!dropped)
        {
            ShowPreview(dock switch
            {
                ToolbarDock.Left => new Rect(0, 0, size.Width, root.Height),
                ToolbarDock.Right => new Rect(root.Width - RightPanelWidth() - size.Width, 0, size.Width, root.Height),
                ToolbarDock.Top => new Rect(0, 0, root.Width - RightPanelWidth(), 46),
                _ => new Rect(x, y, Math.Max(size.Width, 60), Math.Min(size.Height, root.Height - y)),
            });
            return;
        }

        HidePreview();
        s.ToolbarDock = dock;
        s.ToolbarX = x;
        s.ToolbarY = y;
        Vm!.Main.SaveSettings();
        ApplyDock(dock);
    }

    private double RightPanelWidth() => this.GetVisualDescendants().OfType<RightPanel>().FirstOrDefault()?.Bounds.Width ?? 0;

    /// <summary>Snap by where the pointer is: left edge, right side (the right panel counts as the edge), top, or float.</summary>
    private ToolbarDock SnapFor(Point pointer, Rect root)
    {
        var px = pointer.X;
        var py = pointer.Y - root.Y;
        return px < SnapDistance ? ToolbarDock.Left
            : px > root.Width - RightPanelWidth() - SnapDistance ? ToolbarDock.Right
            : py < SnapDistance / 2 ? ToolbarDock.Top
            : ToolbarDock.Floating;
    }

    private void ShowPreview(Rect rect)
    {
        if (_dragPreview is null)
        {
            _dragPreview = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false,
            };
            if (this.TryFindResource("SystemAccentColor", out var accent) && accent is Avalonia.Media.Color c)
            {
                _dragPreview.BorderBrush = new Avalonia.Media.SolidColorBrush(c);
                _dragPreview.Background = new Avalonia.Media.SolidColorBrush(c, 0.15);
            }
            FloatLayer.Children.Add(_dragPreview);
        }
        _dragPreview.IsVisible = true;
        _dragPreview.Width = rect.Width;
        _dragPreview.Height = rect.Height;
        Canvas.SetLeft(_dragPreview, rect.X);
        Canvas.SetTop(_dragPreview, rect.Y);
    }

    private void HidePreview()
    {
        if (_dragPreview is not null)
            _dragPreview.IsVisible = false;
    }
}
