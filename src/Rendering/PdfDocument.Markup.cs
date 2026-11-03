using System.Text;
using PdfReader.Rendering.Native;

namespace PdfReader.Rendering;

public enum TextMarkupKind { Highlight, Underline, Squiggly, StrikeOut }
public enum ShapeKind { Rectangle, Ellipse }

/// <summary>A piece of a stamp's drawing, in page points (top-left origin).</summary>
public abstract record StampPart;

/// <summary>A polyline or polygon. Closed paths may be filled.</summary>
public sealed record StampPath(IReadOnlyList<PdfPoint> Points, bool Closed, uint Stroke, double Width, uint? Fill = null) : StampPart;

/// <summary>Text starting at a baseline point. Font is a standard PDF font name, or FontData holds a TrueType file to embed.</summary>
public sealed record StampText(string Text, PdfPoint Baseline, double Size, uint Color, string Font = "Helvetica", byte[]? FontData = null) : StampPart;

/// <summary>Text markup, shapes, stamps (typed text, symbols, signatures), inserts and attachments.</summary>
public sealed partial class PdfDocument
{
    private const string KeyKind = "PDFR_Kind";

    public Task<string> AddTextMarkupAsync(int pageIndex, TextMarkupKind kind, IReadOnlyList<PdfRect> rects, uint argb,
        string? contents = null, string? tag = null, string? quote = null)
        => _engine.RunAsync(() => AddTextMarkup(pageIndex, kind, rects, argb, contents, tag, quote), WorkPriority.Immediate);

    public Task<string> AddShapeAsync(int pageIndex, ShapeKind kind, PdfRect rect, uint stroke, double width, uint? fill = null)
        => _engine.RunAsync(() => AddShape(pageIndex, kind, rect, stroke, width, fill), WorkPriority.Immediate);

    /// <summary>A stamp annotation drawn from text and paths: typed text, callouts, check marks, stamps, signatures.</summary>
    public Task<string> AddStampAsync(int pageIndex, IReadOnlyList<StampPart> parts, string tag, string? contents = null)
        => _engine.RunAsync(() => AddStamp(pageIndex, parts, tag, contents), WorkPriority.Immediate);

    /// <summary>Width of a line of text in points (to size boxes around typed text).</summary>
    public Task<double> MeasureTextAsync(string text, double size, string font = "Helvetica", byte[]? fontData = null)
        => _engine.RunAsync(() => MeasureText(text, size, font, fontData), WorkPriority.Interactive);

    /// <summary>"Insert text here": a caret mark with the text to insert as its comment. (PDFium cannot create /Caret annotations, so this is a note drawn as a caret.)</summary>
    public Task<string> AddInsertAsync(int pageIndex, PdfRect caret, string text, uint argb)
        => _engine.RunAsync(() => AddCaretOrAttachment(pageIndex, Pdfium.FPDF_ANNOT_TEXT, caret, text, argb, "Insert", null, null), WorkPriority.Immediate);

    /// <summary>Embeds a file in the PDF behind a paper-clip marker.</summary>
    public Task<string> AddFileAttachmentAsync(int pageIndex, PdfRect marker, string fileName, byte[] data, uint argb)
        => _engine.RunAsync(() => AddCaretOrAttachment(pageIndex, Pdfium.FPDF_ANNOT_FILEATTACHMENT, marker, fileName, argb, "Attachment", fileName, data), WorkPriority.Immediate);

    // ---------------------------------------------------------------------------------------

