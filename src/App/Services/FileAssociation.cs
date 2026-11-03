using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PdfReader.App.Services;

/// <summary>
/// Per-user (HKCU, no admin) registration so PDF.Reader appears under "Open with" for .pdf.
/// It never makes itself the default app; Windows lets the user choose that.
/// </summary>
internal static partial class FileAssociation
{
    private const string ProgId = "PDFReader.Document";
    private const string AppKey = @"Software\Classes\Applications\PDF.Reader.exe";

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    public static void Register()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return;
            var command = $"\"{exe}\" \"%1\"";

            using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                prog.SetValue("", "PDF Document");
                prog.CreateSubKey("DefaultIcon").SetValue("", $"\"{exe}\",0");
                prog.CreateSubKey(@"shell\open\command").SetValue("", command);
            }
            using (var app = Registry.CurrentUser.CreateSubKey(AppKey))
            {
                app.SetValue("FriendlyAppName", "PDF.Reader");
                app.CreateSubKey(@"shell\open\command").SetValue("", command);
                app.CreateSubKey("SupportedTypes").SetValue(".pdf", "");
            }
            using (var ext = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids"))
                ext.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);

            Notify();
        }
        catch (Exception)
        {
            // Never block install/update on optional shell integration.
        }
    }

    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(AppKey, throwOnMissingSubKey: false);
            using var ext = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids", writable: true);
            ext?.DeleteValue(ProgId, throwOnMissingValue: false);
            Notify();
        }
        catch (Exception) { }
    }

    private static void Notify() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
}
