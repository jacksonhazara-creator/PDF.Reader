using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfReader.Core.Bookmarks;

/// <summary>A pending bookmark change, applied when the document is saved.</summary>
public abstract record OutlineEdit
{
    /// <summary>Removes the bookmark at a path of child indices (e.g. [2] or [0, 3]).</summary>
    public sealed record Remove(IReadOnlyList<int> Path) : OutlineEdit;

    /// <summary>Adds a top-level bookmark pointing at a page.</summary>
    public sealed record Add(string Title, int PageIndex) : OutlineEdit;
}

/// <summary>
/// Rewrites a PDF's bookmarks (PDFium can read them but not edit them). Works on a saved file;
/// PDFsharp 6.2 is MIT licensed (6.5+ is AGPL and must not be used).
/// </summary>
public static class OutlineEditor
{
    public static void Apply(string path, IReadOnlyList<OutlineEdit> edits, string? password = null)
    {
        if (edits.Count == 0)
            return;
        var output = path + ".outline";
        using (var document = password is null
                   ? global::PdfSharp.Pdf.IO.PdfReader.Open(path, PdfDocumentOpenMode.Modify)
                   : global::PdfSharp.Pdf.IO.PdfReader.Open(path, password, PdfDocumentOpenMode.Modify))
        {
            // Remove deepest/last first so earlier paths stay valid.
            foreach (var remove in edits.OfType<OutlineEdit.Remove>()
                         .OrderByDescending(r => r.Path.Count)
                         .ThenByDescending(r => string.Join(".", r.Path.Select(i => i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)))))
            {
                var collection = document.Outlines;
                for (var depth = 0; depth < remove.Path.Count - 1 && collection is not null; depth++)
                    collection = remove.Path[depth] < collection.Count ? collection[remove.Path[depth]].Outlines : null;
                var index = remove.Path[^1];
                if (collection is not null && index < collection.Count)
                    collection.RemoveAt(index);
            }

            foreach (var add in edits.OfType<OutlineEdit.Add>())
                if (add.PageIndex >= 0 && add.PageIndex < document.PageCount)
                    document.Outlines.Add(add.Title, document.Pages[add.PageIndex], true);

            document.Save(output);
        }
        File.Move(output, path, overwrite: true);
    }
}
