using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PdfReader.App.ViewModels;

namespace PdfReader.App.Views;

public sealed partial class DialogView : UserControl
{
    public DialogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is DialogViewModel dialog)
                Dispatcher.UIThread.Post(() =>
                {
                    if (dialog.HasInput)
                    {
                        Input.Focus();
                        Input.SelectAll();
                    }
                    else
                    {
                        PrimaryButton.Focus();
                    }
                }, DispatcherPriority.Input);
        };
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is DialogViewModel dialog)
        {
            dialog.Primary();
            e.Handled = true;
        }
    }
}
