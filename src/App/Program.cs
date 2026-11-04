using Avalonia;
using PdfReader.App.Services;
using Velopack;

namespace PdfReader.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack install/update/uninstall hooks and exits quickly when invoked by them.
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => FileAssociation.Register())
            .OnAfterUpdateFastCallback(_ => FileAssociation.Register())
            .OnBeforeUninstallFastCallback(_ => FileAssociation.Unregister())
            .Run();

        var options = StartupOptions.Parse(args);
        if (!options.IsAutomated && !SingleInstance.TryBecomePrimary(options.Files.ToArray()))
            return; // handed the files to the window that is already open

        // Load and initialize PDFium on its own thread while the window is being created.
        _ = PdfReader.Rendering.PdfEngine.Shared.RunAsync(static () => { }, PdfReader.Rendering.WorkPriority.Immediate);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
