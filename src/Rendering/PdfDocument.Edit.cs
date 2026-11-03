using System.Globalization;
using PdfReader.Rendering.Native;

namespace PdfReader.Rendering;

public enum AnnotationKind { Highlight, Underline, Squiggly, StrikeOut, Ink, Square, Circle, Stamp, Comment, Attachment, FreeText, Other }

/// <summary>An annotation under the pointer (for the right-click menu).</summary>
public sealed record PdfAnnotationHit(int PageIndex, string Id, AnnotationKind Kind, PdfRect Bounds, uint Color, string Contents, string Tag)
{
    /// <summary>Annotations whose main point is a note: they get "Edit comment".</summary>
    public bool IsNote => Kind is AnnotationKind.Comment or AnnotationKind.FreeText || Contents.Length > 0 && Kind != AnnotationKind.Attachment;
}

/// <summary>Finding, recoloring, re-texting and deleting annotations that already exist.</summary>
public sealed partial class PdfDocument
{
    /// <summary>The topmost user annotation at a point (page points). Links and form fields are ignored.</summary>
    public Task<PdfAnnotationHit?> GetAnnotationAtAsync(int pageIndex, double x, double y, double tolerance = 3)
        => _engine.RunAsync(() => HitTest(pageIndex, x, y, tolerance), WorkPriority.Interactive);

    /// <summary>Changes an annotation's color (keeps its transparency) and redraws it.</summary>
    public Task<bool> SetAnnotationColorAsync(int pageIndex, string id, uint rgb)
        => _engine.RunAsync(() => WithAnnotation(pageIndex, id, (page, annot) => Recolor(page, annot, rgb & 0xFFFFFF)), WorkPriority.Immediate);

    public Task<bool> SetAnnotationTextAsync(int pageIndex, string id, string text)
        => _engine.RunAsync(() => WithAnnotation(pageIndex, id, (_, annot) =>
        {
            SetString(annot, "Contents", text);
            SetString(annot, "M", ToPdfDate(DateTime.UtcNow));
            return true;
        }), WorkPriority.Immediate);

