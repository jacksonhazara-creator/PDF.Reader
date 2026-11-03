using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PdfReader.App.ViewModels;

namespace PdfReader.App.Views;

public sealed partial class RightPanel : UserControl
{
    private bool _resizing;
    private double _startX, _startWidth;

    public RightPanel() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is { } vm)
            Expanded.Width = Math.Clamp(vm.Main.Settings.RightPanelWidth, 220, 560);
    }

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _resizing = true;
        _startX = e.GetPosition(null).X;
        _startWidth = Expanded.Width;
        e.Pointer.Capture(ResizeHandle);
        e.Handled = true;
    }

    private void OnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (_resizing)
            Expanded.Width = Math.Clamp(_startWidth - (e.GetPosition(null).X - _startX), 220, 560);
    }

    private void OnResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing)
            return;
        _resizing = false;
        e.Pointer.Capture(null);
        if (Vm is { } vm)
        {
            vm.Main.Settings.RightPanelWidth = Expanded.Width;
            vm.Main.SaveSettings();
        }
    }

    private DocumentViewModel? Vm => DataContext as DocumentViewModel;

    private void OnCollapse(object? sender, RoutedEventArgs e) { if (Vm is { } vm) vm.IsSidebarOpen = false; }
    private void OnExpand(object? sender, RoutedEventArgs e) { if (Vm is { } vm) vm.IsSidebarOpen = true; }
    private void OnShowPages(object? sender, RoutedEventArgs e) => Vm?.ShowSidebar(SidebarTab.Thumbnails);
    private void OnShowBookmarks(object? sender, RoutedEventArgs e) => Vm?.ShowSidebar(SidebarTab.Outline);
    private void OnShowComments(object? sender, RoutedEventArgs e) => Vm?.ShowSidebar(SidebarTab.Comments);

    private void OnPageBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is not { } vm)
            return;
        if (int.TryParse(PageBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var page))
            vm.GoToPage(page - 1);
        else
            PageBox.Text = vm.CurrentPageNumber.ToString(CultureInfo.CurrentCulture);
        e.Handled = true;
    }

    private void OnPageBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            PageBox.Text = vm.CurrentPageNumber.ToString(CultureInfo.CurrentCulture);
    }
}