    private unsafe string AddTextMarkup(int pageIndex, TextMarkupKind kind, IReadOnlyList<PdfRect> rects, uint argb, string? contents, string? tag, string? quote)
    {
        if (rects.Count == 0)
            throw new ArgumentException("Nothing selected.", nameof(rects));
        var subtype = kind switch
        {
            TextMarkupKind.Underline => Pdfium.FPDF_ANNOT_UNDERLINE,
            TextMarkupKind.Squiggly => Pdfium.FPDF_ANNOT_SQUIGGLY,
            TextMarkupKind.StrikeOut => Pdfium.FPDF_ANNOT_STRIKEOUT,
            _ => Pdfium.FPDF_ANNOT_HIGHLIGHT,
        };
        var page = GetPage(pageIndex);
        var annot = Create(page, subtype);
        try
        {
            foreach (var r in rects)
            {
                var (x1, y1) = ToPdfSpace(page, r.X, r.Y);
                var (x2, y2) = ToPdfSpace(page, r.Right, r.Y);
                var (x3, y3) = ToPdfSpace(page, r.X, r.Bottom);
                var (x4, y4) = ToPdfSpace(page, r.Right, r.Bottom);
                var quad = new Pdfium.FS_QUADPOINTSF
                {
                    X1 = (float)x1, Y1 = (float)y1, X2 = (float)x2, Y2 = (float)y2,
                    X3 = (float)x3, Y3 = (float)y3, X4 = (float)x4, Y4 = (float)y4,
                };
                Pdfium.FPDFAnnot_AppendAttachmentPoints(annot, &quad);
            }
            SetRect(page, annot, Union(rects));
            var id = FinishAnnotation(annot, argb);
            if (contents is not null) SetString(annot, "Contents", contents);
            if (tag is not null) SetString(annot, KeyKind, tag);
            if (quote is not null) SetString(annot, KeyQuote, quote);
            if (contents is not null) SetString(annot, KeyRects, FormatRects(rects));
            return id;
        }
        finally
        {
            Pdfium.FPDFPage_CloseAnnot(annot);
        }
    }

    private string AddShape(int pageIndex, ShapeKind kind, PdfRect rect, uint stroke, double width, uint? fill)
    {
        var page = GetPage(pageIndex);
        var annot = Create(page, kind == ShapeKind.Ellipse ? Pdfium.FPDF_ANNOT_CIRCLE : Pdfium.FPDF_ANNOT_SQUARE);
        try
        {
            var pad = width / 2;
            SetRect(page, annot, new PdfRect(rect.X - pad, rect.Y - pad, rect.Width + 2 * pad, rect.Height + 2 * pad));
            Pdfium.FPDFAnnot_SetBorder(annot, 0, 0, (float)width);
            if (fill is { } f)
                Pdfium.FPDFAnnot_SetColor(annot, Pdfium.FPDFANNOT_COLORTYPE_InteriorColor, (f >> 16) & 0xFF, (f >> 8) & 0xFF, f & 0xFF, f >> 24);
            return FinishAnnotation(annot, stroke);
        }
        finally
        {
            Pdfium.FPDFPage_CloseAnnot(annot);
        }
    }

    private unsafe string AddStamp(int pageIndex, IReadOnlyList<StampPart> parts, string tag, string? contents)
    {
        var page = GetPage(pageIndex);
        var annot = Create(page, Pdfium.FPDF_ANNOT_STAMP);
        var fonts = new List<IntPtr>();
        try
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            var objects = new List<IntPtr>();
            foreach (var part in parts)
            {
                var obj = part switch
                {
                    StampPath p => CreatePath(page, p),
                    StampText t => CreateText(page, t, fonts),
                    _ => IntPtr.Zero,
                };
                if (obj == IntPtr.Zero)
                    continue;
                if (part is StampPath sp)
                {
                    // PDFium only computes a new path's bounds once it is transformed; use our own points.
                    foreach (var pt in sp.Points)
                    {
                        var (px, py) = ToPdfSpace(page, pt.X, pt.Y);
                        var grow = (float)sp.Width;
                        minX = Math.Min(minX, (float)px - grow); minY = Math.Min(minY, (float)py - grow);
                        maxX = Math.Max(maxX, (float)px + grow); maxY = Math.Max(maxY, (float)py + grow);
                    }
                }
                else
                {
                    float l, b, r, t2;
                    if (Pdfium.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t2) != 0)
                    {
                        minX = Math.Min(minX, l); minY = Math.Min(minY, b);
                        maxX = Math.Max(maxX, r); maxY = Math.Max(maxY, t2);
                    }
                }
                objects.Add(obj);
            }
            if (objects.Count == 0)
                throw new ArgumentException("Nothing to place.", nameof(parts));

