using PdfReader.Rendering;

namespace PdfReader.Core.Markup;

/// <summary>Pure geometry for drawing tools, in page points (top-left origin). No PDF or UI dependencies.</summary>
public static class ShapeBuilder
{
    /// <summary>Two short strokes forming an arrow head at <paramref name="tip"/>, pointing away from <paramref name="from"/>.</summary>
    public static IReadOnlyList<PdfPoint> ArrowHead(PdfPoint from, PdfPoint tip, double size)
    {
        var angle = Math.Atan2(tip.Y - from.Y, tip.X - from.X);
        PdfPoint Wing(double delta) => new(tip.X - size * Math.Cos(angle + delta), tip.Y - size * Math.Sin(angle + delta));
        return [Wing(0.45), tip, Wing(-0.45)];
    }

    /// <summary>A filled block arrow from <paramref name="from"/> to <paramref name="tip"/>.</summary>
    public static IReadOnlyList<PdfPoint> BlockArrow(PdfPoint from, PdfPoint tip, double width)
    {
        var dx = tip.X - from.X;
        var dy = tip.Y - from.Y;
        var length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        var ux = dx / length;
        var uy = dy / length;
        var nx = -uy;
        var ny = ux;
        var head = Math.Min(length * 0.45, width * 2.2);
        var shaft = width / 2;
        var wing = width;
        var bx = tip.X - ux * head;
        var by = tip.Y - uy * head;
        return
        [
            new(from.X + nx * shaft, from.Y + ny * shaft),
            new(bx + nx * shaft, by + ny * shaft),
            new(bx + nx * wing, by + ny * wing),
            tip,
            new(bx - nx * wing, by - ny * wing),
            new(bx - nx * shaft, by - ny * shaft),
            new(from.X - nx * shaft, from.Y - ny * shaft),
        ];
    }

    /// <summary>A closed "cloud" outline: bumps around the ellipse inscribed in <paramref name="rect"/>.</summary>
    public static IReadOnlyList<PdfPoint> Cloud(PdfRect rect, double bumpSize = 12)
    {
        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;
        var rx = Math.Max(4, rect.Width / 2);
        var ry = Math.Max(4, rect.Height / 2);
        var perimeter = Math.PI * (3 * (rx + ry) - Math.Sqrt((3 * rx + ry) * (rx + 3 * ry)));
        var bumps = Math.Clamp((int)(perimeter / bumpSize), 6, 80);
        var points = new List<PdfPoint>();
        for (var i = 0; i < bumps; i++)
        {
            var a0 = 2 * Math.PI * i / bumps;
            var a1 = 2 * Math.PI * (i + 1) / bumps;
            for (var s = 0; s <= 8; s++)
            {
                var t = s / 8.0;
                var a = a0 + (a1 - a0) * t;
                var bulge = 1 + 0.18 * Math.Sin(Math.PI * t); // outward arc between bump joints
                points.Add(new PdfPoint(cx + rx * bulge * Math.Cos(a), cy + ry * bulge * Math.Sin(a)));
            }
        }
        points.Add(points[0]);
        return points;
    }

    public static IReadOnlyList<PdfPoint> Ellipse(PdfRect rect, int segments = 48)
    {
        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;
        return Enumerable.Range(0, segments + 1)
            .Select(i => 2 * Math.PI * i / segments)
            .Select(a => new PdfPoint(cx + rect.Width / 2 * Math.Cos(a), cy + rect.Height / 2 * Math.Sin(a)))
            .ToList();
    }

    public static IReadOnlyList<PdfPoint> CheckMark(PdfPoint center, double size)
        => [new(center.X - size * 0.45, center.Y), new(center.X - size * 0.12, center.Y + size * 0.35), new(center.X + size * 0.5, center.Y - size * 0.4)];

    public static IReadOnlyList<IReadOnlyList<PdfPoint>> CrossMark(PdfPoint center, double size)
    {
        var h = size * 0.4;
        return
        [
            [new(center.X - h, center.Y - h), new(center.X + h, center.Y + h)],
            [new(center.X + h, center.Y - h), new(center.X - h, center.Y + h)],
        ];
    }

    /// <summary>Scales normalized (0..1) signature strokes into a box of the given width, centered on a point.</summary>
    public static IReadOnlyList<IReadOnlyList<PdfPoint>> PlaceStrokes(IReadOnlyList<IReadOnlyList<PdfPoint>> normalized, PdfPoint center, double width, double aspect)
    {
        var height = width / Math.Max(0.1, aspect);
        var left = center.X - width / 2;
        var top = center.Y - height / 2;
        return normalized.Select(s => (IReadOnlyList<PdfPoint>)s.Select(p => new PdfPoint(left + p.X * width, top + p.Y * height)).ToList()).ToList();
    }

    /// <summary>Normalizes raw strokes into 0..1 within their bounding box; returns width/height aspect.</summary>
    public static (IReadOnlyList<IReadOnlyList<PdfPoint>> Strokes, double Aspect) Normalize(IReadOnlyList<IReadOnlyList<PdfPoint>> strokes)
    {
        var all = strokes.SelectMany(s => s).ToList();
        if (all.Count == 0)
            return ([], 3);
        var minX = all.Min(p => p.X); var maxX = all.Max(p => p.X);
        var minY = all.Min(p => p.Y); var maxY = all.Max(p => p.Y);
        var w = Math.Max(1, maxX - minX);
        var h = Math.Max(1, maxY - minY);
        var normalized = strokes.Select(s => (IReadOnlyList<PdfPoint>)s.Select(p => new PdfPoint((p.X - minX) / w, (p.Y - minY) / h)).ToList()).ToList();
        return (normalized, w / h);
    }

    /// <summary>Rounded rectangle outline as a closed polyline.</summary>
    public static IReadOnlyList<PdfPoint> RoundedRect(PdfRect r, double radius)
    {
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        var points = new List<PdfPoint>();
        void Corner(double cx, double cy, double startAngle)
        {
            for (var i = 0; i <= 6; i++)
            {
                var a = startAngle + Math.PI / 2 * i / 6;
                points.Add(new PdfPoint(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a)));
            }
        }
        Corner(r.Right - radius, r.Y + radius, -Math.PI / 2);
        Corner(r.Right - radius, r.Bottom - radius, 0);
        Corner(r.X + radius, r.Bottom - radius, Math.PI / 2);
        Corner(r.X + radius, r.Y + radius, Math.PI);
        return points;
    }
}
