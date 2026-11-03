using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace PdfReader.App.Controls;

/// <summary>
/// An icon: a Segoe Fluent Icons glyph (string) for crisp, pixel-aligned Windows-native icons, or a
/// 24×24 line geometry for the few custom shapes. Color follows the inherited Foreground.
/// </summary>
public sealed class Icon : Control
{
    private static readonly FontFamily GlyphFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Typeface GlyphTypeface = new(GlyphFont);

    public static readonly StyledProperty<object?> DataProperty =
        AvaloniaProperty.Register<Icon, object?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.35);

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, ForegroundProperty, StrokeThicknessProperty);
        WidthProperty.OverrideDefaultValue<Icon>(16);
        HeightProperty.OverrideDefaultValue<Icon>(16);
        IsHitTestVisibleProperty.OverrideDefaultValue<Icon>(false);
    }

    /// <summary>A glyph string (e.g. "") or a <see cref="Geometry"/>.</summary>
    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Foreground is not { } brush)
            return;
        var box = Math.Min(Bounds.Width, Bounds.Height);
        if (box <= 0)
            return;

        switch (Data)
        {
            case string glyph when glyph.Length > 0:
            {
                // Fluent icons are designed on a 16px grid: whole-pixel sizes and positions keep them sharp.
                var size = Math.Max(8, Math.Round(box));
                var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, GlyphTypeface, size, brush);
                var x = Math.Round((Bounds.Width - text.Width) / 2);
                var y = Math.Round((Bounds.Height - text.Height) / 2);
                context.DrawText(text, new Point(x, y));
                break;
            }
            case Geometry geometry:
            {
                var scale = box / 24.0;
                var pen = new Pen(brush, StrokeThickness / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                var ox = Math.Round((Bounds.Width - box) / 2);
                var oy = Math.Round((Bounds.Height - box) / 2);
                using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(ox, oy)))
                    context.DrawGeometry(null, pen, geometry);
                break;
            }
        }
    }
}
