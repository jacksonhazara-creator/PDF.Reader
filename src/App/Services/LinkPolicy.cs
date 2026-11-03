using System.Diagnostics;

namespace PdfReader.App.Services;

/// <summary>Opens links the user confirmed. The allow-list lives in <see cref="Core.Security.LinkPolicy"/>.</summary>
public static class LinkPolicy
{
    public static bool IsAllowedUri(string? target, out Uri? uri) => Core.Security.LinkPolicy.IsAllowedUri(target, out uri);

    public static void Open(Uri uri)
    {
        // Pass the normalized absolute URI, never the raw PDF string, to the shell.
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
