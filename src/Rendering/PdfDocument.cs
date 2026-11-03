using System.Runtime.InteropServices;
using System.Text;
using PdfReader.Rendering.Native;

namespace PdfReader.Rendering;

/// <summary>
/// An open PDF. All PDFium access is marshalled onto <see cref="PdfEngine"/>'s thread; every
/// public method is safe to call from any thread. Page geometry is loaded up front (cheap), so
/// the viewer can lay out the whole document before a single pixel is rendered.
/// </summary>
public sealed partial class PdfDocument : IAsyncDisposable
{
    private const int PageCacheCapacity = 8;
    // FPDF_PageToDevice works in integers; scale up for sub-point precision.
    private const int CoordPrecision = 16;
    private const int MaxOutlineItems = 20_000;
    private const int MaxOutlineDepth = 32;

    private readonly PdfEngine _engine;
    private readonly Stream _stream;
    private readonly Dictionary<int, PageHandle> _pages = new();
    private readonly LinkedList<int> _pageLru = new();
    private IntPtr _document;
    private unsafe Pdfium.FPDF_FILEACCESS* _fileAccess;
    private GCHandle _streamHandle;
    private int _disposed;

    private PdfDocument(PdfEngine engine, Stream stream, string? filePath)
    {
        _engine = engine;
        _stream = stream;
        FilePath = filePath;
    }

    public string? FilePath { get; }
    public int PageCount { get; private set; }
    public IReadOnlyList<PdfPageSize> PageSizes { get; private set; } = [];
    public string? Title { get; private set; }

    /// <summary>Document-level active content, known right after opening. Page-level launch
    /// actions are found later by <see cref="ScanActiveContentAsync"/>.</summary>
    public SecurityReport Security { get; private set; } = SecurityReport.Empty;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    // ---------------------------------------------------------------------------------------
    // Opening
    // ---------------------------------------------------------------------------------------

    public static Task<PdfDocument> OpenAsync(string path, string? password = null, CancellationToken cancellationToken = default)
    {
        FileStream stream;
        try
        {
            // Share everything so other apps can still edit/replace the file while it is open.
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096, FileOptions.RandomAccess);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromException<PdfDocument>(new PdfException(PdfError.FileNotFound, $"File not found: {path}", ex));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromException<PdfDocument>(new PdfException(PdfError.FileNotFound, ex.Message, ex));
        }

