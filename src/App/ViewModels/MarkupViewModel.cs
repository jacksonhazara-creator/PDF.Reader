using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfReader.App.Localization;
using PdfReader.Core.Settings;
using PdfReader.Rendering;

namespace PdfReader.App.ViewModels;

public enum ToolKind
{
    Select, Pan,
    Comment, ReplaceText, InsertText, TextComment, AttachFile,
    Highlight, Underline, StrikeOut,
    Draw, Line, LineArrow, Arrow, Rectangle, Circle, Callout, Polygon, Cloud, ConnectedLine, Stamp,
    TypeText, Crossmark, Checkmark, Dot, CircleAround, Crossout,
    Signature, Initials,
}

/// <summary>How a tool is used on the page.</summary>
public enum ToolInput { Select, Pan, TextSelect, Click, DragShape, Freehand, MultiClick }

public sealed record ToolItem(ToolKind Kind, string IconKey, string TitleKey)
{
    public string Title => Loc.Get(TitleKey);
    public object? Icon => Avalonia.Application.Current?.TryGetResource(IconKey, null, out var g) == true ? g : null;
    public override string ToString() => Title;

    public ToolInput Input => Kind switch
    {
        ToolKind.Select => ToolInput.Select,
        ToolKind.Pan => ToolInput.Pan,
        ToolKind.Comment or ToolKind.ReplaceText or ToolKind.Highlight or ToolKind.Underline or ToolKind.StrikeOut => ToolInput.TextSelect,
        ToolKind.Draw => ToolInput.Freehand,
        ToolKind.Line or ToolKind.LineArrow or ToolKind.Arrow or ToolKind.Rectangle or ToolKind.Circle or ToolKind.Cloud
            or ToolKind.CircleAround or ToolKind.Crossout or ToolKind.Callout => ToolInput.DragShape,
        ToolKind.Polygon or ToolKind.ConnectedLine => ToolInput.MultiClick,
        _ => ToolInput.Click,
    };
}

public sealed partial class ToolGroupViewModel(string titleKey, IReadOnlyList<ToolItem> items) : ObservableObject
{
    public string Title => Loc.Get(titleKey);
    public IReadOnlyList<ToolItem> Items { get; } = items;

    [ObservableProperty] public partial ToolItem Current { get; set; } = items[0];
    [ObservableProperty] public partial bool IsActive { get; set; }

    public object? Icon => Current.Icon;
    partial void OnCurrentChanged(ToolItem value) => OnPropertyChanged(nameof(Icon));
}

public sealed record ColorSwatch(string Hex)
{
    public IBrush Brush { get; } = Avalonia.Media.Brush.Parse(Hex);
    public override string ToString() => Hex;
}

public sealed record ShapeChoice(CommentShape Shape, string IconKey, string TitleKey)
{
    public string Title => Loc.Get(TitleKey);
    public object? Icon => Avalonia.Application.Current?.TryGetResource(IconKey, null, out var g) == true ? g : null;
}

public sealed record StampChoice(string LabelKey, string Hex)
{
    public string Label => Loc.Get(LabelKey);
    public IBrush Brush { get; } = Avalonia.Media.Brush.Parse(Hex);
    public uint Argb { get; } = ColorParser.ParseOrDefault(Hex, 0xFF1F9D55);
}

