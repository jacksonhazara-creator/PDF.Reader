using PdfReader.Rendering;

namespace PdfReader.Core.Search;

public sealed record SearchOptions(bool MatchCase = false, bool WholeWord = false);

/// <summary>
/// Searches a document page by page, starting at the current page and wrapping around, so the
/// first results show up immediately while the rest stream in. Each page is a separate engine
/// work item, so rendering stays smooth during long searches.
/// </summary>
public static class DocumentSearch
{
    public static async IAsyncEnumerable<IReadOnlyList<SearchHit>> SearchAsync(
        PdfDocument document,
        string query,
        SearchOptions options,
        int startPage = 0,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || document.PageCount == 0)
            yield break;

        startPage = Math.Clamp(startPage, 0, document.PageCount - 1);
        for (var n = 0; n < document.PageCount; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = (startPage + n) % document.PageCount;
            IReadOnlyList<SearchHit> hits;
            try
            {
                hits = await document.SearchPageAsync(page, query, options.MatchCase, options.WholeWord, cancellationToken).ConfigureAwait(false);
            }
            catch (PdfException)
            {
                continue; // a broken page must not stop the search
            }
            if (hits.Count > 0)
                yield return hits;
        }
    }

    /// <summary>Orders hits in reading order (page, then character position).</summary>
    public static int Compare(SearchHit a, SearchHit b)
        => a.PageIndex != b.PageIndex ? a.PageIndex.CompareTo(b.PageIndex) : a.CharIndex.CompareTo(b.CharIndex);
}
