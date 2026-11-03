using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PdfReader.App.ViewModels;

namespace PdfReader.App.Views;

public sealed partial class DocumentView : UserControl
{
    private readonly DispatcherTimer _fadeTimer;
    private bool _pointerOverBar;

    public DocumentView()
    {
        InitializeComponent();
        _fadeTimer = new DispatcherTimer(TimeSpan.FromSeconds(2.5), DispatcherPriority.Background, (_, _) => FadeOut());
        _fadeTimer.Start();
        BottomBar.PointerExited += (_, _) => _pointerOverBar = false;
    }

    private DocumentViewModel? Vm => DataContext as DocumentViewModel;

    private void OnPointerActivity(object? sender, PointerEventArgs e)
    {
        BottomBar.Opacity = 1;
        BottomBar.IsHitTestVisible = true;
        _fadeTimer.Stop();
        _fadeTimer.Start();
    }

    private void OnBottomBarEntered(object? sender, PointerEventArgs e) => _pointerOverBar = true;

    private void FadeOut()
    {
        if (_pointerOverBar || PageBox.IsFocused || BottomBar.IsKeyboardFocusWithin)
            return;
        BottomBar.Opacity = 0;
        BottomBar.IsHitTestVisible = false;
        _fadeTimer.Stop();
    }

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
    }

    private void OnZoomPreset(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom))
            Vm?.Request(new ViewerRequest.SetZoom(zoom));
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _fadeTimer.Stop();
    }
}
