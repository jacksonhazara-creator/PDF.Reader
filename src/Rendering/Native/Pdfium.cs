using System.Runtime.InteropServices;

namespace PdfReader.Rendering.Native;

// Thin, AOT-friendly P/Invoke layer over the PDFium C API. Signatures mirror the
// headers shipped in the bblanchon.PDFium.Win32 package (build/native/include).
// PDFium is NOT thread-safe: every call must happen on the PdfEngine worker thread.
// Note: C "unsigned long" is 32-bit on Windows, hence uint.
internal static unsafe partial class Pdfium
{
    private const string Lib = "pdfium";

    // Errors (FPDF_GetLastError)
    public const uint FPDF_ERR_SUCCESS = 0;
    public const uint FPDF_ERR_UNKNOWN = 1;
    public const uint FPDF_ERR_FILE = 2;
    public const uint FPDF_ERR_FORMAT = 3;
    public const uint FPDF_ERR_PASSWORD = 4;
    public const uint FPDF_ERR_SECURITY = 5;
    public const uint FPDF_ERR_PAGE = 6;

    // Render flags
    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;
    public const int FPDF_PRINTING = 0x800;

    public const int FPDFBitmap_BGRA = 4;

    // Progressive render status
    public const int FPDF_RENDER_TOBECONTINUED = 1;

    // Action types
    public const uint PDFACTION_GOTO = 1;
    public const uint PDFACTION_REMOTEGOTO = 2;
    public const uint PDFACTION_URI = 3;
    public const uint PDFACTION_LAUNCH = 4;
    public const uint PDFACTION_EMBEDDEDGOTO = 5;

    public const int FPDF_ANNOT_FILEATTACHMENT = 17;

