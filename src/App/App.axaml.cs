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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = StartupOptions.Parse(desktop.Args ?? []);
            // Benchmarks must not touch the user's settings, recent files or session.
            var store = options.BenchmarkOutput is null
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

/// <summary>Command line: file paths to open, plus <c>--benchmark out.json</c> for CI startup measurements.</summary>
public sealed record StartupOptions(IReadOnlyList<string> Files, string? BenchmarkOutput)
{
    public static StartupOptions Parse(string[] args)
    {
        var files = new List<string>();
        string? benchmark = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--benchmark" && i + 1 < args.Length)
                benchmark = args[++i];
            else if (!args[i].StartsWith("--", StringComparison.Ordinal))
                files.Add(args[i]);
        }
        return new StartupOptions(files, benchmark);
    }
}