/// <summary>The active tool (grouped as on the toolbar) and every tool's options. Options persist in settings.</summary>
public sealed partial class MarkupViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public MarkupViewModel(MainViewModel main)
    {
        _main = main;
        Groups =
        [
            new("GroupSelect", [T(ToolKind.Select, "IconSelect", "ToolSelect"), T(ToolKind.Pan, "IconPan", "ToolPan")]),
            new("GroupComment", [T(ToolKind.Comment, "IconComment", "ToolComment"), T(ToolKind.ReplaceText, "IconReplace", "ToolReplace"),
                T(ToolKind.InsertText, "IconInsert", "ToolInsert"), T(ToolKind.TextComment, "IconTextComment", "ToolTextComment"),
                T(ToolKind.AttachFile, "IconAttach", "ToolAttach")]),
            new("GroupMarker", [T(ToolKind.Highlight, "IconMarker", "ToolHighlight"), T(ToolKind.Underline, "IconUnderline", "ToolUnderline"),
                T(ToolKind.StrikeOut, "IconStrike", "ToolStrike")]),
            new("GroupDraw", [T(ToolKind.Draw, "IconDraw", "ToolDraw"), T(ToolKind.Line, "IconLine", "ToolLine"),
                T(ToolKind.LineArrow, "IconLineArrow", "ToolLineArrow"), T(ToolKind.Arrow, "IconArrow", "ToolArrow"),
                T(ToolKind.Rectangle, "ShapeSquare", "ToolRectangle"), T(ToolKind.Circle, "ShapeCircle", "ToolCircle"),
                T(ToolKind.Callout, "IconCallout", "ToolCallout"), T(ToolKind.Polygon, "IconPolygon", "ToolPolygon"),
                T(ToolKind.Cloud, "ShapeCloud", "ToolCloud"), T(ToolKind.ConnectedLine, "IconPolyline", "ToolConnectedLine"),
                T(ToolKind.Stamp, "IconStamp", "ToolStamp")]),
            new("GroupText", [T(ToolKind.TypeText, "IconTypeText", "ToolTypeText"), T(ToolKind.Crossmark, "IconCross", "ToolCrossmark"),
                T(ToolKind.Checkmark, "IconCheck", "ToolCheckmark"), T(ToolKind.Dot, "IconDot", "ToolDot"),
                T(ToolKind.CircleAround, "IconCircleAround", "ToolCircleAround"), T(ToolKind.Crossout, "IconCrossout", "ToolCrossout")]),
            new("GroupSignature", [T(ToolKind.Signature, "IconSignature", "ToolSignature"), T(ToolKind.Initials, "IconInitials", "ToolInitials")]),
        ];
        UpdateGroups();
    }

    private static ToolItem T(ToolKind kind, string icon, string title) => new(kind, icon, title);
    private AppSettings S => _main.Settings;
    private void Save() => _main.SaveSettings();

    public IReadOnlyList<ToolGroupViewModel> Groups { get; }
    public IEnumerable<ToolItem> AllTools => Groups.SelectMany(g => g.Items);

    // --- Active tool ---------------------------------------------------------------------

    [ObservableProperty] public partial ToolKind Tool { get; set; }

    public ToolItem Current => AllTools.First(t => t.Kind == Tool);
    public ToolInput Input => Current.Input;
    public string Hint => Loc.Get("Hint" + Tool);

    public bool IsSelect { get => Tool == ToolKind.Select; set { if (value) Tool = ToolKind.Select; } }

    [RelayCommand] public void SelectTool(ToolItem item) => Tool = item.Kind;

    /// <summary>Toggle: choosing the active tool again returns to Select.</summary>
    public void Toggle(ToolKind kind) => Tool = Tool == kind ? ToolKind.Select : kind;

    partial void OnToolChanged(ToolKind value)
    {
        UpdateGroups();
        foreach (var name in new[]
                 {
                     nameof(Current), nameof(Input), nameof(Hint), nameof(IsSelect), nameof(HasOptions), nameof(ShowMarkerOptions),
                     nameof(ShowCommentOptions), nameof(ShowDrawOptions), nameof(ShowTextOptions), nameof(ShowStampOptions),
                     nameof(ShowSignatureOptions), nameof(ShowThickness),
                 })
            OnPropertyChanged(name);
    }

    private void UpdateGroups()
    {
        foreach (var g in Groups)
        {
            var item = g.Items.FirstOrDefault(i => i.Kind == Tool);
            g.IsActive = item is not null;
            if (item is not null)
                g.Current = item;
        }
    }

    // --- Which options to show -------------------------------------------------------------

    public bool ShowMarkerOptions => Tool is ToolKind.Highlight or ToolKind.Underline or ToolKind.StrikeOut;
    public bool ShowCommentOptions => Tool is ToolKind.Comment or ToolKind.ReplaceText or ToolKind.InsertText or ToolKind.AttachFile;
    public bool ShowDrawOptions => Tool is ToolKind.Draw or ToolKind.Line or ToolKind.LineArrow or ToolKind.Arrow or ToolKind.Rectangle
        or ToolKind.Circle or ToolKind.Callout or ToolKind.Polygon or ToolKind.Cloud or ToolKind.ConnectedLine
        or ToolKind.TypeText or ToolKind.TextComment or ToolKind.Crossmark or ToolKind.Checkmark or ToolKind.Dot or ToolKind.CircleAround or ToolKind.Crossout;
    public bool ShowThickness => ShowDrawOptions && Tool is not (ToolKind.TypeText or ToolKind.TextComment or ToolKind.Dot);
    public bool ShowTextOptions => Tool is ToolKind.TypeText or ToolKind.Callout or ToolKind.TextComment;
    public bool ShowStampOptions => Tool == ToolKind.Stamp;
    public bool ShowSignatureOptions => Tool is ToolKind.Signature or ToolKind.Initials;
    public bool HasOptions => Tool is not (ToolKind.Select or ToolKind.Pan);

    // --- Marker (highlight / underline / strike-through) ---------------------------------------

    public IReadOnlyList<ColorSwatch> MarkerColors { get; } =
        [new("#FFE600"), new("#7CFC00"), new("#00E5FF"), new("#FF6EC7"), new("#FF9F1C"), new("#FF3B30"), new("#A66CFF")];

    public string MarkerColor { get => S.MarkerColor; set { S.MarkerColor = value; Save(); OnPropertyChanged(); NotifyMarker(); } }

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

    public IBrush MarkerPreview => new SolidColorBrush(Color.FromUInt32(S.ResolveMarkerArgb()));
    public uint MarkerArgb => S.ResolveMarkerArgb();

    [RelayCommand] private void SetMarkerColor(ColorSwatch swatch) => MarkerColor = swatch.Hex;
    private void NotifyMarker() { OnPropertyChanged(nameof(MarkerPreview)); OnPropertyChanged(nameof(MarkerArgb)); }

    // --- Draw & text ---------------------------------------------------------------------------

    public IReadOnlyList<ColorSwatch> DrawColors { get; } =
        [new("#D9363E"), new("#1B1B1B"), new("#2F6FEB"), new("#1F9D55"), new("#E8762C"), new("#8E5BE8"), new("#FFFFFF")];

    public string DrawColor { get => S.DrawColor; set { S.DrawColor = value; Save(); OnPropertyChanged(); NotifyDraw(); } }

    public double DrawTransparency
    {
        get => Math.Round(S.DrawTransparency * 100);
        set { S.DrawTransparency = Math.Clamp(value, 0, 90) / 100; Save(); OnPropertyChanged(); NotifyDraw(); }
    }

    /// <summary>Pen width in points, 0.5–16.</summary>
    public double DrawThickness
    {
        get => S.DrawThickness;
        set { S.DrawThickness = Math.Clamp(Math.Round(value * 2) / 2, 0.5, 16); Save(); OnPropertyChanged(); }
    }

    /// <summary>Font size for typed text, 8–48 pt.</summary>
    public double TextSize
    {
        get => S.TextSize;
        set { S.TextSize = Math.Clamp(Math.Round(value), 8, 48); Save(); OnPropertyChanged(); }
    }

    public uint DrawArgb => S.ResolveDrawArgb();
    public IBrush DrawPreview => new SolidColorBrush(Color.FromUInt32(S.ResolveDrawArgb()));

    [RelayCommand] private void SetDrawColor(ColorSwatch swatch) => DrawColor = swatch.Hex;
    private void NotifyDraw() { OnPropertyChanged(nameof(DrawArgb)); OnPropertyChanged(nameof(DrawPreview)); }

    // --- Stamps --------------------------------------------------------------------------------

    public IReadOnlyList<StampChoice> Stamps { get; } =
    [
        new("StampApproved", "#1F9D55"), new("StampReviewed", "#2F6FEB"), new("StampFinal", "#2F6FEB"), new("StampDraft", "#6B7280"),
        new("StampForComment", "#E8762C"), new("StampConfidential", "#D9363E"), new("StampNotApproved", "#D9363E"), new("StampRejected", "#D9363E"),
    ];

    public StampChoice CurrentStamp => Stamps.FirstOrDefault(s => s.LabelKey == S.StampLabel) ?? Stamps[0];

    [RelayCommand]
    private void SetStamp(StampChoice stamp)
    {
        S.StampLabel = stamp.LabelKey;
        Save();
        OnPropertyChanged(nameof(CurrentStamp));
    }

    // --- Comments --------------------------------------------------------------------------------

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

    public CommentShape Shape
    {
        get => S.CommentShape;
        set { S.CommentShape = value; Save(); OnPropertyChanged(); OnPropertyChanged(nameof(ShapeIcon)); }
    }

    public object? ShapeIcon => Shapes.First(s => s.Shape == Shape).Icon;
    public string CommentColor { get => S.CommentColor; set { S.CommentColor = value; Save(); OnPropertyChanged(); OnPropertyChanged(nameof(CommentBrush)); OnPropertyChanged(nameof(CommentArgb)); } }
    public IBrush CommentBrush => Avalonia.Media.Brush.Parse(S.CommentColor);
    public uint CommentArgb => ColorParser.ParseOrDefault(S.CommentColor, 0xFFFFB020);

    public bool OnLastWord
    {
        get => S.CommentOnLastWord;
        set { S.CommentOnLastWord = value; Save(); OnPropertyChanged(); OnPropertyChanged(nameof(OnFirstWord)); }
    }

    public bool OnFirstWord { get => !S.CommentOnLastWord; set => OnLastWord = !value; }

    [RelayCommand] private void SetShape(ShapeChoice choice) => Shape = choice.Shape;
    [RelayCommand] private void SetCommentColor(ColorSwatch swatch) => CommentColor = swatch.Hex;

    // --- Signatures --------------------------------------------------------------------------------

    public bool HasSignature => _main.State.Signature is not null;
    public bool HasInitials => _main.State.Initials is not null;
    public bool IsInitials => Tool == ToolKind.Initials;

    [RelayCommand] private Task EditSignature() => _main.EditSignatureAsync(Tool == ToolKind.Initials);

    public void NotifySignatures()
    {
        OnPropertyChanged(nameof(HasSignature));
        OnPropertyChanged(nameof(HasInitials));
    }
}