        return OpenAsync(stream, path, password, cancellationToken);
    }

    /// <summary>Opens a PDF from a seekable stream. The document takes ownership of the stream.</summary>
    public static async Task<PdfDocument> OpenAsync(Stream stream, string? filePath = null, string? password = null,
        CancellationToken cancellationToken = default)
    {
        if (!stream.CanSeek || !stream.CanRead)
            throw new ArgumentException("Stream must be readable and seekable.", nameof(stream));

        var engine = PdfEngine.Shared;
        var document = new PdfDocument(engine, stream, filePath);
        try
        {
            await engine.RunAsync(() => document.OpenCore(password), WorkPriority.Immediate, cancellationToken).ConfigureAwait(false);
            return document;
        }
        catch
        {
            await document.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private unsafe void OpenCore(string? password)
    {
        if (_stream.Length is 0 or > uint.MaxValue)
            throw new PdfException(PdfError.InvalidFormat, _stream.Length == 0 ? "The file is empty." : "Files larger than 4 GB are not supported.");

        _streamHandle = GCHandle.Alloc(_stream);
        _fileAccess = (Pdfium.FPDF_FILEACCESS*)NativeMemory.AllocZeroed((nuint)sizeof(Pdfium.FPDF_FILEACCESS));
        _fileAccess->FileLen = (uint)_stream.Length;
        _fileAccess->GetBlock = &GetBlock;
        _fileAccess->Param = GCHandle.ToIntPtr(_streamHandle);

        var passwordBytes = password is null ? null : Encoding.UTF8.GetBytes(password + '\0');
        fixed (byte* pw = passwordBytes)
            _document = Pdfium.FPDF_LoadCustomDocument(_fileAccess, pw);

        if (_document == IntPtr.Zero)
        {
            var error = Pdfium.FPDF_GetLastError();
            throw error switch
            {
                Pdfium.FPDF_ERR_PASSWORD => new PdfException(PdfError.PasswordRequired,
                    password is null ? "This document is password protected." : "The password is incorrect."),
                Pdfium.FPDF_ERR_SECURITY => new PdfException(PdfError.UnsupportedSecurity, "This document uses an unsupported security handler."),
                Pdfium.FPDF_ERR_FILE => new PdfException(PdfError.FileNotFound, "The file could not be read."),
                _ => new PdfException(PdfError.InvalidFormat, "This file is damaged or is not a PDF document."),
            };
        }

        var count = Math.Max(0, Pdfium.FPDF_GetPageCount(_document));
        var sizes = new PdfPageSize[count];
        for (var i = 0; i < count; i++)
        {
            Pdfium.FS_SIZEF size;
            sizes[i] = Pdfium.FPDF_GetPageSizeByIndexF(_document, i, &size) != 0 && size.Width > 0 && size.Height > 0
                ? new PdfPageSize(size.Width, size.Height)
                : new PdfPageSize(612, 792); // US Letter fallback for broken page dictionaries
        }

        PageCount = count;
        PageSizes = sizes;
        Title = GetMetaText("Title");
        Security = new SecurityReport(
            Math.Max(0, Pdfium.FPDFDoc_GetJavaScriptActionCount(_document)),
            Math.Max(0, Pdfium.FPDFDoc_GetAttachmentCount(_document)),
            0, 0);
    }

    [UnmanagedCallersOnly]
    private static unsafe int GetBlock(IntPtr param, uint position, byte* buffer, uint size)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr(param).Target!;
            if (position + (long)size > stream.Length)
                return 0;
            stream.Position = position;
            stream.ReadExactly(new Span<byte>(buffer, checked((int)size)));
            return 1;
        }
        catch
        {
            return 0; // PDFium treats this as a read error; never let exceptions cross into native code.
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe int NeedToPauseNow(Pdfium.IFSDK_PAUSE* pause) => PdfEngine.CurrentToken.IsCancellationRequested ? 1 : 0;

    // ---------------------------------------------------------------------------------------
    // Rendering
    // ---------------------------------------------------------------------------------------

    /// <summary>Pixel size of a whole page at <paramref name="scale"/> pixels per point.</summary>
    public (int Width, int Height) GetPixelSize(int pageIndex, double scale)
    {
        var s = PageSizes[pageIndex];
        return (Math.Max(1, (int)Math.Round(s.Width * scale)), Math.Max(1, (int)Math.Round(s.Height * scale)));
    }

    public Task<PixelBuffer> RenderAsync(RenderRequest request, WorkPriority priority = WorkPriority.Visible, CancellationToken cancellationToken = default)
        => _engine.RunAsync(() => RenderCore(request), priority, cancellationToken);

    /// <summary>Renders a whole page (used for previews and thumbnails).</summary>
    public Task<PixelBuffer> RenderPageAsync(int pageIndex, double scale, PageColors colors, WorkPriority priority = WorkPriority.Background,
        CancellationToken cancellationToken = default)
    {
        var (w, h) = GetPixelSize(pageIndex, scale);
        return RenderAsync(new RenderRequest(pageIndex, scale, 0, 0, w, h, colors), priority, cancellationToken);
    }

    private unsafe PixelBuffer RenderCore(RenderRequest r)
    {
        var token = PdfEngine.CurrentToken;
        var page = GetPage(r.PageIndex);
        var (fullW, fullH) = GetPixelSize(r.PageIndex, r.Scale);
        var colors = r.Colors;
        var useColorScheme = !colors.IsNormal && colors.KeepImagesNatural;
        var useLuminanceMap = !colors.IsNormal && !colors.KeepImagesNatural;

        var buffer = new PixelBuffer(r.Width, r.Height);
        var bitmap = Pdfium.FPDFBitmap_CreateEx(r.Width, r.Height, Pdfium.FPDFBitmap_BGRA, (void*)buffer.Data, buffer.Stride);
        if (bitmap == IntPtr.Zero)
        {
            buffer.Dispose();
            throw new OutOfMemoryException("PDFium could not allocate a bitmap.");
        }

        try
        {
            var background = useColorScheme ? colors.Background : 0xFFFFFFFF;
            Pdfium.FPDFBitmap_FillRect(bitmap, 0, 0, r.Width, r.Height, background);

            var pause = new Pdfium.IFSDK_PAUSE { Version = 1, NeedToPauseNow = &NeedToPauseNow };
            const int flags = Pdfium.FPDF_ANNOT;
            int status;
            if (useColorScheme)
            {
                // Text and vector strokes take the foreground color; filled shapes get a faint tint so
                // table rules stay visible without large white boxes glaring. Images are untouched,
                // which is exactly "keep images natural".
                var scheme = new Pdfium.FPDF_COLORSCHEME
                {
                    PathFillColor = PageColors.Mix(colors.Background, colors.Foreground, 0.18),
                    PathStrokeColor = colors.Foreground,
                    TextFillColor = colors.Foreground,
                    TextStrokeColor = colors.Foreground,
                };
                status = Pdfium.FPDF_RenderPageBitmapWithColorScheme_Start(bitmap, page.Page, -r.TileX, -r.TileY, fullW, fullH, 0, flags, &scheme, &pause);
                while (status == Pdfium.FPDF_RENDER_TOBECONTINUED && !token.IsCancellationRequested)
                    status = Pdfium.FPDF_RenderPage_Continue(page.Page, &pause);
            }
            else
            {
                status = Pdfium.FPDF_RenderPageBitmap_Start(bitmap, page.Page, -r.TileX, -r.TileY, fullW, fullH, 0, flags, &pause);
                while (status == Pdfium.FPDF_RENDER_TOBECONTINUED && !token.IsCancellationRequested)
                    status = Pdfium.FPDF_RenderPage_Continue(page.Page, &pause);
            }
            Pdfium.FPDF_RenderPage_Close(page.Page);
            token.ThrowIfCancellationRequested();

            if (useLuminanceMap)
                PageColorFilter.ApplyLuminanceMap(buffer.Pixels, colors);

            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
        finally
        {
            Pdfium.FPDFBitmap_Destroy(bitmap);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Text
    // ---------------------------------------------------------------------------------------

    public Task<string> GetPageTextAsync(int pageIndex, WorkPriority priority = WorkPriority.Interactive, CancellationToken cancellationToken = default)
        => _engine.RunAsync(() =>
        {
            var tp = GetPage(pageIndex).GetTextPage();
            return GetText(tp, 0, Pdfium.FPDFText_CountChars(tp));
        }, priority, cancellationToken);

    /// <summary>Index of the character at a point (page points), or -1.</summary>
    public Task<int> GetCharIndexAtAsync(int pageIndex, double x, double y, double tolerance = 6, CancellationToken cancellationToken = default)
        => _engine.RunAsync(() =>
        {
            var page = GetPage(pageIndex);
            var (px, py) = ToPdfSpace(page, x, y);
            var index = Pdfium.FPDFText_GetCharIndexAtPos(page.GetTextPage(), px, py, tolerance, tolerance);
            return index < 0 ? -1 : index;
        }, WorkPriority.Interactive, cancellationToken);

    public Task<int> GetCharCountAsync(int pageIndex, CancellationToken cancellationToken = default)
        => _engine.RunAsync(() => Pdfium.FPDFText_CountChars(GetPage(pageIndex).GetTextPage()), WorkPriority.Interactive, cancellationToken);

    /// <summary>Text and highlight rectangles for an inclusive character range.</summary>
    public Task<TextSelection> GetSelectionAsync(int pageIndex, int fromChar, int toChar, CancellationToken cancellationToken = default)
        => _engine.RunAsync(() =>
        {
            var page = GetPage(pageIndex);
            var tp = page.GetTextPage();
            var total = Pdfium.FPDFText_CountChars(tp);
            var start = Math.Clamp(Math.Min(fromChar, toChar), 0, Math.Max(0, total - 1));
            var end = Math.Clamp(Math.Max(fromChar, toChar), 0, Math.Max(0, total - 1));
            if (total <= 0)
                return new TextSelection(pageIndex, 0, -1, string.Empty, []);
            var count = end - start + 1;
            return new TextSelection(pageIndex, start, end, GetText(tp, start, count), GetTextRects(page, start, count));
        }, WorkPriority.Interactive, cancellationToken);

    /// <summary>Finds all matches on one page.</summary>
    public unsafe Task<IReadOnlyList<SearchHit>> SearchPageAsync(int pageIndex, string query, bool matchCase, bool wholeWord,
        CancellationToken cancellationToken = default)
        => _engine.RunAsync<IReadOnlyList<SearchHit>>(() =>
        {
            if (string.IsNullOrEmpty(query))
                return [];
            var page = GetPage(pageIndex);
            var tp = page.GetTextPage();
            var flags = (matchCase ? Pdfium.FPDF_MATCHCASE : 0) | (wholeWord ? Pdfium.FPDF_MATCHWHOLEWORD : 0);
            var hits = new List<SearchHit>();
            fixed (char* q = query + '\0')
            {
                var handle = Pdfium.FPDFText_FindStart(tp, (ushort*)q, flags, 0);
                if (handle == IntPtr.Zero)
                    return hits;
                try
                {
                    while (Pdfium.FPDFText_FindNext(handle) != 0 && hits.Count < 10_000)
                    {
                        var index = Pdfium.FPDFText_GetSchResultIndex(handle);
                        var count = Pdfium.FPDFText_GetSchCount(handle);
                        hits.Add(new SearchHit(pageIndex, index, count, GetTextRects(page, index, count)));
                    }
                }
                finally
                {
                    Pdfium.FPDFText_FindClose(handle);
                }
            }
            return hits;
        }, WorkPriority.Interactive, cancellationToken);

    private static unsafe string GetText(IntPtr textPage, int start, int count)
    {
        if (count <= 0)
            return string.Empty;
        var buffer = new char[count + 1];
        fixed (char* p = buffer)
        {
            var written = Pdfium.FPDFText_GetText(textPage, start, count, (ushort*)p);
            return written <= 1 ? string.Empty : new string(p, 0, written - 1);
        }
    }

    private static unsafe PdfRect[] GetTextRects(PageHandle page, int start, int count)
    {
        var tp = page.GetTextPage();
        var n = Pdfium.FPDFText_CountRects(tp, start, count);
        if (n <= 0)
            return [];
        var rects = new PdfRect[n];
        for (var i = 0; i < n; i++)
        {
            double l, t, r, b;
            Pdfium.FPDFText_GetRect(tp, i, &l, &t, &r, &b);
            rects[i] = FromPdfRect(page, l, t, r, b);
        }
        return rects;
    }

    // ---------------------------------------------------------------------------------------
    // Outline, links, metadata, security
    // ---------------------------------------------------------------------------------------

    public Task<IReadOnlyList<OutlineItem>> GetOutlineAsync(CancellationToken cancellationToken = default)
        => _engine.RunAsync<IReadOnlyList<OutlineItem>>(() =>
        {
            var visited = new HashSet<IntPtr>();
            return ReadOutline(IntPtr.Zero, 0, visited);
        }, WorkPriority.Background, cancellationToken);

    private unsafe List<OutlineItem> ReadOutline(IntPtr parent, int depth, HashSet<IntPtr> visited)
    {
        var items = new List<OutlineItem>();
        if (depth > MaxOutlineDepth)
            return items;

        // visited guards against malicious cyclic outlines.
        for (var bm = Pdfium.FPDFBookmark_GetFirstChild(_document, parent);
             bm != IntPtr.Zero && visited.Count < MaxOutlineItems && visited.Add(bm);
             bm = Pdfium.FPDFBookmark_GetNextSibling(_document, bm))
        {
            var title = ReadUtf16((buf, len) => Pdfium.FPDFBookmark_GetTitle(bm, buf, len));
            var dest = Pdfium.FPDFBookmark_GetDest(_document, bm);
            if (dest == IntPtr.Zero)
            {
                var action = Pdfium.FPDFBookmark_GetAction(bm);
                if (action != IntPtr.Zero && Pdfium.FPDFAction_GetType(action) == Pdfium.PDFACTION_GOTO)
                    dest = Pdfium.FPDFAction_GetDest(_document, action);
            }
            var pageIndex = dest == IntPtr.Zero ? -1 : Pdfium.FPDFDest_GetDestPageIndex(_document, dest);
            items.Add(new OutlineItem(title.Trim(), pageIndex, ReadOutline(bm, depth + 1, visited)));
        }
        return items;
    }

    /// <summary>The link at a point (page points), if any. Links are never followed automatically.</summary>
    public Task<PdfLink?> GetLinkAtAsync(int pageIndex, double x, double y, CancellationToken cancellationToken = default)
        => _engine.RunAsync(() =>
        {
            var page = GetPage(pageIndex);
            var (px, py) = ToPdfSpace(page, x, y);
            var link = Pdfium.FPDFLink_GetLinkAtPoint(page.Page, px, py);
            return link == IntPtr.Zero ? null : ReadLink(page, link);
        }, WorkPriority.Interactive, cancellationToken);

    private unsafe PdfLink ReadLink(PageHandle page, IntPtr link)
    {
        Pdfium.FS_RECTF rect;
        var bounds = Pdfium.FPDFLink_GetAnnotRect(link, &rect) != 0
            ? FromPdfRect(page, rect.Left, rect.Top, rect.Right, rect.Bottom)
            : default;

        var dest = Pdfium.FPDFLink_GetDest(_document, link);
        if (dest != IntPtr.Zero)
            return new PdfLink(LinkKind.GoToPage, bounds, Pdfium.FPDFDest_GetDestPageIndex(_document, dest));

        var action = Pdfium.FPDFLink_GetAction(link);
        if (action == IntPtr.Zero)
            return new PdfLink(LinkKind.Unsupported, bounds);

        switch (Pdfium.FPDFAction_GetType(action))
        {
            case Pdfium.PDFACTION_GOTO:
                var actionDest = Pdfium.FPDFAction_GetDest(_document, action);
                return new PdfLink(LinkKind.GoToPage, bounds, actionDest == IntPtr.Zero ? -1 : Pdfium.FPDFDest_GetDestPageIndex(_document, actionDest));
            case Pdfium.PDFACTION_URI:
                var uri = ReadBytes((buf, len) => Pdfium.FPDFAction_GetURIPath(_document, action, buf, len));
                return new PdfLink(LinkKind.Uri, bounds, Target: Encoding.ASCII.GetString(uri).TrimEnd('\0'));
            case Pdfium.PDFACTION_LAUNCH:
                var file = ReadBytes((buf, len) => Pdfium.FPDFAction_GetFilePath(action, buf, len));
                return new PdfLink(LinkKind.Launch, bounds, Target: Encoding.UTF8.GetString(file).TrimEnd('\0'));
            case Pdfium.PDFACTION_REMOTEGOTO:
                var remote = ReadBytes((buf, len) => Pdfium.FPDFAction_GetFilePath(action, buf, len));
                return new PdfLink(LinkKind.RemoteFile, bounds, Target: Encoding.UTF8.GetString(remote).TrimEnd('\0'));
            default:
                return new PdfLink(LinkKind.Unsupported, bounds);
        }
    }

    /// <summary>
    /// Walks every page looking for launch actions and file-attachment annotations, one page per
    /// background work item so rendering is never held up. Returns the full report.
    /// </summary>
    public async Task<SecurityReport> ScanActiveContentAsync(CancellationToken cancellationToken = default)
    {
        int launches = 0, fileAnnots = 0;
        for (var i = 0; i < PageCount; i++)
        {
            var pageIndex = i;
            var (l, f) = await _engine.RunAsync(() => ScanPage(pageIndex), WorkPriority.Background, cancellationToken).ConfigureAwait(false);
            launches += l;
            fileAnnots += f;
        }
        Security = Security with { LaunchActionCount = launches, FileAttachmentAnnotations = fileAnnots };
        return Security;
    }

    private unsafe (int Launches, int FileAnnotations) ScanPage(int pageIndex)
    {
        ThrowIfDisposed();
        var cached = _pages.ContainsKey(pageIndex);
        var page = cached ? _pages[pageIndex].Page : Pdfium.FPDF_LoadPage(_document, pageIndex);
        if (page == IntPtr.Zero)
            return (0, 0);
        try
        {
            int launches = 0, fileAnnots = 0, pos = 0;
            IntPtr link;
            while (Pdfium.FPDFLink_Enumerate(page, &pos, &link) != 0)
            {
                var action = Pdfium.FPDFLink_GetAction(link);
                if (action != IntPtr.Zero && Pdfium.FPDFAction_GetType(action) == Pdfium.PDFACTION_LAUNCH)
                    launches++;
            }

            var annotCount = Pdfium.FPDFPage_GetAnnotCount(page);
            for (var a = 0; a < annotCount; a++)
            {
                var annot = Pdfium.FPDFPage_GetAnnot(page, a);
                if (annot == IntPtr.Zero)
                    continue;
                if (Pdfium.FPDFAnnot_GetSubtype(annot) == Pdfium.FPDF_ANNOT_FILEATTACHMENT)
                    fileAnnots++;
                Pdfium.FPDFPage_CloseAnnot(annot);
            }
            return (launches, fileAnnots);
        }
        finally
        {
            if (!cached)
                Pdfium.FPDF_ClosePage(page);
        }
    }

    private unsafe string? GetMetaText(string tag)
    {
        var tagBytes = Encoding.ASCII.GetBytes(tag + '\0');
        fixed (byte* t = tagBytes)
        {
            var tp = t;
            var value = ReadUtf16((buf, len) => Pdfium.FPDF_GetMetaText(_document, tp, buf, len)).Trim();
            return value.Length == 0 ? null : value;
        }
    }

    private unsafe delegate uint BufferReader(void* buffer, uint length);

    private static unsafe byte[] ReadBytes(BufferReader read)
    {
        var length = read(null, 0);
        if (length == 0 || length > 1 << 20)
            return [];
        var buffer = new byte[length];
        fixed (byte* p = buffer)
            read(p, length);
        return buffer;
    }

    private static unsafe string ReadUtf16(BufferReader read)
    {
        var bytes = ReadBytes(read);
        return bytes.Length < 2 ? string.Empty : Encoding.Unicode.GetString(bytes, 0, bytes.Length - 2);
    }

    // ---------------------------------------------------------------------------------------
    // Printing (Windows GDI). Always prints original colors.
    // ---------------------------------------------------------------------------------------

    /// <summary>Prints pages to a printer device context obtained from the print dialog.</summary>
    /// <remarks>One page per engine work item so on-screen rendering keeps flowing while spooling.</remarks>
    public async Task PrintAsync(IntPtr hdc, string documentName, IReadOnlyList<int> pageIndices, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = await _engine.RunAsync(() => StartDoc(hdc, documentName), WorkPriority.Immediate, CancellationToken.None).ConfigureAwait(false);
        if (!started)
            throw new IOException("The printer could not start the print job.");

        try
        {
            for (var i = 0; i < pageIndices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageIndex = pageIndices[i];
                await _engine.RunAsync(() => PrintPage(hdc, pageIndex), WorkPriority.Immediate, CancellationToken.None).ConfigureAwait(false);
                progress?.Report(i + 1);
            }
            await _engine.RunAsync(() => { Pdfium.EndDoc(hdc); }, WorkPriority.Immediate).ConfigureAwait(false);
        }
        catch
        {
            await _engine.RunAsync(() => { Pdfium.AbortDoc(hdc); }, WorkPriority.Immediate).ConfigureAwait(false);
            throw;
        }
    }

    private static unsafe bool StartDoc(IntPtr hdc, string documentName)
    {
        fixed (char* name = documentName + '\0')
        {
            var info = new Pdfium.DOCINFOW { cbSize = sizeof(Pdfium.DOCINFOW), lpszDocName = name };
            return Pdfium.StartDocW(hdc, &info) > 0;
        }
    }

    private void PrintPage(IntPtr hdc, int pageIndex)
    {
        var page = GetPage(pageIndex);
        var printableW = Pdfium.GetDeviceCaps(hdc, Pdfium.HORZRES);
        var printableH = Pdfium.GetDeviceCaps(hdc, Pdfium.VERTRES);
        var size = PageSizes[pageIndex];
        var scale = Math.Min(printableW / size.Width, printableH / size.Height);
        var w = (int)(size.Width * scale);
        var h = (int)(size.Height * scale);
        var x = (printableW - w) / 2;
        var y = (printableH - h) / 2;

        if (Pdfium.StartPage(hdc) <= 0)
            throw new IOException("The printer rejected a page.");
        Pdfium.FPDF_RenderPage(hdc, page.Page, x, y, w, h, 0, Pdfium.FPDF_ANNOT | Pdfium.FPDF_PRINTING);
        if (Pdfium.EndPage(hdc) <= 0)
            throw new IOException("The printer rejected a page.");
    }

    // ---------------------------------------------------------------------------------------
    // Page cache & coordinate conversion (engine thread only)
    // ---------------------------------------------------------------------------------------

    private PageHandle GetPage(int index)
    {
        ThrowIfDisposed();
        if ((uint)index >= (uint)PageCount)
            throw new PdfException(PdfError.PageNotFound, $"Page {index + 1} does not exist.");

        if (_pages.TryGetValue(index, out var cached))
        {
            _pageLru.Remove(cached.LruNode);
            _pageLru.AddFirst(cached.LruNode);
            return cached;
        }

        var handle = Pdfium.FPDF_LoadPage(_document, index);
        if (handle == IntPtr.Zero)
            throw new PdfException(PdfError.PageNotFound, $"Page {index + 1} could not be loaded.");

        var entry = new PageHandle(handle, Pdfium.FPDF_GetPageWidthF(handle), Pdfium.FPDF_GetPageHeightF(handle), _pageLru.AddFirst(index));
        _pages[index] = entry;

        while (_pages.Count > PageCacheCapacity)
        {
            var oldest = _pageLru.Last!.Value;
            _pageLru.RemoveLast();
            _pages.Remove(oldest, out var evicted);
            evicted?.Close();
        }
        return entry;
    }

    private static unsafe (double X, double Y) ToPdfSpace(PageHandle page, double x, double y)
    {
        double px, py;
        Pdfium.FPDF_DeviceToPage(page.Page, 0, 0, (int)(page.Width * CoordPrecision), (int)(page.Height * CoordPrecision), 0,
            (int)(x * CoordPrecision), (int)(y * CoordPrecision), &px, &py);
        return (px, py);
    }

    private static unsafe (double X, double Y) FromPdfSpace(PageHandle page, double px, double py)
    {
        int dx, dy;
        Pdfium.FPDF_PageToDevice(page.Page, 0, 0, (int)(page.Width * CoordPrecision), (int)(page.Height * CoordPrecision), 0, px, py, &dx, &dy);
        return (dx / (double)CoordPrecision, dy / (double)CoordPrecision);
    }

    private static PdfRect FromPdfRect(PageHandle page, double left, double top, double right, double bottom)
    {
        var (x1, y1) = FromPdfSpace(page, left, top);
        var (x2, y2) = FromPdfSpace(page, right, bottom);
        return PdfRect.FromEdges(x1, y1, x2, y2);
    }

    private void ThrowIfDisposed()
    {
        if (_document == IntPtr.Zero || IsDisposed)
            throw new ObjectDisposedException(nameof(PdfDocument));
    }

    private sealed class PageHandle(IntPtr page, float width, float height, LinkedListNode<int> lruNode)
    {
        private IntPtr _textPage;

        public IntPtr Page { get; } = page;
        public float Width { get; } = width;
        public float Height { get; } = height;
        public LinkedListNode<int> LruNode { get; } = lruNode;

        public IntPtr GetTextPage()
        {
            if (_textPage == IntPtr.Zero)
                _textPage = Pdfium.FPDFText_LoadPage(Page);
            return _textPage != IntPtr.Zero ? _textPage : throw new PdfException(PdfError.Unknown, "Text could not be read from this page.");
        }

        public void Close()
        {
            if (_textPage != IntPtr.Zero)
                Pdfium.FPDFText_ClosePage(_textPage);
            Pdfium.FPDF_ClosePage(Page);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Disposal
    // ---------------------------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _engine.RunAsync(ReleaseNative, WorkPriority.Immediate).ConfigureAwait(false);
    }

    private unsafe void ReleaseNative()
    {
        foreach (var page in _pages.Values)
            page.Close();
        _pages.Clear();
        _pageLru.Clear();
        if (_document != IntPtr.Zero)
        {
            Pdfium.FPDF_CloseDocument(_document);
            _document = IntPtr.Zero;
        }
        if (_fileAccess != null)
        {
            NativeMemory.Free(_fileAccess);
            _fileAccess = null;
        }
        if (_streamHandle.IsAllocated)
            _streamHandle.Free();
        _stream.Dispose();
    }
}
