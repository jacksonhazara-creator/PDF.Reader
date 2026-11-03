namespace PdfReader.Core.Security;

/// <summary>Which link targets from a PDF may be opened at all (and then only after the user confirms).</summary>
public static class LinkPolicy
{
    private static readonly string[] AllowedSchemes = ["http", "https", "mailto"];

    public static bool IsAllowedUri(string? target, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(target) || target.Length > 4096)
            return false;
        if (!Uri.TryCreate(target.Trim(), UriKind.Absolute, out var parsed))
            return false;
        if (!AllowedSchemes.Contains(parsed.Scheme, StringComparer.OrdinalIgnoreCase))
            return false;
        uri = parsed;
        return true;
    }
}
