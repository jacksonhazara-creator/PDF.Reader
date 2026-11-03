using CommunityToolkit.Mvvm.ComponentModel;
using PdfReader.App.Localization;
using PdfReader.Core.Settings;
using Velopack;
using Velopack.Sources;

namespace PdfReader.App.Services;

/// <summary>
/// Updates via Velopack from public GitHub Releases: anonymous HTTPS, no token. Stable users only
/// see full releases; Beta users also get pre-releases (tags like v1.2.0-beta.1). Velopack verifies
/// package hashes before applying.
/// </summary>
public sealed partial class UpdateService(AppSettings settings) : ObservableObject
{
    private UpdateInfo? _ready;

    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsReadyToInstall { get; set; }
    [ObservableProperty] public partial string? ReleaseNotes { get; set; }
    [ObservableProperty] public partial int DownloadProgress { get; set; }

    public bool HasReleaseNotes => !string.IsNullOrWhiteSpace(ReleaseNotes);
    partial void OnReleaseNotesChanged(string? value) => OnPropertyChanged(nameof(HasReleaseNotes));

    public string CurrentVersion => SafeManager()?.CurrentVersion?.ToString() ?? AppInfo.Version;

    private UpdateManager? SafeManager()
    {
        try
        {
            var source = new GithubSource(AppInfo.RepositoryUrl, null, settings.UpdateChannel == UpdateChannel.Beta);
            return new UpdateManager(source);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task CheckAsync(bool userInitiated)
    {
        if (IsBusy || IsReadyToInstall)
            return;

        var manager = SafeManager();
        if (manager is null || !manager.IsInstalled)
        {
            if (userInitiated)
                Status = Loc.Get("UpdatesNotInstalled");
            return;
        }

        IsBusy = true;
        Status = Loc.Get("CheckingUpdates");
        try
        {
            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                Status = Loc.Get("UpToDate");
                return;
            }

            var version = update.TargetFullRelease.Version.ToString();
            ReleaseNotes = update.TargetFullRelease.NotesMarkdown;
            Status = Loc.Format("UpdateAvailable", version);

            await manager.DownloadUpdatesAsync(update, p =>
            {
                DownloadProgress = p;
                Status = Loc.Format("Downloading", p);
            });

            _ready = update;
            IsReadyToInstall = true;
            Status = Loc.Format("ReadyToInstall", version);
        }
        catch (Exception ex)
        {
            Status = userInitiated ? Loc.Format("UpdateFailed", ex.Message) : "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void RestartToInstall()
    {
        if (_ready is null || SafeManager() is not { } manager)
            return;
        manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease);
    }
}
