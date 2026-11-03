using Avalonia.Controls;
using PdfReader.App.ViewModels;

namespace PdfReader.App.Views;

public sealed partial class SignatureDialog : UserControl
{
    private SignatureDialogViewModel? _vm;

    public SignatureDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
                _vm.Cleared -= Pad.Clear;
            _vm = DataContext as SignatureDialogViewModel;
            if (_vm is null)
                return;
            Pad.Strokes = _vm.Strokes;
            Pad.InvalidateVisual();
            _vm.Cleared += Pad.Clear;
        };
    }
}
