using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Pipeline;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Platform.Windows.Tray;

namespace BlenderLauncher.App.Lifetime;

/// <summary>The three kinds of notification, each with its own setting (Settings → Appearance → Notifications).</summary>
public enum NotificationKind
{
    NewBuilds,
    DownloadFinished,
    Error,
}

/// <summary>Where a notification goes.</summary>
public enum NotificationRoute
{
    None,

    /// <summary>The status-bar toast: the window is on screen.</summary>
    Toast,

    /// <summary>A tray balloon: the window is hidden or minimised and the tray icon exists.</summary>
    Balloon,
}

/// <summary>
/// Routes the launcher's notifications (upstream <c>window.py:565-573</c>): new builds found by a check,
/// downloads finished, and errors (a failed download, a check that reached no source). Each kind is filtered by its
/// setting; while the window is on screen the status-bar toast is used, otherwise a tray balloon. A check the user
/// started is not notified: the list header already reports it.
/// </summary>
internal sealed class NotificationRouter
{
    private readonly Func<AppearanceSettings> _settings;
    private readonly Func<bool> _windowShown;
    private readonly Func<bool> _trayActive;
    private readonly Action<string> _toast;
    private readonly Func<string, TrayBalloonIcon, Task<bool>> _balloon;
    private MainWindowViewModel? _shell;

    public NotificationRouter(Func<AppearanceSettings> settings, Func<bool> windowShown, Func<bool> trayActive,
        Action<string> toast, Func<string, TrayBalloonIcon, Task<bool>> balloon)
    {
        _settings = settings;
        _windowShown = windowShown;
        _trayActive = trayActive;
        _toast = toast;
        _balloon = balloon;
    }

    /// <summary>Where a notification of <paramref name="kind"/> goes now.</summary>
    public static NotificationRoute RouteFor(NotificationKind kind, AppearanceSettings settings, bool windowShown, bool trayActive)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var enabled = kind switch
        {
            NotificationKind.NewBuilds => settings.NotifyNewBuilds,
            NotificationKind.DownloadFinished => settings.NotifyDownloadFinished,
            _ => settings.NotifyErrors,
        };
        return !enabled ? NotificationRoute.None
            : windowShown ? NotificationRoute.Toast
            : trayActive ? NotificationRoute.Balloon
            : NotificationRoute.None;
    }

    /// <summary>Follows the services' install jobs and checks. Call once, on the UI thread.</summary>
    public void Attach(AppServices services, MainWindowViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        services.Install.JobStateChanged += UiDispatcher.Marshal<InstallJob>(OnJobStateChanged);
        services.Scrape.CheckCompleted += UiDispatcher.Marshal<ScrapeSnapshot>(OnCheckCompleted);
    }

    /// <summary>Shows <paramref name="text"/> as a notification of <paramref name="kind"/>, if its setting allows.</summary>
    public void Notify(NotificationKind kind, string text)
    {
        switch (RouteFor(kind, _settings(), _windowShown(), _trayActive()))
        {
            case NotificationRoute.Toast:
                _toast(text);
                break;
            case NotificationRoute.Balloon:
                _ = _balloon(text, kind == NotificationKind.Error ? TrayBalloonIcon.Error : TrayBalloonIcon.Info);
                break;
        }
    }

    private void OnJobStateChanged(InstallJob job)
    {
        if (job.State is not (InstallState.Done or InstallState.Failed))
        {
            return;
        }

        var name = _shell?.Catalog.FindAvailable(ScrapeService.DeduplicationKey(job.Build.Build))?.Name is { Length: > 0 } rowName
            ? rowName
            : job.Build.Build.DisplayVersion;
        if (job.State == InstallState.Done)
        {
            Notify(NotificationKind.DownloadFinished, Loc.Tr("ui.notify.download_finished", ("name", name)));
        }
        else
        {
            Notify(NotificationKind.Error, Loc.Tr("ui.notify.download_failed", ("name", name), ("error", job.Error?.Message ?? "")));
        }
    }

    private void OnCheckCompleted(object? sender, ScrapeSnapshot snapshot)
    {
        if (_shell is { IsManualCheck: true })
        {
            return;
        }

        if (snapshot.NewCount > 0)
        {
            Notify(NotificationKind.NewBuilds, Loc.Plural("ui.toast.new_builds", snapshot.NewCount));
        }
        else if (snapshot.Sources.Count > 0 && snapshot.Sources.All(s => !s.Succeeded))
        {
            Notify(NotificationKind.Error, Loc.Tr("ui.notify.check_failed"));
        }
    }
}
