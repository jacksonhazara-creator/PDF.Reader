using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PdfReader.Rendering.Native;

namespace PdfReader.Rendering;

public enum CommentShape { Square, Circle, Cloud, Bubble, Star }

/// <summary>A comment attached to a piece of text. Stored as a standard PDF text annotation
/// (other PDF apps show it as a note) with its own drawn appearance for the chosen shape.</summary>
public sealed record PdfComment(
    int PageIndex,
    string Id,
    string Text,
    string Quote,
    DateTime CreatedUtc,
    CommentShape Shape,
    uint Color,
    PdfRect Marker,
    IReadOnlyList<PdfRect> TextRects,
    string Kind = "Comment");

public readonly record struct PdfPoint(double X, double Y);

/// <summary>Creating, reading and removing annotations, and saving. All coordinates in page points (top-left origin).</summary>
public sealed partial class PdfDocument
{
    private const string KeyId = "NM";
    private const string KeyShape = "PDFR_Shape";
    private const string KeyQuote = "PDFR_Quote";
    private const string KeyRects = "PDFR_Rects";
    private const string KeyColor = "PDFR_Color";
    /// <summary>Exact 0xAARRGGBB; PDFium cannot read /C back once an appearance exists.</summary>
    private const string KeyArgb = "PDFR_ARGB";

    private int _modified;

    /// <summary>True when there are annotation changes that have not been saved.</summary>
    public bool IsModified => Volatile.Read(ref _modified) != 0;

    /// <summary>Highlights text. <paramref name="argb"/> alpha is the opacity.</summary>
    public Task<string> AddHighlightAsync(int pageIndex, IReadOnlyList<PdfRect> rects, uint argb)
        => _engine.RunAsync(() => AddHighlight(pageIndex, rects, argb), WorkPriority.Immediate);

    /// <summary>Freehand marker strokes. <paramref name="argb"/> alpha is the opacity.</summary>
    public Task<string> AddInkAsync(int pageIndex, IReadOnlyList<IReadOnlyList<PdfPoint>> strokes, uint argb, double width)
        => _engine.RunAsync(() => AddInk(pageIndex, strokes, argb, width), WorkPriority.Immediate);

    public Task<string> AddCommentAsync(int pageIndex, PdfRect marker, string text, string quote, IReadOnlyList<PdfRect> textRects,
        CommentShape shape, uint argb)
        => _engine.RunAsync(() => AddComment(pageIndex, marker, text, quote, textRects, shape, argb), WorkPriority.Immediate);

    public Task<IReadOnlyList<PdfComment>> GetCommentsAsync(int pageIndex, CancellationToken cancellationToken = default)
        => _engine.RunAsync<IReadOnlyList<PdfComment>>(() => ReadComments(pageIndex), WorkPriority.Interactive, cancellationToken);

    public Task<bool> RemoveAnnotationAsync(int pageIndex, string id)
        => _engine.RunAsync(() => RemoveAnnotation(pageIndex, id), WorkPriority.Immediate);

