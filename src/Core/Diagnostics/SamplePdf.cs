using System.Text;

namespace PdfReader.Core.Diagnostics;

/// <summary>
/// Builds a realistic test document in memory (text lines, colored shapes, bookmarks, a web link),
/// so the self-diagnosis never needs the user's own files.
/// </summary>
public static class SamplePdf
{
    private static readonly string[] Lines =
    [
        "PDF.Reader renders every page with PDFium on a background thread.",
        "Scrolling stays smooth because only visible tiles are drawn.",
        "Dark, sepia and custom page colors never change the file itself.",
        "Links are never opened automatically; you are always asked first.",
        "The quick brown fox jumps over the lazy dog. 0123456789",
    ];

    /// <summary>Top-left y (page points) of the vertical middle of text line <paramref name="line"/>.</summary>
    public static double LineY(int line) => 92 + 15 * line - 4;

    public static byte[] Create(int pages = 60)
    {
        var objects = new List<string>();
        int Add(string body) { objects.Add(body); return objects.Count; }
        void Set(int id, string body) => objects[id - 1] = body;

        var catalog = Add("");
        var pagesRoot = Add("");
        var font = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var bold = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");
        var pageIds = new List<int>();

        for (var p = 1; p <= pages; p++)
        {
            var sb = new StringBuilder();
            sb.Append("0.18 0.43 0.92 rg 0 742 612 50 re f\n");
            sb.Append($"BT /F2 22 Tf 1 1 1 rg 40 758 Td (Chapter {p}) Tj ET\n");
            for (var i = 0; i < 10; i++)
                sb.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{0.2 + 0.07 * i:0.##} 0.6 {0.8 - 0.07 * i:0.##} rg {380 + i * 18} 560 18 120 re f\n"));
            sb.Append("0 0 0 rg BT /F1 11 Tf 40 700 Td 15 TL\n");
            for (var line = 0; line < 38; line++)
                sb.Append('(').Append($"{p}.{line + 1}  {Lines[(p + line) % Lines.Length]}".Replace("(", "\\(").Replace(")", "\\)")).Append(") Tj T*\n");
            sb.Append("ET\n");
            var stream = sb.ToString();
            var content = Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream");
            var annots = "";
            if (p == 1)
                annots = $" /Annots [{Add("<< /Type /Annot /Subtype /Link /Rect [40 40 300 60] /Border [0 0 0] /A << /S /URI /URI (https://example.com/) >> >>")} 0 R]";
            pageIds.Add(Add($"<< /Type /Page /Parent {pagesRoot} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R /F2 {bold} 0 R >> >> /Contents {content} 0 R{annots} >>"));
        }
        Set(pagesRoot, $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {pages} >>");

        var outlines = Add("");
        var items = Enumerable.Range(0, pages).Select(_ => Add("")).ToList();
        for (var i = 0; i < pages; i++)
        {
            var prev = i > 0 ? $" /Prev {items[i - 1]} 0 R" : "";
            var next = i < pages - 1 ? $" /Next {items[i + 1]} 0 R" : "";
            Set(items[i], $"<< /Title (Chapter {i + 1}) /Parent {outlines} 0 R /Dest [{pageIds[i]} 0 R /Fit]{prev}{next} >>");
        }
        Set(outlines, $"<< /Type /Outlines /First {items[0]} 0 R /Last {items[^1]} 0 R /Count {pages} >>");
        Set(catalog, $"<< /Type /Catalog /Pages {pagesRoot} 0 R /Outlines {outlines} 0 R >>");

        var output = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(output.ToString()));
            output.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(output.ToString());
        output.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
            output.Append($"{o:D10} 00000 n \n");
        output.Append($"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(output.ToString());
    }
}
