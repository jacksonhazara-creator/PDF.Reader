using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfReader.Core.Settings;
using PdfReader.Rendering;

namespace PdfReader.App.ViewModels;

public enum EditTool { Select, Marker, Comment }

public sealed record ColorSwatch(string Hex)
{
    public IBrush Brush { get; } = Avalonia.Media.Brush.Parse(Hex);
}

public sealed record ShapeChoice(CommentShape Shape, string IconKey, string TitleKey)
{
    public string Title => Localization.Loc.Get(TitleKey);
    public Geometry? Icon => Avalonia.Application.Current?.TryGetResource(IconKey, null, out var g) == true ? g as Geometry : null;
}

/// <summary>The active tool (Select / Marker / Comment) and their options. Options persist in settings.</summary>
public sealed partial class MarkupViewModel(MainViewModel main) : ObservableObject
{
    private AppSettings S => main.Settings;

    public IReadOnlyList<ColorSwatch> MarkerColors { get; } =
        [new("#FFE600"), new("#7CFC00"), new("#00E5FF"), new("#FF6EC7"), new("#FF9F1C"), new("#FF3B30"), new("#A66CFF")];

    public IReadOnlyList<ColorSwatch> CommentColors { get; } =
        [new("#FFB020"), new("#2F6FEB"), new("#1F9D55"), new("#D9363E"), new("#8E5BE8"), new("#6B7280")];

    public IReadOnlyList<ShapeChoice> Shapes { get; } =
    [
        new(CommentShape.Square, "ShapeSquare", "ShapeSquare"),
        new(CommentShape.Circle, "ShapeCircle", "ShapeCircle"),
        new(CommentShape.Cloud, "ShapeCloud", "ShapeCloud"),
        new(CommentShape.Bubble, "ShapeBubble", "ShapeBubble"),
        new(CommentShape.Star, "ShapeStar", "ShapeStar"),
    ];

    // --- Tool ----------------------------------------------------------------------------

    [ObservableProperty] public partial EditTool Tool { get; set; }

    public bool IsSelect { get => Tool == EditTool.Select; set { if (value) Tool = EditTool.Select; } }
    public bool IsMarker { get => Tool == EditTool.Marker; set { Tool = value ? EditTool.Marker : EditTool.Select; } }
    public bool IsComment { get => Tool == EditTool.Comment; set { Tool = value ? EditTool.Comment : EditTool.Select; } }
    public bool HasOptions => Tool != EditTool.Select;

    partial void OnToolChanged(EditTool value)
    {
        OnPropertyChanged(nameof(IsSelect));
        OnPropertyChanged(nameof(IsMarker));
        OnPropertyChanged(nameof(IsComment));
        OnPropertyChanged(nameof(HasOptions));
    }

    // --- Marker ----------------------------------------------------------------------------

    public string MarkerColor
    {
        get => S.MarkerColor;
        set { S.MarkerColor = value; Save(); OnPropertyChanged(); NotifyMarker(); }
    }

    /// <summary>Slider 0–90 (%).</summary>
    public double MarkerTransparency
    {
        get => Math.Round(S.MarkerTransparency * 100);
        set { S.MarkerTransparency = Math.Clamp(value, 0, 90) / 100; Save(); OnPropertyChanged(); NotifyMarker(); }
    }

    /// <summary>Slider 20–100 (%): how strong / saturated the color is.</summary>
    public double MarkerStrength
    {
        get => Math.Round(S.MarkerStrength * 100);
        set { S.MarkerStrength = Math.Clamp(value, 20, 100) / 100; Save(); OnPropertyChanged(); NotifyMarker(); }
    }

    /// <summary>Slider 2–24 (pt): pen width when drawing.</summary>
    public double MarkerThickness
    {
        get => S.MarkerThickness;
        set { S.MarkerThickness = Math.Clamp(Math.Round(value), 2, 24); Save(); OnPropertyChanged(); }
    }

    /// <summary>The marker exactly as it will look on the page.</summary>
    public IBrush MarkerPreview => new SolidColorBrush(Color.FromUInt32(S.ResolveMarkerArgb()));
    public uint MarkerArgb => S.ResolveMarkerArgb();

    [RelayCommand] private void SetMarkerColor(ColorSwatch swatch) => MarkerColor = swatch.Hex;

    private void NotifyMarker()
    {
        OnPropertyChanged(nameof(MarkerPreview));
        OnPropertyChanged(nameof(MarkerArgb));
    }

    // --- Comments -------------------------------------------------------------------------

    public CommentShape Shape
    {
        get => S.CommentShape;
        set { S.CommentShape = value; Save(); OnPropertyChanged(); OnPropertyChanged(nameof(ShapeIcon)); }
    }

    public Geometry? ShapeIcon => Shapes.First(s => s.Shape == Shape).Icon;

    public string CommentColor
    {
        get => S.CommentColor;
        set { S.CommentColor = value; Save(); OnPropertyChanged(); OnPropertyChanged(nameof(CommentBrush)); }
    }

    public IBrush CommentBrush => Avalonia.Media.Brush.Parse(S.CommentColor);

    public bool OnLastWord
    {
        get => S.CommentOnLastWord;
        set { S.CommentOnLastWord = value; Save(); OnPropertyChanged(); OnPropertyChanged(nameof(OnFirstWord)); }
    }

    public bool OnFirstWord
    {
        get => !S.CommentOnLastWord;
        set => OnLastWord = !value;
    }

    [RelayCommand] private void SetShape(ShapeChoice choice) => Shape = choice.Shape;
    [RelayCommand] private void SetCommentColor(ColorSwatch swatch) => CommentColor = swatch.Hex;

    private void Save() => main.SaveSettings();
}
