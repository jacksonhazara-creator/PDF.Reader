using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PdfReader.App.ViewModels;

namespace PdfReader.App.Views;

public sealed partial class DocumentView : UserControl
{
    public DocumentView() => InitializeComponent();

    private DocumentViewModel? Vm => DataContext as DocumentViewModel;

    private void OnPageBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitPage();
            Viewer.Focus();
            e.Handled = true;
        }
    }

    private void OnPageBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            PageBox.Text = vm.CurrentPageNumber.ToString(CultureInfo.CurrentCulture);
    }

    private void CommitPage()
    {
        if (Vm is { } vm && int.TryParse(PageBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var page))
            vm.GoToPage(page - 1);
        else if (Vm is { } v)
            PageBox.Text = v.CurrentPageNumber.ToString(CultureInfo.CurrentCulture);
    }

    private void OnZoomPreset(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom))
            Vm?.Request(new ViewerRequest.SetZoom(zoom));
    }

    private void OnUndo(object? sender, RoutedEventArgs e) => _ = Vm?.UndoAsync();
}
