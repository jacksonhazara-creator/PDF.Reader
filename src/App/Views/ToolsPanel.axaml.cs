using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using PdfReader.App.ViewModels;

namespace PdfReader.App.Views;

public sealed partial class ToolsPanel : UserControl
{
    // Tools that open something on top: the docked panel steps aside for them.
    private static readonly HashSet<string> ClosesPanel = ["palette", "settings", "updates", "tools", "readingMode"];

    public ToolsPanel()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>Which tools apply (a document open, a selection...) can change at any time.</summary>
    public void RefreshAvailability()
    {
        var source = Groups.ItemsSource;
        Groups.ItemsSource = null;
        Groups.ItemsSource = source;
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
            vm.IsToolsPanelOpen = false;
    }

    private void OnToolClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppCommand command } || Vm is not { } vm)
            return;
        if (ClosesPanel.Contains(command.Id))
            vm.IsToolsPanelOpen = false;
        if (command.CanExecute())
            command.Execute();
        RefreshAvailability();
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
