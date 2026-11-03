namespace PdfReader.Core.Commands;

/// <summary>Scoring for the command palette: prefix &gt; word-start &gt; substring &gt; in-order letters.</summary>
public static class CommandMatcher
{
    /// <returns>Higher is better; 0 means no match.</returns>
    public static int Score(string candidate, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 1;
        query = query.Trim();

        if (candidate.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
            return 1000 - candidate.Length;

        var index = candidate.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (index > 0)
        {
            var atWordStart = !char.IsLetterOrDigit(candidate[index - 1]);
            return (atWordStart ? 800 : 600) - index;
        }

        // Fuzzy: all query letters appear in order ("pgc" -> "Page colors").
        var ci = 0;
        var gaps = 0;
        foreach (var qc in query)
        {
            if (char.IsWhiteSpace(qc))
                continue;
            var found = false;
            while (ci < candidate.Length)
            {
                if (char.ToUpperInvariant(candidate[ci++]) == char.ToUpperInvariant(qc))
                {
                    found = true;
                    break;
                }
                gaps++;
            }
            if (!found)
                return 0;
        }
        return Math.Max(1, 300 - gaps);
    }
}
