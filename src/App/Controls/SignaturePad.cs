using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PdfReader.App.Controls;

/// <summary>Draw a signature with mouse, pen or finger. Strokes go into <see cref="Strokes"/>.</summary>
public sealed class SignaturePad : Control
{
    private List<Point>? _current;

    public List<List<Point>> Strokes { get; set; } = [];

    public SignaturePad()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public void Clear()
    {
        Strokes.Clear();
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _current = [e.GetPosition(this)];
        Strokes.Add(_current);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_current is null)
            return;
        var p = e.GetPosition(this);
        var last = _current[^1];
        if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) > 0.8)
        {
            _current.Add(p);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _current = null;
        e.Pointer.Capture(null);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size), 8);
        var baseline = Bounds.Height * 0.75;
        context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), 1, dashStyle: DashStyle.Dash),
            new Point(16, baseline), new Point(Bounds.Width - 16, baseline));
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x6C)), 2.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (var stroke in Strokes)
        {
            if (stroke.Count < 2)
                continue;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(stroke[0], false);
                foreach (var p in stroke.Skip(1))
                    ctx.LineTo(p);
                ctx.EndFigure(false);
            }
            context.DrawGeometry(null, pen, g);
        }
    }
}