            var rect = new Pdfium.FS_RECTF { Left = minX - 1, Bottom = minY - 1, Right = maxX + 1, Top = maxY + 1 };
            Pdfium.FPDFAnnot_SetRect(annot, &rect);
            foreach (var obj in objects)
                Pdfium.FPDFAnnot_AppendObject(annot, obj); // owned by the annotation from now on
            var id = FinishAnnotation(annot, 0xFF000000);
            SetString(annot, KeyKind, tag);
            if (contents is not null)
                SetString(annot, "Contents", contents);
            return id;
        }
        finally
        {
            Pdfium.FPDFPage_CloseAnnot(annot);
        }
    }

    private static IntPtr CreatePath(PageHandle page, StampPath p)
    {
        if (p.Points.Count == 0)
            return IntPtr.Zero;
        var (x0, y0) = ToPdfSpace(page, p.Points[0].X, p.Points[0].Y);
        var path = Pdfium.FPDFPageObj_CreateNewPath((float)x0, (float)y0);
        if (p.Points.Count == 1)
            Pdfium.FPDFPath_LineTo(path, (float)x0 + 0.01f, (float)y0); // a dot: zero-length segment with round caps
        for (var i = 1; i < p.Points.Count; i++)
        {
            var (x, y) = ToPdfSpace(page, p.Points[i].X, p.Points[i].Y);
            Pdfium.FPDFPath_LineTo(path, (float)x, (float)y);
        }
        if (p.Closed)
            Pdfium.FPDFPath_Close(path);
        Pdfium.FPDFPageObj_SetStrokeColor(path, (p.Stroke >> 16) & 0xFF, (p.Stroke >> 8) & 0xFF, p.Stroke & 0xFF, p.Stroke >> 24);
        Pdfium.FPDFPageObj_SetStrokeWidth(path, (float)p.Width);
        Pdfium.FPDFPageObj_SetLineCap(path, 1);  // round
        Pdfium.FPDFPageObj_SetLineJoin(path, 1); // round
        if (p.Fill is { } f)
            Pdfium.FPDFPageObj_SetFillColor(path, (f >> 16) & 0xFF, (f >> 8) & 0xFF, f & 0xFF, f >> 24);
        Pdfium.FPDFPath_SetDrawMode(path, p.Fill is null ? Pdfium.FPDF_FILLMODE_NONE : Pdfium.FPDF_FILLMODE_WINDING, p.Width > 0 ? 1 : 0);
        return path;
    }

    private unsafe IntPtr CreateText(PageHandle page, StampText t, List<IntPtr> fonts)
    {
        var obj = NewTextObject(t.Text, t.Size, t.Font, t.FontData, fonts);
        if (obj == IntPtr.Zero)
            return IntPtr.Zero;
        Pdfium.FPDFPageObj_SetFillColor(obj, (t.Color >> 16) & 0xFF, (t.Color >> 8) & 0xFF, t.Color & 0xFF, t.Color >> 24);
        var (x, y) = ToPdfSpace(page, t.Baseline.X, t.Baseline.Y);
        Pdfium.FPDFPageObj_Transform(obj, 1, 0, 0, 1, x, y);
        return obj;
    }

    private unsafe IntPtr NewTextObject(string text, double size, string font, byte[]? fontData, List<IntPtr> fonts)
    {
        IntPtr obj;
        if (fontData is { Length: > 0 })
        {
            IntPtr loaded;
            fixed (byte* d = fontData)
                loaded = Pdfium.FPDFText_LoadFont(_document, d, (uint)fontData.Length, Pdfium.FPDF_FONT_TRUETYPE, 1);
            if (loaded == IntPtr.Zero)
                return NewTextObject(text, size, "Helvetica-Oblique", null, fonts);
            fonts.Add(loaded);
            obj = Pdfium.FPDFPageObj_CreateTextObj(_document, loaded, (float)size);
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(font + '\0');
            fixed (byte* n = name)
                obj = Pdfium.FPDFPageObj_NewTextObj(_document, n, (float)size);
        }
        if (obj == IntPtr.Zero)
            return IntPtr.Zero;
        fixed (char* s = text + '\0')
            Pdfium.FPDFText_SetText(obj, (ushort*)s);
        return obj;
    }

    private unsafe double MeasureText(string text, double size, string font, byte[]? fontData)
    {
        ThrowIfDisposed();
        var fonts = new List<IntPtr>();
        var obj = NewTextObject(text, size, font, fontData, fonts);
        if (obj == IntPtr.Zero)
            return text.Length * size * 0.5;
        try
        {
            float l, b, r, t;
            return Pdfium.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) != 0 ? r - l : text.Length * size * 0.5;
        }
        finally
        {
            Pdfium.FPDFPageObj_Destroy(obj);
        }
    }

    private unsafe string AddCaretOrAttachment(int pageIndex, int subtype, PdfRect marker, string text, uint argb, string tag, string? fileName, byte[]? data)
    {
        var page = GetPage(pageIndex);
        var annot = Create(page, subtype);
        try
        {
            SetRect(page, annot, marker);
            var id = FinishAnnotation(annot, argb | 0xFF000000);
            SetString(annot, "Contents", text);
            SetString(annot, KeyKind, tag);
            SetString(annot, KeyColor, (argb & 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture));
            if (fileName is not null && data is not null)
            {
                IntPtr attachment;
                fixed (char* n = fileName + '\0')
                    attachment = Pdfium.FPDFAnnot_AddFileAttachment(annot, (ushort*)n);
                if (attachment == IntPtr.Zero)
                    throw new PdfException(PdfError.Unknown, "The file could not be attached.");
                fixed (byte* d = data)
                    Pdfium.FPDFAttachment_SetFile(attachment, _document, d, (uint)data.Length);
            }

            var (l, t) = ToPdfSpace(page, marker.X, marker.Y);
            var (r, b) = ToPdfSpace(page, marker.Right, marker.Bottom);
            var ap = MarkerAppearance.Build(subtype == Pdfium.FPDF_ANNOT_FILEATTACHMENT ? "clip" : "caret", argb, Math.Min(l, r), Math.Min(b, t), Math.Abs(r - l), Math.Abs(t - b));
            fixed (char* p = ap + '\0')
                Pdfium.FPDFAnnot_SetAP(annot, Pdfium.FPDF_ANNOT_APPEARANCEMODE_NORMAL, (ushort*)p);
            return id;
        }
        finally
        {
            Pdfium.FPDFPage_CloseAnnot(annot);
        }
    }

    private static IntPtr Create(PageHandle page, int subtype)
    {
        var annot = Pdfium.FPDFPage_CreateAnnot(page.Page, subtype);
        return annot != IntPtr.Zero ? annot : throw new PdfException(PdfError.Unknown, "The annotation could not be created.");
    }

    private static string FormatRects(IReadOnlyList<PdfRect> rects)
        => string.Join(";", rects.Select(r => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{r.X:0.##} {r.Y:0.##} {r.Width:0.##} {r.Height:0.##}")));
}

/// <summary>Appearance drawings for caret and attachment markers (page space, bottom-left origin).</summary>
internal static class MarkerAppearance
{
    public static string Build(string kind, uint argb, double x, double y, double w, double h)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double C(int shift) => ((argb >> shift) & 0xFF) / 255.0;
        var color = string.Format(inv, "{0:0.###} {1:0.###} {2:0.###}", C(16), C(8), C(0));
        if (kind == "caret")
            return string.Format(inv, "{0} rg {1:0.##} {2:0.##} m {3:0.##} {4:0.##} l {5:0.##} {2:0.##} l h f\n",
                color, x, y, x + w / 2, y + h, x + w);
        // Paper clip: two nested rounded loops.
        var sb = new StringBuilder();
        sb.Append(string.Format(inv, "{0} RG 1.3 w 1 J 1 j\n", color));
        void Loop(double lx, double by, double lw, double lh)
        {
            var r = lw / 2;
            sb.Append(string.Format(inv, "{0:0.##} {1:0.##} m {0:0.##} {2:0.##} l ", lx, by + r, by + lh - r));
            sb.Append(string.Format(inv, "{0:0.##} {1:0.##} {2:0.##} {1:0.##} {2:0.##} {3:0.##} c ", lx, by + lh + r * 0.33, lx + lw, by + lh - r));
            sb.Append(string.Format(inv, "{0:0.##} {1:0.##} l ", lx + lw, by + r));
            sb.Append(string.Format(inv, "{0:0.##} {1:0.##} {2:0.##} {1:0.##} {2:0.##} {3:0.##} c S\n", lx + lw, by - r * 0.33, lx, by + r));
        }
        Loop(x + w * 0.28, y + h * 0.08, w * 0.44, h * 0.84);
        Loop(x + w * 0.4, y + h * 0.25, w * 0.2, h * 0.5);
        return sb.ToString();
    }
}
