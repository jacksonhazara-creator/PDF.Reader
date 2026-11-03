using System.Runtime.InteropServices;

namespace PdfReader.Rendering;

public enum PdfError
{
    Unknown,
    FileNotFound,
    InvalidFormat,
    PasswordRequired,
    UnsupportedSecurity,
    PageNotFound,
    EngineUnavailable,
}

public sealed class PdfException(PdfError error, string message, Exception? inner = null) : Exception(message, inner)
{
    public PdfError Error { get; } = error;
}

/// <summary>
/// A rectangle in "page points": origin top-left of the page as displayed (page rotation
/// applied), 1 unit = 1/72 inch. All coordinates crossing the Rendering API use this space.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct PdfRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    public static PdfRect FromEdges(double left, double top, double right, double bottom)
        => new(Math.Min(left, right), Math.Min(top, bottom), Math.Abs(right - left), Math.Abs(bottom - top));
}

public readonly record struct PdfPageSize(double Width, double Height);

public sealed record OutlineItem(string Title, int PageIndex, IReadOnlyList<OutlineItem> Children);

public sealed record SearchHit(int PageIndex, int CharIndex, int CharCount, IReadOnlyList<PdfRect> Rects);

public sealed record TextSelection(int PageIndex, int StartChar, int EndChar, string Text, IReadOnlyList<PdfRect> Rects)
{
    public bool IsEmpty => Text.Length == 0;
}

public enum LinkKind { GoToPage, Uri, Launch, RemoteFile, Unsupported }

public sealed record PdfLink(LinkKind Kind, PdfRect Bounds, int PageIndex = -1, string? Target = null);

/// <summary>Active content found in a document. None of it is ever executed.</summary>
public sealed record SecurityReport(int JavaScriptCount, int AttachmentCount, int LaunchActionCount, int FileAttachmentAnnotations)
{
    public static readonly SecurityReport Empty = new(0, 0, 0, 0);
    public bool HasScripts => JavaScriptCount > 0;
    public bool HasAttachments => AttachmentCount + FileAttachmentAnnotations > 0;
    public bool HasLaunchActions => LaunchActionCount > 0;
    public bool HasActiveContent => HasScripts || HasAttachments || HasLaunchActions;
}

/// <summary>BGRA, premultiplied (always opaque), unmanaged memory. Dispose to free.</summary>
public sealed unsafe class PixelBuffer : IDisposable
{
    public PixelBuffer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Width = width;
        Height = height;
        Stride = width * 4;
        Data = (IntPtr)NativeMemory.Alloc((nuint)Stride * (nuint)height);
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public IntPtr Data { get; private set; }
    public long ByteCount => (long)Stride * Height;

    public Span<uint> Pixels => new((void*)Data, Width * Height);

    public void Dispose()
    {
        if (Data != IntPtr.Zero)
        {
            NativeMemory.Free((void*)Data);
            Data = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~PixelBuffer() => Dispose();
}

/// <summary>What to render: a tile of a page at a given scale (pixels per point).</summary>
public readonly record struct RenderRequest(int PageIndex, double Scale, int TileX, int TileY, int Width, int Height, PageColors Colors);
