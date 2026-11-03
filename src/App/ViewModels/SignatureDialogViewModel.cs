using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfReader.App.Localization;
using PdfReader.Core.Markup;
using PdfReader.Core.Settings;
using PdfReader.Rendering;

namespace PdfReader.App.ViewModels;

/// <summary>Create a signature or initials: draw with mouse/pen/touch, or type it.</summary>
public sealed partial class SignatureDialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<SignatureData?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SignatureDialogViewModel(bool initials, SignatureData? existing)
    {
        IsInitials = initials;
        TypedText = existing?.Typed ?? "";
        IsTypeTab = existing?.Typed is { Length: > 0 };
    }

    public bool IsInitials { get; }
    public string Title => Loc.Get(IsInitials ? "CreateInitials" : "CreateSignature");
    public Task<SignatureData?> Result => _result.Task;

    /// <summary>Strokes drawn on the pad, in pad coordinates (DIPs). Filled by the view.</summary>
    public List<List<Point>> Strokes { get; } = [];

    [ObservableProperty] public partial bool IsTypeTab { get; set; }
    public bool IsDrawTab { get => !IsTypeTab; set => IsTypeTab = !value; }
    partial void OnIsTypeTabChanged(bool value) => OnPropertyChanged(nameof(IsDrawTab));

    [ObservableProperty] public partial string TypedText { get; set; } = "";

    /// <summary>Raised to make the pad clear itself.</summary>
    public event Action? Cleared;

    [RelayCommand]
    private void Clear()
    {
        Strokes.Clear();
        TypedText = "";
        Cleared?.Invoke();
    }

    [RelayCommand]
    private void Save()
    {
        if (IsTypeTab)
        {
            if (string.IsNullOrWhiteSpace(TypedText))
                return;
            _result.TrySetResult(new SignatureData { Typed = TypedText.Trim() });
            return;
        }
        var strokes = Strokes.Where(s => s.Count > 1)
            .Select(s => (IReadOnlyList<PdfPoint>)s.Select(p => new PdfPoint(p.X, p.Y)).ToList()).ToList();
        if (strokes.Count == 0)
            return;
        var (normalized, aspect) = ShapeBuilder.Normalize(strokes);
        _result.TrySetResult(new SignatureData
        {
            Aspect = aspect,
            Strokes = normalized.Select(s => s.SelectMany(p => new[] { Math.Round(p.X, 4), Math.Round(p.Y, 4) }).ToArray()).ToList(),
        });
    }

    [RelayCommand] public void Cancel() => _result.TrySetResult(null);
}
