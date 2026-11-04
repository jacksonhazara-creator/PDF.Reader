using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PdfReader.App.Localization;
using PdfReader.App.Views;
using PdfReader.Core.Settings;

namespace PdfReader.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Icon-only buttons get their tooltip as accessible name (screen readers, UI automation).
        Avalonia.Controls.Control.LoadedEvent.AddClassHandler<Avalonia.Controls.Button>((button, _) =>
        {
            if (string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(button))
                && button.Content is not string
                && Avalonia.Controls.ToolTip.GetTip(button) is string tip)
                Avalonia.Automation.AutomationProperties.SetName(button, tip);
        });

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = StartupOptions.Parse(desktop.Args ?? []);
            // Benchmarks and diagnosis runs must not touch the user's settings, recent files or session.
            var store = !options.IsAutomated
                ? new JsonStore()
                : new JsonStore(Path.Combine(Path.GetTempPath(), $"pdfreader-bench-{Guid.NewGuid():N}"));
            var settings = store.LoadSettings();
            var state = store.LoadState();
            Loc.Initialize(settings.Language);

            var window = new MainWindow(store, settings, state, options);
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => window.SaveAll();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>
/// Command line: file paths to open, plus <c>--benchmark out.json</c> for CI startup measurements and
/// <c>--diagnose report.txt</c> to run the self-diagnosis unattended, write the report and exit
/// (exit code 0 = no failed checks, 1 = at least one failed check).
/// </summary>
public sealed record StartupOptions(IReadOnlyList<string> Files, string? BenchmarkOutput, string? DiagnoseOutput = null)
{
    /// <summary>Automated runs use a throwaway settings folder and skip the single-instance handoff.</summary>
    public bool IsAutomated => BenchmarkOutput is not null || DiagnoseOutput is not null;

    public static StartupOptions Parse(string[] args)
    {
        var files = new List<string>();
        string? benchmark = null;
        string? diagnose = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--benchmark" && i + 1 < args.Length)
                benchmark = args[++i];
            else if (args[i] == "--diagnose" && i + 1 < args.Length)
                diagnose = args[++i];
            else if (!args[i].StartsWith("--", StringComparison.Ordinal))
                files.Add(args[i]);
        }
        return new StartupOptions(files, benchmark, diagnose);
    }
}
