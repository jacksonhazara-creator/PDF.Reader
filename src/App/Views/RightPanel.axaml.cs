using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PdfReader.App.ViewModels;

namespace PdfReader.App.Views;

public sealed partial class RightPanel : UserControl
{
    public RightPanel() => InitializeComponent();

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