    /// <summary>
    /// Saves to <paramref name="path"/> (may be the file this document was opened from). Changes are
    /// appended as an incremental update, so the original content stays byte-for-byte intact. The
    /// file is written to a temporary file first and then swapped in, so a crash never corrupts it.
    /// </summary>
    /// <param name="transformSavedFile">Optional extra step run on the written temp file before it replaces the target (e.g. bookmark edits).</param>
    public async Task SaveAsync(string path, Action<string>? transformSavedFile = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await _engine.RunAsync(() => SaveCore(temp), WorkPriority.Immediate).ConfigureAwait(false);
            if (transformSavedFile is not null)
                await Task.Run(() => transformSavedFile(temp)).ConfigureAwait(false);
            var inPlace = FilePath is not null && string.Equals(Path.GetFullPath(FilePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            if (inPlace)
                await _engine.RunAsync(() => ReplaceOpenFile(temp, path), WorkPriority.Immediate).ConfigureAwait(false);
            else
                File.Move(temp, path, overwrite: true);
            Volatile.Write(ref _modified, 0);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    // ---------------------------------------------------------------------------------------

    private unsafe string AddHighlight(int pageIndex, IReadOnlyList<PdfRect> rects, uint argb)
    {
        if (rects.Count == 0)
            throw new ArgumentException("Nothing to highlight.", nameof(rects));
        var page = GetPage(pageIndex);
        var annot = Pdfium.FPDFPage_CreateAnnot(page.Page, Pdfium.FPDF_ANNOT_HIGHLIGHT);
        if (annot == IntPtr.Zero)
            throw new PdfException(PdfError.Unknown, "The highlight could not be created.");
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
            return FinishAnnotation(annot, argb);
        }
        finally
        {
            Pdfium.FPDFPage_CloseAnnot(annot);
        }
    }

    private unsafe string AddInk(int pageIndex, IReadOnlyList<IReadOnlyList<PdfPoint>> strokes, uint argb, double width)
    {
        var page = GetPage(pageIndex);
        var annot = Pdfium.FPDFPage_CreateAnnot(page.Page, Pdfium.FPDF_ANNOT_INK);
        if (annot == IntPtr.Zero)
            throw new PdfException(PdfError.Unknown, "The drawing could not be created.");
        try
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var stroke in strokes)
            {
                if (stroke.Count == 0)
                    continue;
                var points = new Pdfium.FS_POINTF[stroke.Count];
                for (var i = 0; i < stroke.Count; i++)
                {
                    var (px, py) = ToPdfSpace(page, stroke[i].X, stroke[i].Y);
                    points[i] = new Pdfium.FS_POINTF { X = (float)px, Y = (float)py };
                    minX = Math.Min(minX, stroke[i].X); maxX = Math.Max(maxX, stroke[i].X);
                    minY = Math.Min(minY, stroke[i].Y); maxY = Math.Max(maxY, stroke[i].Y);
                }
                fixed (Pdfium.FS_POINTF* p = points)
                    Pdfium.FPDFAnnot_AddInkStroke(annot, p, (nuint)points.Length);
            }
            var pad = width / 2 + 1;
            SetRect(page, annot, PdfRect.FromEdges(minX - pad, minY - pad, maxX + pad, maxY + pad));
            Pdfium.FPDFAnnot_SetBorder(annot, 0, 0, (float)width);
            return FinishAnnotation(annot, argb);
        }
        finally
        {
            Pdfium.FPDFPage_CloseAnnot(annot);
        }
    }

    private unsafe string AddComment(int pageIndex, PdfRect marker, string text, string quote, IReadOnlyList<PdfRect> textRects,
        CommentShape shape, uint argb)
    {
        var page = GetPage(pageIndex);
        var annot = Pdfium.FPDFPage_CreateAnnot(page.Page, Pdfium.FPDF_ANNOT_TEXT);
        if (annot == IntPtr.Zero)
            throw new PdfException(PdfError.Unknown, "The comment could not be created.");
        try
        {
            SetRect(page, annot, marker);
            var id = FinishAnnotation(annot, argb | 0xFF000000);
            SetString(annot, "Contents", text);
            SetString(annot, "Subj", "Comment");
            SetString(annot, KeyShape, shape.ToString());
            SetString(annot, KeyQuote, quote);
            SetString(annot, KeyColor, (argb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture));
            SetString(annot, KeyRects, string.Join(";", textRects.Select(r =>
                string.Create(CultureInfo.InvariantCulture, $"{r.X:0.##} {r.Y:0.##} {r.Width:0.##} {r.Height:0.##}"))));

            // Draw the shape ourselves so it looks the same here and in other PDF apps.
            var (l, t) = ToPdfSpace(page, marker.X, marker.Y);
            var (r2, b) = ToPdfSpace(page, marker.Right, marker.Bottom);
            var ap = CommentAppearance.Build(shape, argb, Math.Min(l, r2), Math.Min(b, t), Math.Abs(r2 - l), Math.Abs(t - b));
            fixed (char* p = ap + '\0')
                Pdfium.FPDFAnnot_SetAP(annot, Pdfium.FPDF_ANNOT_APPEARANCEMODE_NORMAL, (ushort*)p);
            return id;
        }
        finally
        {
            Pdfium.FPDFPage_CloseAnnot(annot);
        }
    }

