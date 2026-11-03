using System.Runtime.InteropServices;

namespace PdfReader.App.Services;

/// <summary>A printer device context plus the pages chosen in the print dialog.</summary>
public sealed class PrintJob(IntPtr deviceContext, IReadOnlyList<int> pages) : IDisposable
{
    public IntPtr DeviceContext { get; private set; } = deviceContext;
    public IReadOnlyList<int> Pages { get; } = pages;

    public void Dispose()
    {
        if (DeviceContext != IntPtr.Zero)
        {
            PrintDialog.DeleteDC(DeviceContext);
            DeviceContext = IntPtr.Zero;
        }
    }
}

/// <summary>The standard Windows print dialog (comdlg32). Printing renders with PDFium straight to GDI.</summary>
internal static partial class PrintDialog
{
    private const int PD_PAGENUMS = 0x2;
    private const int PD_NOSELECTION = 0x4;
    private const int PD_RETURNDC = 0x100;
    private const int PD_USEDEVMODECOPIESANDCOLLATE = 0x40000;

    [StructLayout(LayoutKind.Sequential)]
    private struct PRINTDLGW
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hDevMode;
        public IntPtr hDevNames;
        public IntPtr hDC;
        public int Flags;
        public ushort nFromPage;
        public ushort nToPage;
        public ushort nMinPage;
        public ushort nMaxPage;
        public ushort nCopies;
        public IntPtr hInstance;
        public IntPtr lCustData;
        public IntPtr lpfnPrintHook;
        public IntPtr lpfnSetupHook;
        public IntPtr lpPrintTemplateName;
        public IntPtr lpSetupTemplateName;
        public IntPtr hPrintTemplate;
        public IntPtr hSetupTemplate;
    }

    [LibraryImport("comdlg32.dll")]
    private static partial int PrintDlgW(ref PRINTDLGW dialog);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalFree(IntPtr handle);

    [LibraryImport("gdi32.dll")]
    internal static partial int DeleteDC(IntPtr hdc);

    [LibraryImport("user32.dll")]
    private static partial int EnableWindow(IntPtr hwnd, int enable);

    /// <summary>
    /// Shows the dialog on its own STA thread so the app's UI thread keeps running (rendering,
    /// accessibility). The owner window is disabled meanwhile, so the dialog still behaves modally.
    /// </summary>
    public static Task<PrintJob?> ShowAsync(IntPtr owner, int pageCount)
    {
        var tcs = new TaskCompletionSource<PrintJob?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (owner != IntPtr.Zero) EnableWindow(owner, 0);
                tcs.TrySetResult(Show(owner, pageCount));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                if (owner != IntPtr.Zero) EnableWindow(owner, 1);
            }
        }) { IsBackground = true, Name = "Print dialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    public static unsafe PrintJob? Show(IntPtr owner, int pageCount)
    {
        if (pageCount <= 0)
            return null;
        var max = (ushort)Math.Min(pageCount, ushort.MaxValue);
        var dialog = new PRINTDLGW
        {
            lStructSize = sizeof(PRINTDLGW),
            hwndOwner = owner,
            Flags = PD_RETURNDC | PD_NOSELECTION | PD_USEDEVMODECOPIESANDCOLLATE,
            nMinPage = 1,
            nMaxPage = max,
            nFromPage = 1,
            nToPage = max,
            nCopies = 1,
        };

        try
        {
            if (PrintDlgW(ref dialog) == 0 || dialog.hDC == IntPtr.Zero)
                return null;

            var from = 1;
            var to = (int)max;
            if ((dialog.Flags & PD_PAGENUMS) != 0)
            {
                from = Math.Clamp((int)dialog.nFromPage, 1, max);
                to = Math.Clamp((int)dialog.nToPage, from, max);
            }
            return new PrintJob(dialog.hDC, Enumerable.Range(from - 1, to - from + 1).ToArray());
        }
        finally
        {
            if (dialog.hDevMode != IntPtr.Zero) GlobalFree(dialog.hDevMode);
            if (dialog.hDevNames != IntPtr.Zero) GlobalFree(dialog.hDevNames);
        }
    }
}