    private unsafe PdfAnnotationHit? HitTest(int pageIndex, double x, double y, double tolerance)
    {
        var page = GetPage(pageIndex);
        var count = Pdfium.FPDFPage_GetAnnotCount(page.Page);
        for (var i = count - 1; i >= 0; i--) // topmost first
        {
            var annot = Pdfium.FPDFPage_GetAnnot(page.Page, i);
            if (annot == IntPtr.Zero)
                continue;
            try
            {
                var subtype = Pdfium.FPDFAnnot_GetSubtype(annot);
                if (subtype is Pdfium.FPDF_ANNOT_LINK or Pdfium.FPDF_ANNOT_WIDGET or Pdfium.FPDF_ANNOT_POPUP)
                    continue;
                Pdfium.FS_RECTF rect;
                if (Pdfium.FPDFAnnot_GetRect(annot, &rect) == 0)
                    continue;
                var bounds = FromPdfRect(page, rect.Left, rect.Top, rect.Right, rect.Bottom);
                if (!new PdfRect(bounds.X - tolerance, bounds.Y - tolerance, bounds.Width + 2 * tolerance, bounds.Height + 2 * tolerance).Contains(x, y))
                    continue;

                // Text markup: only the marked words count, not the whole bounding box.
                var quads = (int)Pdfium.FPDFAnnot_CountAttachmentPoints(annot);
                if (quads > 0 && subtype is Pdfium.FPDF_ANNOT_HIGHLIGHT or Pdfium.FPDF_ANNOT_UNDERLINE or Pdfium.FPDF_ANNOT_SQUIGGLY or Pdfium.FPDF_ANNOT_STRIKEOUT)
                {
                    var inside = false;
                    for (var q = 0; q < quads && !inside; q++)
                    {
                        Pdfium.FS_QUADPOINTSF quad;
                        if (Pdfium.FPDFAnnot_GetAttachmentPoints(annot, (nuint)q, &quad) == 0)
                            continue;
                        var r = PdfRect.FromEdges(
                            Math.Min(Math.Min(quad.X1, quad.X2), Math.Min(quad.X3, quad.X4)), Math.Max(Math.Max(quad.Y1, quad.Y2), Math.Max(quad.Y3, quad.Y4)),
                            Math.Max(Math.Max(quad.X1, quad.X2), Math.Max(quad.X3, quad.X4)), Math.Min(Math.Min(quad.Y1, quad.Y2), Math.Min(quad.Y3, quad.Y4)));
                        var qr = FromPdfRect(page, r.X, r.Y, r.Right, r.Bottom);
                        inside = new PdfRect(qr.X - tolerance, qr.Y - tolerance, qr.Width + 2 * tolerance, qr.Height + 2 * tolerance).Contains(x, y);
                    }
                    if (!inside)
                        continue;
                }

                uint cr, cg, cb, ca;
                var color = uint.TryParse(GetString(annot, KeyArgb), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var stored) ? stored
                    : Pdfium.FPDFAnnot_GetColor(annot, Pdfium.FPDFANNOT_COLORTYPE_Color, &cr, &cg, &cb, &ca) != 0 ? (ca << 24) | (cr << 16) | (cg << 8) | cb
                    : 0xFF000000;
                var tag = GetString(annot, KeyKind);
                var kind = subtype switch
                {
                    Pdfium.FPDF_ANNOT_HIGHLIGHT => AnnotationKind.Highlight,
                    Pdfium.FPDF_ANNOT_UNDERLINE => AnnotationKind.Underline,
                    Pdfium.FPDF_ANNOT_SQUIGGLY => AnnotationKind.Squiggly,
                    Pdfium.FPDF_ANNOT_STRIKEOUT => AnnotationKind.StrikeOut,
                    Pdfium.FPDF_ANNOT_INK => AnnotationKind.Ink,
                    Pdfium.FPDF_ANNOT_SQUARE => AnnotationKind.Square,
                    Pdfium.FPDF_ANNOT_CIRCLE => AnnotationKind.Circle,
                    Pdfium.FPDF_ANNOT_STAMP => AnnotationKind.Stamp,
                    Pdfium.FPDF_ANNOT_TEXT => AnnotationKind.Comment,
                    Pdfium.FPDF_ANNOT_FILEATTACHMENT => AnnotationKind.Attachment,
                    Pdfium.FPDF_ANNOT_FREETEXT => AnnotationKind.FreeText,
                    _ => AnnotationKind.Other,
                };
                var id = GetString(annot, KeyId);
                return new PdfAnnotationHit(pageIndex, id.Length > 0 ? id : $"#{i}", kind, bounds, color, GetString(annot, "Contents"), tag);
            }
            finally
            {
                Pdfium.FPDFPage_CloseAnnot(annot);
            }
        }
        return null;
    }

