using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using PdfReader.App.ViewModels;

namespace PdfReader.App.Views;

public sealed partial class ToolsPanel : UserControl
{
    public ToolsPanel()
    {
        InitializeComponent();
        // Re-evaluate which tools apply each time the panel opens.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible && DataContext is MainViewModel vm)
            {
                DataContext = null;
                DataContext = vm;
            }
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => Close();
    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void Close()
    {
        if (Vm is { } vm)
            vm.IsToolsPanelOpen = false;
    }

    private void OnToolClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AppCommand command })
        {
            Close();
            if (command.CanExecute())
                command.Execute();
        }
    }
}

/// <summary>Turns an AppCommand.CanExecute delegate into a bool for IsEnabled.</summary>
public sealed class CanExecuteConverter : IValueConverter
{
    public static readonly CanExecuteConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Func<bool> canExecute ? canExecute() : true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
