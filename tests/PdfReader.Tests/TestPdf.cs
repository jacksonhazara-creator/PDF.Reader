using System.Text;

namespace PdfReader.Tests;

/// <summary>
/// Builds small, valid PDFs in memory so the tests need no binary fixtures in the repo.
/// Uses the standard Helvetica font, so text is extractable and searchable.
/// </summary>
internal static class TestPdf
{
    public sealed record Options
    {
        public string[] Pages { get; init; } = ["Hello PDF"];
        public bool Outline { get; init; }
        public bool JavaScript { get; init; }
        public bool Attachment { get; init; }
        public bool LaunchLink { get; init; }
        public bool UriLink { get; init; }
        /// <summary>Draws a filled black square at (100,100)-(200,200) in page space (top-left origin) on page 1.</summary>
        public bool BlackSquare { get; init; }
        public int Rotate { get; init; }
    }

    public static byte[] Create(params string[] pages) => Create(new Options { Pages = pages });

    public static byte[] Create(Options o)
    {
        var objects = new List<string>();
        int Add(string body) { objects.Add(body); return objects.Count; }
        void Set(int id, string body) => objects[id - 1] = body;

        var catalog = Add("");
        var pagesRoot = Add("");
        var font = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var pageIds = new List<int>();
        for (var i = 0; i < o.Pages.Length; i++)
        {
            var content = new StringBuilder();
            content.Append("BT /F1 24 Tf 72 700 Td (").Append(Escape(o.Pages[i])).Append(") Tj ET\n");
            if (i == 0 && o.BlackSquare)
                content.Append("0 0 0 rg 100 592 100 100 re f\n"); // page height 792: y=792-200
            var stream = content.ToString();
            var contentId = Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream");

            var annots = "";
            if (i == 0 && (o.LaunchLink || o.UriLink))
            {
                var action = o.LaunchLink
                    ? "<< /S /Launch /F (calc.exe) >>"
                    : "<< /S /URI /URI (https://example.com/) >>";
                var link = Add($"<< /Type /Annot /Subtype /Link /Rect [72 690 300 730] /Border [0 0 0] /A {action} >>");
                annots = $" /Annots [{link} 0 R]";
            }

            var rotate = o.Rotate != 0 ? $" /Rotate {o.Rotate}" : "";
            pageIds.Add(Add($"<< /Type /Page /Parent {pagesRoot} 0 R /MediaBox [0 0 612 792]{rotate} /Resources << /Font << /F1 {font} 0 R >> >> /Contents {contentId} 0 R{annots} >>"));
        }

        Set(pagesRoot, $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(p => $"{p} 0 R"))}] /Count {pageIds.Count} >>");

        var extra = new StringBuilder();
        if (o.Outline)
        {
            var outlines = Add("");
            var items = new List<int>();
            for (var i = 0; i < pageIds.Count; i++)
                items.Add(Add(""));
            for (var i = 0; i < items.Count; i++)
            {
                var prev = i > 0 ? $" /Prev {items[i - 1]} 0 R" : "";
                var next = i < items.Count - 1 ? $" /Next {items[i + 1]} 0 R" : "";
                Set(items[i], $"<< /Title (Chapter {i + 1}) /Parent {outlines} 0 R /Dest [{pageIds[i]} 0 R /Fit]{prev}{next} >>");
            }
            Set(outlines, $"<< /Type /Outlines /First {items[0]} 0 R /Last {items[^1]} 0 R /Count {items.Count} >>");
            extra.Append($" /Outlines {outlines} 0 R");
        }

        var names = new StringBuilder();
        if (o.JavaScript)
        {
            var js = Add("<< /S /JavaScript /JS (app.alert\\('should never run'\\);) >>");
            names.Append($" /JavaScript << /Names [(hello) {js} 0 R] >>");
        }
        if (o.Attachment)
        {
            const string data = "attachment payload";
            var file = Add($"<< /Type /EmbeddedFile /Length {data.Length} >>\nstream\n{data}\nendstream");
            var spec = Add($"<< /Type /Filespec /F (payload.txt) /EF << /F {file} 0 R >> >>");
            names.Append($" /EmbeddedFiles << /Names [(payload.txt) {spec} 0 R] >>");
        }
        if (names.Length > 0)
            extra.Append($" /Names <<{names} >>");

        Set(catalog, $"<< /Type /Catalog /Pages {pagesRoot} 0 R{extra} >>");
        return Serialize(objects);
    }

    private static byte[] Serialize(List<string> objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets)
            sb.Append($"{off:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