    /// <summary>Common finishing: color/opacity, print flag, unique id and modification date.</summary>
    private string FinishAnnotation(IntPtr annot, uint argb)
    {
        Pdfium.FPDFAnnot_SetColor(annot, Pdfium.FPDFANNOT_COLORTYPE_Color, (argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF, argb >> 24);
        Pdfium.FPDFAnnot_SetFlags(annot, Pdfium.FPDF_ANNOT_FLAG_PRINT);
        var id = Guid.NewGuid().ToString("N");
        SetString(annot, KeyId, id);
        SetString(annot, KeyArgb, argb.ToString("X8", CultureInfo.InvariantCulture));
        SetString(annot, "M", ToPdfDate(DateTime.UtcNow));
        SetString(annot, "CreationDate", ToPdfDate(DateTime.UtcNow));
        Volatile.Write(ref _modified, 1);
        return id;
    }

    private unsafe List<PdfComment> ReadComments(int pageIndex)
    {
        var page = GetPage(pageIndex);
        var comments = new List<PdfComment>();
        var count = Pdfium.FPDFPage_GetAnnotCount(page.Page);
        for (var i = 0; i < count; i++)
        {
            var annot = Pdfium.FPDFPage_GetAnnot(page.Page, i);
            if (annot == IntPtr.Zero)
                continue;
            try
            {
                // Comments = sticky notes, inserts, attachments, and any markup that carries a note.
                var subtype = Pdfium.FPDFAnnot_GetSubtype(annot);
                var contents = GetString(annot, "Contents");
                var isComment = subtype is Pdfium.FPDF_ANNOT_TEXT or Pdfium.FPDF_ANNOT_CARET or Pdfium.FPDF_ANNOT_FILEATTACHMENT or Pdfium.FPDF_ANNOT_FREETEXT
                    || contents.Length > 0;
                if (!isComment)
                    continue;
                Pdfium.FS_RECTF rect;
                if (Pdfium.FPDFAnnot_GetRect(annot, &rect) == 0)
                    continue;
                var marker = FromPdfRect(page, rect.Left, rect.Top, rect.Right, rect.Bottom);
                var shape = Enum.TryParse<CommentShape>(GetString(annot, KeyShape), out var s) ? s : CommentShape.Square;
                var color = uint.TryParse(GetString(annot, KeyColor), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var c)
                    ? 0xFF000000 | c : 0xFFFFC83D;
                var id = GetString(annot, KeyId);
                var kind = GetString(annot, KeyKind) is { Length: > 0 } k ? k : subtype switch
                {
                    Pdfium.FPDF_ANNOT_CARET => "Insert",
                    Pdfium.FPDF_ANNOT_FILEATTACHMENT => "Attachment",
                    Pdfium.FPDF_ANNOT_FREETEXT => "TextComment",
                    Pdfium.FPDF_ANNOT_TEXT => "Comment",
                    _ => "Note",
                };
                comments.Add(new PdfComment(pageIndex, id.Length > 0 ? id : $"#{i}", contents, GetString(annot, KeyQuote),
                    ParsePdfDate(GetString(annot, "M")) ?? ParsePdfDate(GetString(annot, "CreationDate")) ?? DateTime.MinValue,
                    shape, color, marker, ParseRects(GetString(annot, KeyRects)), kind));
            }
            finally
            {
                Pdfium.FPDFPage_CloseAnnot(annot);
            }
        }
        return comments;
    }

    private bool RemoveAnnotation(int pageIndex, string id)
    {
        var page = GetPage(pageIndex);
        var count = Pdfium.FPDFPage_GetAnnotCount(page.Page);
        // Annotations without an id (made by other apps) are addressed as "#index".
        var byIndex = id.StartsWith('#') && int.TryParse(id.AsSpan(1), System.Globalization.NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) ? idx : -1;
        for (var i = 0; i < count; i++)
        {
            var annot = Pdfium.FPDFPage_GetAnnot(page.Page, i);
            if (annot == IntPtr.Zero)
                continue;
            var match = i == byIndex || GetString(annot, KeyId) == id;
            Pdfium.FPDFPage_CloseAnnot(annot);
            if (match && Pdfium.FPDFPage_RemoveAnnot(page.Page, i) != 0)
            {
                Volatile.Write(ref _modified, 1);
                return true;
            }
        }
        return false;
    }

    private unsafe void SaveCore(string tempPath)
    {
        ThrowIfDisposed();
        using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var handle = GCHandle.Alloc(output);
        var writer = (Pdfium.FileWrite*)NativeMemory.AllocZeroed((nuint)sizeof(Pdfium.FileWrite));
        try
        {
            writer->Version = 1;
            writer->WriteBlock = &WriteBlock;
            writer->StreamHandle = GCHandle.ToIntPtr(handle);
            if (Pdfium.FPDF_SaveAsCopy(_document, writer, Pdfium.FPDF_INCREMENTAL) == 0)
                throw new IOException("The document could not be saved.");
            output.Flush(flushToDisk: true);
        }
        finally
        {
            NativeMemory.Free(writer);
            handle.Free();
        }
    }

    /// <summary>
    /// Windows can't replace a file while we hold it open, so: close our handle, swap in the saved
    /// file, and reopen it. Runs on the engine thread so no render sees a half-closed document.
    /// The saved file contains every change, so nothing is lost by reopening.
    /// </summary>
    private void ReplaceOpenFile(string temp, string path)
    {
        var password = _password;
        ReleaseNative();
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            // Reopen whichever file is now at the path (the new one, or the untouched original).
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            OpenCore(password);
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe int WriteBlock(Pdfium.FileWrite* self, void* data, uint size)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr(self->StreamHandle).Target!;
            stream.Write(new ReadOnlySpan<byte>(data, checked((int)size)));
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    // ---------------------------------------------------------------------------------------

    private static unsafe void SetRect(PageHandle page, IntPtr annot, PdfRect r)
    {
        var (x1, y1) = ToPdfSpace(page, r.X, r.Y);
        var (x2, y2) = ToPdfSpace(page, r.Right, r.Bottom);
        var rect = new Pdfium.FS_RECTF
        {
            Left = (float)Math.Min(x1, x2), Right = (float)Math.Max(x1, x2),
            Top = (float)Math.Max(y1, y2), Bottom = (float)Math.Min(y1, y2),
        };
        Pdfium.FPDFAnnot_SetRect(annot, &rect);
    }

    private static PdfRect Union(IReadOnlyList<PdfRect> rects)
        => PdfRect.FromEdges(rects.Min(r => r.X), rects.Min(r => r.Y), rects.Max(r => r.Right), rects.Max(r => r.Bottom));

    private static unsafe void SetString(IntPtr annot, string key, string value)
    {
        var keyBytes = Encoding.ASCII.GetBytes(key + '\0');
        fixed (byte* k = keyBytes)
        fixed (char* v = value + '\0')
            Pdfium.FPDFAnnot_SetStringValue(annot, k, (ushort*)v);
    }

    private static unsafe string GetString(IntPtr annot, string key)
    {
        var keyBytes = Encoding.ASCII.GetBytes(key + '\0');
        fixed (byte* k = keyBytes)
        {
            var length = Pdfium.FPDFAnnot_GetStringValue(annot, k, null, 0);
            if (length <= 2 || length > 1 << 20)
                return string.Empty;
            var buffer = new char[length / 2];
            fixed (char* b = buffer)
                Pdfium.FPDFAnnot_GetStringValue(annot, k, (ushort*)b, length);
            return new string(buffer, 0, buffer.Length - 1);
        }
    }

    private static string ToPdfDate(DateTime utc) => "D:" + utc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z";

    internal static DateTime? ParsePdfDate(string value)
    {
        var s = value.StartsWith("D:", StringComparison.Ordinal) ? value[2..] : value;
        if (s.Length < 8)
            return null;
        var digits = new string(s.TakeWhile(char.IsDigit).Take(14).ToArray()).PadRight(14, '0');
        if (!DateTime.TryParseExact(digits, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;
        var rest = s[Math.Min(s.Length, s.TakeWhile(char.IsDigit).Count())..];
        if (rest.Length >= 3 && (rest[0] == '+' || rest[0] == '-')
            && int.TryParse(rest.AsSpan(1, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours))
        {
            var minutes = rest.Length >= 6 && int.TryParse(rest.AsSpan(4, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0;
            var offset = new TimeSpan(hours, minutes, 0);
            return DateTime.SpecifyKind(rest[0] == '+' ? local - offset : local + offset, DateTimeKind.Utc);
        }
        return DateTime.SpecifyKind(local, DateTimeKind.Utc);
    }

    private static PdfRect[] ParseRects(string value)
    {
        var rects = new List<PdfRect>();
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var n = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (n.Length == 4
                && double.TryParse(n[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(n[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                && double.TryParse(n[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                && double.TryParse(n[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
                rects.Add(new PdfRect(x, y, w, h));
        }
        return rects.ToArray();
    }
}

/// <summary>PDF content-stream drawing of comment markers (page space, origin bottom-left).</summary>
internal static class CommentAppearance
{
    public static string Build(CommentShape shape, uint argb, double x, double y, double w, double h)
    {
        var sb = new StringBuilder();
        double R(uint c, int shift) => ((c >> shift) & 0xFF) / 255.0;
        var dark = PageColors.Mix(argb, 0xFF000000, 0.35);
        sb.Append(F($"{R(argb, 16):0.###} {R(argb, 8):0.###} {R(argb, 0):0.###} rg "));
        sb.Append(F($"{R(dark, 16):0.###} {R(dark, 8):0.###} {R(dark, 0):0.###} RG 0.8 w\n"));
        var cx = x + w / 2;
        var cy = y + h / 2;
        var r = Math.Min(w, h) / 2 - 0.6;

        switch (shape)
        {
            case CommentShape.Circle:
                Circle(sb, cx, cy, r);
                sb.Append("B\n");
                break;
            case CommentShape.Cloud:
                Circle(sb, cx, cy - r * 0.08, r * 0.52);
                for (var i = 0; i < 7; i++)
                {
                    var a = Math.PI * 2 * i / 7 + 0.3;
                    Circle(sb, cx + Math.Cos(a) * r * 0.55, cy + Math.Sin(a) * r * 0.48, r * 0.38);
                }
                sb.Append("f\n");
                break;
            case CommentShape.Bubble:
                var top = y + h - 0.6;
                var bottom = y + h * 0.3;
                sb.Append(F($"{x + 0.6:0.##} {bottom:0.##} m {x + w - 0.6:0.##} {bottom:0.##} l {x + w - 0.6:0.##} {top:0.##} l {x + 0.6:0.##} {top:0.##} l h "));
                sb.Append(F($"{x + w * 0.22:0.##} {bottom:0.##} m {x + w * 0.12:0.##} {y + 0.6:0.##} l {x + w * 0.5:0.##} {bottom:0.##} l h B\n"));
                break;
            case CommentShape.Star:
                for (var i = 0; i < 10; i++)
                {
                    var a = Math.PI / 2 + Math.PI * i / 5;
                    var rr = i % 2 == 0 ? r : r * 0.45;
                    sb.Append(F($"{cx + Math.Cos(a) * rr:0.##} {cy + Math.Sin(a) * rr:0.##} {(i == 0 ? "m" : "l")} "));
                }
                sb.Append("h B\n");
                break;
            default: // Square note with two text lines
                sb.Append(F($"{x + 0.6:0.##} {y + 0.6:0.##} {w - 1.2:0.##} {h - 1.2:0.##} re B\n"));
                sb.Append(F($"1 1 1 RG 1 w {x + w * 0.25:0.##} {y + h * 0.62:0.##} m {x + w * 0.75:0.##} {y + h * 0.62:0.##} l S "));
                sb.Append(F($"{x + w * 0.25:0.##} {y + h * 0.38:0.##} m {x + w * 0.6:0.##} {y + h * 0.38:0.##} l S\n"));
                break;
        }
        return sb.ToString();
    }

    private static void Circle(StringBuilder sb, double cx, double cy, double r)
    {
        const double k = 0.5523;
        var o = r * k;
        sb.Append(F($"{cx + r:0.##} {cy:0.##} m "));
        sb.Append(F($"{cx + r:0.##} {cy + o:0.##} {cx + o:0.##} {cy + r:0.##} {cx:0.##} {cy + r:0.##} c "));
        sb.Append(F($"{cx - o:0.##} {cy + r:0.##} {cx - r:0.##} {cy + o:0.##} {cx - r:0.##} {cy:0.##} c "));
        sb.Append(F($"{cx - r:0.##} {cy - o:0.##} {cx - o:0.##} {cy - r:0.##} {cx:0.##} {cy - r:0.##} c "));
        sb.Append(F($"{cx + o:0.##} {cy - r:0.##} {cx + r:0.##} {cy - o:0.##} {cx + r:0.##} {cy:0.##} c h "));
    }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
