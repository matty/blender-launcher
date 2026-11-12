using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.ViewModels.Detail;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace BlenderLauncher.App.Lifetime;

/// <summary>
/// The self-update through Velopack: <see cref="PrepareAsync"/> downloads the newest release behind the
/// progress window, <see cref="ApplyAndRestart"/> hands it to Velopack's Update.exe, which waits for this process to
/// exit, swaps the <c>current</c> folder and starts the new version with <see cref="UpdatedFromSwitch"/>, so it says
/// "Updated to X". Only a copy Velopack manages (<see cref="AppLocation.IsVelopack"/>) can update; a loose exe is sent to
/// the release page.
/// </summary>
internal static class SelfUpdate
{
    /// <summary>Hidden: given to the new version on restart, with the old version. Shows "Updated to X".</summary>
    public const string UpdatedFromSwitch = "--updated-from";

    /// <summary>
    /// Hidden and test-only: <c>--update-source FOLDER-OR-URL</c> reads Velopack releases (<c>releases.win.json</c> and its
    /// packages, as <c>vpk pack</c> writes them) from a local folder or another web server instead of GitHub, so the
    /// update can be verified end to end before a release.
    /// </summary>
    public const string UpdateSourceSwitch = "--update-source";

    /// <summary>The update repository; a property, so null checks stay meaningful while it is a constant.</summary>
    private static string? Repo => AppIdentity.UpdateRepo;

    /// <summary>The value after <paramref name="name"/>, or null.</summary>
    public static string? ValueOf(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>The version this process was updated from (<see cref="UpdatedFromSwitch"/>), or null for a normal start.</summary>
    public static string? UpdatedFrom(IReadOnlyList<string> args) => ValueOf(args, UpdatedFromSwitch);

    /// <summary>True when a test source is in use; the CLI then skips its "launcher running" refusal.</summary>
    public static bool HasTestSource(IReadOnlyList<string> args) => ValueOf(args, UpdateSourceSwitch) is not null;

    /// <summary>
    /// Velopack's update manager over the test source or GitHub's releases of <see cref="AppIdentity.UpdateRepo"/>
    /// (pre-releases included when <paramref name="includePreReleases"/>). Null when this copy cannot update itself:
    /// Velopack does not manage it, or self-update is switched off.
    /// </summary>
    public static UpdateManager? CreateManager(IReadOnlyList<string> args, Func<HttpClient> http, bool includePreReleases, Log? log)
    {
        if (!AppLocation.IsVelopack)
        {
            return null;
        }

        var downloader = new VelopackDownloader(http);
        IUpdateSource? source = ValueOf(args, UpdateSourceSwitch) is { } test
            ? Uri.TryCreate(test, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
                ? new SimpleWebSource(url, downloader)
                : new SimpleFileSource(new DirectoryInfo(test))
            : Repo is { } repo
                ? new GithubSource(ReleaseAssets.RepositoryUrl(repo), accessToken: null, prerelease: includePreReleases, downloader)
                : null;
        if (source is null)
        {
            return null;
        }

        try
        {
            var manager = new UpdateManager(source);
            return manager.IsInstalled ? manager : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            log?.Warning("Velopack's update manager could not be created", e);
            return null;
        }
    }

    /// <summary>
    /// Checks the source and downloads the newest release. <see cref="UpdateOutcome.Ready"/> comes with the package to
    /// give <see cref="ApplyAndRestart"/>; nothing is changed until then.
    /// </summary>
    public static async Task<(UpdateResult Result, VelopackAsset? Package)> PrepareAsync(UpdateManager manager,
        IProgress<UpdateProgress> progress, Log? log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(progress);
        try
        {
            progress.Report(new UpdateProgress(UpdateStage.Downloading, null));
            var info = await manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
            if (info is null)
            {
                log?.Info("Velopack found no newer release");
                return (new UpdateResult(UpdateOutcome.UpToDate), null);
            }

            var target = info.TargetFullRelease;
            log?.Info($"Downloading {target.FileName} ({target.Version}) for the self-update");
            await manager.DownloadUpdatesAsync(info, percent => progress.Report(new UpdateProgress(UpdateStage.Downloading, percent / 100.0)), ct)
                .ConfigureAwait(false);
            progress.Report(new UpdateProgress(UpdateStage.Installing, null));
            return (new UpdateResult(UpdateOutcome.Ready), target);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log?.Info("Self-update cancelled");
            return (new UpdateResult(UpdateOutcome.Cancelled), null);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log?.Error("Self-update download failed", e);
            return (new UpdateResult(UpdateOutcome.Failed, $"{e.GetType().Name}: {e.Message}"), null);
        }
    }

    /// <summary>
    /// Starts Velopack's Update.exe, which waits for this process to exit (up to a minute), installs
    /// <paramref name="package"/> and starts the new version with the restart arguments. The caller then shuts down.
    /// False when Update.exe could not be started.
    /// </summary>
    public static bool ApplyAndRestart(UpdateManager manager, VelopackAsset package, IReadOnlyList<string> args, Log? log)
    {
        ArgumentNullException.ThrowIfNull(manager);
        try
        {
            manager.WaitExitThenApplyUpdates(package, silent: false, restart: true, [.. RelaunchArguments(args, AppIdentity.Version)]);
            log?.Info($"Velopack will install {package.Version} once this process exits");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or ArgumentException)
        {
            log?.Error("Could not start Velopack's updater", e);
            return false;
        }
    }

    /// <summary>The release page for <paramref name="tag"/>, or the newest release without a tag.</summary>
    public static string ReleasePage(string? tag) => Repo is not { } repo
        ? "https://github.com/"
        : tag is null
            ? $"https://github.com/{repo}/releases/latest"
            : ReleaseAssets.ReleasePageUrl(repo, tag).AbsoluteUri;

    /// <summary>
    /// The new version's arguments: the restart rules (<see cref="AppRestart.ArgumentsFor"/>: options kept, commands
    /// dropped, <c>--restarted</c> so it waits for the pipe) plus <see cref="UpdatedFromSwitch"/>. An off-screen
    /// <c>--window-pos</c> is carried over even when it came after a command.
    /// </summary>
    public static IReadOnlyList<string> RelaunchArguments(IReadOnlyList<string> args, string fromVersion)
    {
        var result = AppRestart.ArgumentsFor(args).ToList();
        if (ValueOf(args, WindowPositionOverride.Switch) is { } position && ValueOf(result, WindowPositionOverride.Switch) is null)
        {
            result.Add(WindowPositionOverride.Switch);
            result.Add(position);
        }

        result.Add(UpdatedFromSwitch);
        result.Add(fromVersion);
        return result;
    }

    /// <summary>
    /// This copy cannot update itself (not set up by the installer or the portable zip): offers the release page
    /// instead. <paramref name="owner"/> may be null.
    /// </summary>
    public static async Task ShowNotManagedAsync(string? tag, Avalonia.Controls.Window? owner)
    {
        var answer = await DialogService.ShowAsync(new DialogRequest
        {
            Kind = DialogKind.Info,
            Title = Loc.Tr("ui.update.progress_title"),
            Message = Loc.Tr("ui.update.not_managed"),
            Buttons =
            [
                new PromptButton(Loc.Tr("ui.update.open_page"), IsPrimary: true),
                new PromptButton(Loc.Tr("act.cancel"), IsCancel: true),
            ],
        }, owner).ConfigureAwait(true);
        if (answer == 0)
        {
            DialogService.OpenWithShell(ReleasePage(tag));
        }
    }
}
