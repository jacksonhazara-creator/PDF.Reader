using Avalonia;
using Avalonia.Controls;
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

    private void OnToolbarDragged(Point topLeft, bool dropped)
    {
        if (Settings is not { } s)
            return;
        if (Toolbar.Dock != ToolbarDock.Floating)
            ApplyDock(ToolbarDock.Floating);

        var root = Root.Bounds;
        var x = Math.Clamp(topLeft.X, 0, Math.Max(0, root.Width - Toolbar.Bounds.Width));
        var y = Math.Clamp(topLeft.Y - Root.Bounds.Y, 0, Math.Max(0, root.Height - Math.Min(Toolbar.Bounds.Height, root.Height)));
        Canvas.SetLeft(Toolbar, x);
        Canvas.SetTop(Toolbar, y);
        if (!dropped)
            return;

        // Snap to an edge when dropped close to it; otherwise stay where it was put.
        var dock = x < SnapDistance ? ToolbarDock.Left
            : x + Toolbar.Bounds.Width > root.Width - SnapDistance - 260 && x > root.Width / 2 ? ToolbarDock.Right
            : y < SnapDistance / 2 ? ToolbarDock.Top
            : ToolbarDock.Floating;
        s.ToolbarDock = dock;
        s.ToolbarX = x;
        s.ToolbarY = y;
        Vm!.Main.SaveSettings();
        ApplyDock(dock);
    }
}
