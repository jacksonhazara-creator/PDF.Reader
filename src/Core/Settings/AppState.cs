using PdfReader.Rendering;

namespace PdfReader.Core.Settings;

/// <summary>Things the app remembers on its own (state.json): recent files, last session, per-document view state.</summary>
public sealed class AppState
{
    public const int MaxRecentFiles = 20;
    public const int MaxDocumentStates = 200;

    public List<RecentFile> RecentFiles { get; set; } = [];
    public List<SessionTab> Session { get; set; } = [];
    public int SessionActiveIndex { get; set; }
    public Dictionary<string, DocumentViewState> Documents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool SidebarVisible { get; set; }

    /// <summary>Saved signature and initials (local only), reused every time you sign.</summary>
    public SignatureData? Signature { get; set; }
    public SignatureData? Initials { get; set; }

    public void AddRecent(string path, int pageCount)
    {
        RecentFiles.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, new RecentFile { Path = path, LastOpenedUtc = DateTime.UtcNow, PageCount = pageCount });
        if (RecentFiles.Count > MaxRecentFiles)
            RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }

    public void RemoveRecent(string path)
        => RecentFiles.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    public DocumentViewState GetDocumentState(string path)
    {
        if (!Documents.TryGetValue(path, out var state))
        {
            state = new DocumentViewState();
            Documents[path] = state;
        }
        return state;
    }

    /// <summary>Drops the oldest per-document entries so state.json stays small.</summary>
    public void Trim()
    {
        if (Documents.Count <= MaxDocumentStates)
            return;
        foreach (var key in Documents.OrderBy(d => d.Value.LastViewedUtc).Take(Documents.Count - MaxDocumentStates).Select(d => d.Key).ToList())
            Documents.Remove(key);
    }
}

public sealed class RecentFile
{
    public string Path { get; set; } = "";
    public DateTime LastOpenedUtc { get; set; }
    public int PageCount { get; set; }
}

public sealed class SessionTab
{
    public string Path { get; set; } = "";
}

public sealed class DocumentViewState
{
    public int PageIndex { get; set; }
    /// <summary>Vertical position inside the page, 0..1.</summary>
    public double PageOffset { get; set; }
    public ZoomMode ZoomMode { get; set; } = ZoomMode.FitWidth;
    public double Zoom { get; set; } = 1.0;
    /// <summary>Optional per-document override of the global page color mode.</summary>
    public PageColorMode? PageColorOverride { get; set; }
    public DateTime LastViewedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A drawn (normalized strokes) or typed signature.</summary>
public sealed class SignatureData
{
    /// <summary>Strokes as [x0, y0, x1, y1, ...] in 0..1 of the signature box.</summary>
    public List<double[]> Strokes { get; set; } = [];
    public double Aspect { get; set; } = 3;
    /// <summary>Set for a typed signature instead of strokes.</summary>
    public string? Typed { get; set; }
}
