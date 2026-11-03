using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PdfReader.App.ViewModels;

namespace PdfReader.App.Views;

public sealed partial class CommandPalette : UserControl
{
    public CommandPalette()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
                Dispatcher.UIThread.Post(() => QueryBox.Focus(), DispatcherPriority.Input);
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        switch (e.Key)
        {
            case Key.Down:
                vm.Palette.Move(+1);
                Results.ScrollIntoView(vm.Palette.SelectedCommand!);
                e.Handled = true;
                break;
            case Key.Up:
                vm.Palette.Move(-1);
                Results.ScrollIntoView(vm.Palette.SelectedCommand!);
                e.Handled = true;
                break;
            case Key.Enter:
                vm.Palette.Run();
                e.Handled = true;
                break;
            case Key.Escape:
                vm.IsCommandPaletteOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e) => Vm?.Palette.Run();

    private void OnResultReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && Vm?.Palette.SelectedCommand is not null)
            Vm.Palette.Run();
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm)
            vm.IsCommandPaletteOpen = false;
    }
}