    // Search flags
    public const uint FPDF_MATCHCASE = 0x1;
    public const uint FPDF_MATCHWHOLEWORD = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_SIZEF { public float Width; public float Height; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_RECTF { public float Left; public float Top; public float Right; public float Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_COLORSCHEME
    {
        public uint PathFillColor;
        public uint PathStrokeColor;
        public uint TextFillColor;
        public uint TextStrokeColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEACCESS
    {
        public uint FileLen;
        public delegate* unmanaged<IntPtr, uint, byte*, uint, int> GetBlock;
        public IntPtr Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IFSDK_PAUSE
    {
        public int Version;
        public delegate* unmanaged<IFSDK_PAUSE*, int> NeedToPauseNow;
        public IntPtr User;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DOCINFOW
    {
        public int cbSize;
        public char* lpszDocName;
        public char* lpszOutput;
        public char* lpszDatatype;
        public uint fwType;
    }

    // --- Library -----------------------------------------------------------------
    [LibraryImport(Lib)] public static partial void FPDF_InitLibrary();
    [LibraryImport(Lib)] public static partial uint FPDF_GetLastError();

    // --- Documents -----------------------------------------------------------------
    [LibraryImport(Lib)] public static partial IntPtr FPDF_LoadCustomDocument(FPDF_FILEACCESS* access, byte* password);
    [LibraryImport(Lib)] public static partial void FPDF_CloseDocument(IntPtr document);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageCount(IntPtr document);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageSizeByIndexF(IntPtr document, int index, FS_SIZEF* size);
    [LibraryImport(Lib)] public static partial uint FPDF_GetMetaText(IntPtr document, byte* tag, void* buffer, uint buflen);
    [LibraryImport(Lib)] public static partial int FPDFDoc_GetJavaScriptActionCount(IntPtr document);
    [LibraryImport(Lib)] public static partial int FPDFDoc_GetAttachmentCount(IntPtr document);

    // --- Pages ---------------------------------------------------------------------
    [LibraryImport(Lib)] public static partial IntPtr FPDF_LoadPage(IntPtr document, int index);
    [LibraryImport(Lib)] public static partial void FPDF_ClosePage(IntPtr page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageWidthF(IntPtr page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageHeightF(IntPtr page);

    [LibraryImport(Lib)]
    public static partial int FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate,
        double pageX, double pageY, int* deviceX, int* deviceY);

    [LibraryImport(Lib)]
    public static partial int FPDF_DeviceToPage(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate,
        int deviceX, int deviceY, double* pageX, double* pageY);

    // --- Bitmaps & rendering -------------------------------------------------------
    [LibraryImport(Lib)] public static partial IntPtr FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);
    [LibraryImport(Lib)] public static partial void FPDFBitmap_Destroy(IntPtr bitmap);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Lib)]
    public static partial int FPDF_RenderPageBitmap_Start(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY,
        int rotate, int flags, IFSDK_PAUSE* pause);

    [LibraryImport(Lib)]
    public static partial int FPDF_RenderPageBitmapWithColorScheme_Start(IntPtr bitmap, IntPtr page, int startX, int startY,
        int sizeX, int sizeY, int rotate, int flags, FPDF_COLORSCHEME* colorScheme, IFSDK_PAUSE* pause);

    [LibraryImport(Lib)] public static partial int FPDF_RenderPage_Continue(IntPtr page, IFSDK_PAUSE* pause);
    [LibraryImport(Lib)] public static partial void FPDF_RenderPage_Close(IntPtr page);

    /// <summary>Windows only: renders directly into a GDI device context (used for printing).</summary>
    [LibraryImport(Lib)]
    public static partial int FPDF_RenderPage(IntPtr dc, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    // --- Text ------------------------------------------------------------------------
    [LibraryImport(Lib)] public static partial IntPtr FPDFText_LoadPage(IntPtr page);
    [LibraryImport(Lib)] public static partial void FPDFText_ClosePage(IntPtr textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_CountChars(IntPtr textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_GetText(IntPtr textPage, int start, int count, ushort* result);
    [LibraryImport(Lib)] public static partial int FPDFText_GetCharIndexAtPos(IntPtr textPage, double x, double y, double xTolerance, double yTolerance);
    [LibraryImport(Lib)] public static partial int FPDFText_CountRects(IntPtr textPage, int start, int count);
    [LibraryImport(Lib)] public static partial int FPDFText_GetRect(IntPtr textPage, int index, double* left, double* top, double* right, double* bottom);

    [LibraryImport(Lib)] public static partial IntPtr FPDFText_FindStart(IntPtr textPage, ushort* findWhat, uint flags, int startIndex);
    [LibraryImport(Lib)] public static partial int FPDFText_FindNext(IntPtr search);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchResultIndex(IntPtr search);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchCount(IntPtr search);
    [LibraryImport(Lib)] public static partial void FPDFText_FindClose(IntPtr search);

    // --- Outline, destinations, actions, links ------------------------------------------
    [LibraryImport(Lib)] public static partial IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);
    [LibraryImport(Lib)] public static partial IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);
    [LibraryImport(Lib)] public static partial uint FPDFBookmark_GetTitle(IntPtr bookmark, void* buffer, uint buflen);
    [LibraryImport(Lib)] public static partial IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);
    [LibraryImport(Lib)] public static partial IntPtr FPDFBookmark_GetAction(IntPtr bookmark);
    [LibraryImport(Lib)] public static partial uint FPDFAction_GetType(IntPtr action);
    [LibraryImport(Lib)] public static partial IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);
    [LibraryImport(Lib)] public static partial uint FPDFAction_GetURIPath(IntPtr document, IntPtr action, void* buffer, uint buflen);
    [LibraryImport(Lib)] public static partial uint FPDFAction_GetFilePath(IntPtr action, void* buffer, uint buflen);
    [LibraryImport(Lib)] public static partial int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);

    [LibraryImport(Lib)] public static partial IntPtr FPDFLink_GetLinkAtPoint(IntPtr page, double x, double y);
    [LibraryImport(Lib)] public static partial IntPtr FPDFLink_GetDest(IntPtr document, IntPtr link);
    [LibraryImport(Lib)] public static partial IntPtr FPDFLink_GetAction(IntPtr link);
    [LibraryImport(Lib)] public static partial int FPDFLink_Enumerate(IntPtr page, int* startPos, IntPtr* link);
    [LibraryImport(Lib)] public static partial int FPDFLink_GetAnnotRect(IntPtr link, FS_RECTF* rect);

    // --- Annotations (inspection only) ------------------------------------------------------
    [LibraryImport(Lib)] public static partial int FPDFPage_GetAnnotCount(IntPtr page);
    [LibraryImport(Lib)] public static partial IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetSubtype(IntPtr annot);
    [LibraryImport(Lib)] public static partial void FPDFPage_CloseAnnot(IntPtr annot);

    // --- GDI (printing) ----------------------------------------------------------------
    [LibraryImport("gdi32.dll")] public static partial int StartDocW(IntPtr hdc, DOCINFOW* docInfo);
    [LibraryImport("gdi32.dll")] public static partial int EndDoc(IntPtr hdc);
    [LibraryImport("gdi32.dll")] public static partial int AbortDoc(IntPtr hdc);
    [LibraryImport("gdi32.dll")] public static partial int StartPage(IntPtr hdc);
    [LibraryImport("gdi32.dll")] public static partial int EndPage(IntPtr hdc);
    [LibraryImport("gdi32.dll")] public static partial int GetDeviceCaps(IntPtr hdc, int index);

    public const int HORZRES = 8;
    public const int VERTRES = 10;
}