    /// <summary>Runs an action on the annotation with this id (NM, or "#index" for annotations without one).</summary>
    private bool WithAnnotation(int pageIndex, string id, Func<PageHandle, IntPtr, bool> action)
    {
        var page = GetPage(pageIndex);
        var count = Pdfium.FPDFPage_GetAnnotCount(page.Page);
        var byIndex = id.StartsWith('#') && int.TryParse(id.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : -1;
        for (var i = 0; i < count; i++)
        {
            var annot = Pdfium.FPDFPage_GetAnnot(page.Page, i);
            if (annot == IntPtr.Zero)
                continue;
            try
            {
                if (i != byIndex && GetString(annot, KeyId) != id)
                    continue;
                var changed = action(page, annot);
                if (changed)
                    Volatile.Write(ref _modified, 1);
                return changed;
            }
            finally
            {
                Pdfium.FPDFPage_CloseAnnot(annot);
            }
        }
        return false;
    }

    private unsafe bool Recolor(PageHandle page, IntPtr annot, uint rgb)
    {
        uint r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
        uint cr, cg, cb, ca = 255;
        if (uint.TryParse(GetString(annot, KeyArgb), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var stored))
            ca = stored >> 24;
        else if (Pdfium.FPDFAnnot_GetColor(annot, Pdfium.FPDFANNOT_COLORTYPE_Color, &cr, &cg, &cb, &ca) == 0)
            ca = 255;
        SetString(annot, KeyArgb, ((ca << 24) | rgb).ToString("X8", CultureInfo.InvariantCulture));
        var subtype = Pdfium.FPDFAnnot_GetSubtype(annot);
        // PDFium refuses to change /C while an appearance stream exists, so drop it first (all
        // non-stamp kinds get a fresh appearance below or regenerated by PDFium).
        if (subtype != Pdfium.FPDF_ANNOT_STAMP)
            Pdfium.FPDFAnnot_SetAP(annot, Pdfium.FPDF_ANNOT_APPEARANCEMODE_NORMAL, null);
        Pdfium.FPDFAnnot_SetColor(annot, Pdfium.FPDFANNOT_COLORTYPE_Color, r, g, b, ca);
        SetString(annot, "M", ToPdfDate(DateTime.UtcNow));
        switch (subtype)
        {
            case Pdfium.FPDF_ANNOT_STAMP:
                // Drawn from page objects: recolor each, keeping transparency and non-matching fills (e.g. a box background).
                var n = Pdfium.FPDFAnnot_GetObjectCount(annot);
                for (var i = 0; i < n; i++)
                {
                    var obj = Pdfium.FPDFAnnot_GetObject(annot, i);
                    if (obj == IntPtr.Zero)
                        continue;
                    uint sr, sg, sb, sa, fr, fg, fb, fa;
                    var hasStroke = Pdfium.FPDFPageObj_GetStrokeColor(obj, &sr, &sg, &sb, &sa) != 0;
                    var hasFill = Pdfium.FPDFPageObj_GetFillColor(obj, &fr, &fg, &fb, &fa) != 0;
                    if (Pdfium.FPDFPageObj_GetType(obj) == Pdfium.FPDF_PAGEOBJ_TEXT)
                    {
                        if (hasFill) Pdfium.FPDFPageObj_SetFillColor(obj, r, g, b, fa);
                    }
                    else
                    {
                        if (hasStroke) Pdfium.FPDFPageObj_SetStrokeColor(obj, r, g, b, sa);
                        if (hasFill && hasStroke && fr == sr && fg == sg && fb == sb) Pdfium.FPDFPageObj_SetFillColor(obj, r, g, b, fa);
                        else if (hasFill && fr == fg && fg == fb && fr < 60) { } // keep dark fills
                        else if (hasFill && (fa & 0xFF) < 0x40) Pdfium.FPDFPageObj_SetFillColor(obj, r, g, b, fa); // tinted stamp background
                    }
                    Pdfium.FPDFAnnot_UpdateObject(annot, obj);
                }
                return true;

            case Pdfium.FPDF_ANNOT_TEXT or Pdfium.FPDF_ANNOT_FILEATTACHMENT:
                // Our own drawn markers: redraw in the new color.
                SetString(annot, KeyColor, rgb.ToString("X6", CultureInfo.InvariantCulture));
                Pdfium.FS_RECTF rect;
                if (Pdfium.FPDFAnnot_GetRect(annot, &rect) == 0)
                    return true;
                var argb = 0xFF000000 | rgb;
                string ap;
                if (subtype == Pdfium.FPDF_ANNOT_FILEATTACHMENT)
                    ap = MarkerAppearance.Build("clip", argb, rect.Left, rect.Bottom, rect.Right - rect.Left, rect.Top - rect.Bottom);
                else if (GetString(annot, KeyKind) == "Insert")
                    ap = MarkerAppearance.Build("caret", argb, rect.Left, rect.Bottom, rect.Right - rect.Left, rect.Top - rect.Bottom);
                else if (Enum.TryParse<CommentShape>(GetString(annot, KeyShape), out var shape))
                    ap = CommentAppearance.Build(shape, argb, rect.Left, rect.Bottom, rect.Right - rect.Left, rect.Top - rect.Bottom);
                else
                    return true; // a foreign sticky note: PDFium draws it from /C
                fixed (char* p = ap + '\0')
                    Pdfium.FPDFAnnot_SetAP(annot, Pdfium.FPDF_ANNOT_APPEARANCEMODE_NORMAL, (ushort*)p);
                return true;

            default:
                // Highlight, underline, strikeout, ink, square, circle: drop the old appearance so PDFium regenerates it
                // from /C. PDFium marks appearances it generated and won't redo them, so reset that marker too
                // (a non-boolean value reads as false).
                SetString(annot, "PDFIUM_HasGeneratedAP", "");
                return true;
        }
    }
}
