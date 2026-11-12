using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using BlenderLauncher.App.Controls;
using BlenderLauncher.App.DesignData;
using BlenderLauncher.App.Diagnostics;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.Theme;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.App.ViewModels.Settings;
using BlenderLauncher.App.ViewModels.Detail;
using BlenderLauncher.App.ViewModels.FirstRun;
using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.App.Views;
using BlenderLauncher.App.Views.Dialogs;
using BlenderLauncher.App.Views.FirstRun;
using BlenderLauncher.App.Views.Settings;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Processes;
using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Core.Settings.Upstream;
using BlenderLauncher.Core.Updates;
using BlenderLauncher.Platform.Windows;
using BlenderLauncher.Platform.Windows.Tray;

namespace BlenderLauncher.App.Lifetime;

/// <summary>
/// The application lifetime: builds and shows the main window, starts the services after the first frame,
/// answers the single-instance pipe, decides between hiding and quitting on close, saves the window placement and shuts
/// everything down in order. Runs with <see cref="ShutdownMode.OnExplicitShutdown"/>: only <see cref="QuitAsync"/> (or
/// the OS) ends the process.
/// </summary>
/// <remarks>
/// Also runs: the first-run wizard before the main window, the tray (<see cref="TrayController"/>)
/// with hide-to-tray on close and "start hidden", notifications (<see cref="NotificationRouter"/>), the launcher update
/// dialog, the quit confirmation while downloads run (<see cref="ConfirmQuitAsync"/>) and the on-launch
/// action, and the shell integration (<see cref="ShellIntegration"/>): the quick-launch hotkey, the Add/Remove
/// Programs entry and the first-run wizard's pending shortcuts and file association.
/// </remarks>
internal sealed class AppController : ITrayHost
{
    private const string DevThrowSwitch = "--dev-throw";
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(15);

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly AppBootstrap _boot;
    private WindowPlacementService? _placement;
    private Task<AppServices>? _servicesStart;
    private AppServices? _services;
    private PlatformServiceBundle? _platform;
    private SettingsWindow? _settingsWindow;
    private TrayController? _tray;
    private NotificationRouter? _notifications;
    private ShellIntegration? _integration;
    private bool _quitting;
    private bool _confirmingQuit;
    private bool _updateDialogOpen;
    private int _shutDown;

    public AppController(IClassicDesktopStyleApplicationLifetime desktop, AppBootstrap boot)
    {
        _desktop = desktop;
        _boot = boot;
    }

    /// <summary>The running controller, once <see cref="Start"/> has run.</summary>
    public static AppController? Current { get; private set; }

    public MainWindow Window { get; private set; } = null!;

    public MainWindowViewModel ViewModel { get; private set; } = null!;

    /// <summary>
    /// Whether closing the window may hide it to the tray instead of quitting (setting <c>general.minimiseToTray</c>):
    /// only while the tray icon exists, so the window is never hidden with no way back.
    /// </summary>
    public Func<bool> CanHideToTray { get; set; } = static () => false;

    /// <summary>Asked before quitting; false cancels the quit. Asks while downloads or installs are running.</summary>
    public Func<Task<bool>> ConfirmQuitAsync { get; set; } = static () => Task.FromResult(true);

    /// <summary>The first-run wizard, while it is open (self-test and tests).</summary>
    public FirstRunWindow? FirstRunWindow { get; private set; }

    /// <summary>A second launch asked to open a <c>.blend</c> file; the launch picker has been opened for it.</summary>
    public event EventHandler<string>? OpenFileRequested;

    /// <summary>
    /// Starts the UI: the first-run wizard when it has not been completed (or <c>-force-first-time</c>), otherwise the
    /// main window, which becomes the lifetime's main window (shown by Avalonia unless it starts hidden in the tray).
    /// </summary>
    public void Start()
    {
        Current = this;
        ErrorHandling.InstallUiHandler();
        _desktop.Exit += OnExit;
        ConfirmQuitAsync = ConfirmQuitWhileBusyAsync;
        CanHideToTray = () => _tray?.IsActive == true;

        if (NeedsFirstRun())
        {
            ShowFirstRun();
            return;
        }

        StartMain(duringStartup: true);
    }

    private bool NeedsFirstRun() =>
        !_boot.IsSample
        && (!_boot.Settings.Current.Internal.FirstRunSeen || _boot.Cli.ForceFirstTime);

    /// <summary>The wizard as the only window; Finish starts the main window, Cancel quits.</summary>
    private void ShowFirstRun()
    {
        _boot.Log.Info("Showing the first-run wizard");
        ThemeManager.Apply(_boot.Settings.Current.Appearance);
        WindowChrome.SetUseSystemTitleBar(_boot.Settings.Current.Appearance.UseSystemTitleBar);
        var platform = Platform();
        var upstream = UpstreamPaths.UserIni();
        var viewModel = new FirstRunViewModel(_boot.Settings, new FirstRunEnvironment
        {
            SuggestedLibraryFolder = SuggestedLibraryFolder(),
            UpstreamIniPath = File.Exists(upstream) ? upstream : null,
            Credentials = platform.Credentials,
            LegacyCredentials = platform.Credentials as ILegacyCredentialReader,
            Log = _boot.Log,
        });
        var window = new FirstRunWindow(viewModel);
        WindowPositionOverride.Apply(window);
        FirstRunWindow = window;
        viewModel.Completed += (_, accepted) =>
        {
            FirstRunWindow = null;
            viewModel.Dispose();
            if (accepted)
            {
                StartMain(duringStartup: false);
            }
            else
            {
                _boot.Log.Info("First-run setup cancelled; quitting");
                _ = QuitWithoutWindowAsync();
            }
        };
        _desktop.MainWindow = window;
    }

    /// <summary>
    /// The library suggested to a new user: <c>%USERPROFILE%\Blender Launcher Library</c>, outside the data folder so
    /// that removing the launcher's data never touches the builds.
    /// </summary>
    internal static string SuggestedLibraryFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Blender Launcher Library");

    /// <summary>Quits when no main window exists yet (the wizard was cancelled).</summary>
    private async Task QuitWithoutWindowAsync()
    {
        _quitting = true;
        await ShutDownAsync().ConfigureAwait(true);
        _desktop.Shutdown(0);
    }

    /// <summary>Builds the main window and its view model; <paramref name="duringStartup"/> lets Avalonia show it.</summary>
    private void StartMain(bool duringStartup)
    {
        var settings = _boot.Settings.Current;

        ViewModel = _boot.Sample is { } sample
            ? SampleData.CreateMainWindowViewModel(sample.View, sample.Settings, sample.ExtraRows, _boot.Settings)
            : new MainWindowViewModel(_boot.Settings);
        ThemeManager.Apply(settings.Appearance);

        Window = new MainWindow(ViewModel);
        WindowChrome.Apply(Window, Window.AppTitleBar, settings.Appearance.UseSystemTitleBar);
        _placement = new WindowPlacementService(Window);
        _placement.Restore(settings.Internal.Window);
        WindowPositionOverride.Apply(Window);
        WindowChrome.SetUseSystemTitleBar(settings.Appearance.UseSystemTitleBar);
        ViewModel.Sidebar.SettingsRequested += (_, _) => _ = OpenSettingsAsync();
        ViewModel.StatusBar.UpdateRequested += (_, release) => _ = ShowUpdateDialogAsync(release, offered: false);
        ViewModel.Catalog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BuildCatalog.LastCheck))
            {
                OfferUpdate();
            }
        };
        Window.Closing += OnClosing;
        if (!_boot.IsSample && SelfUpdate.UpdatedFrom(_boot.Args) is { } updatedFrom)
        {
            Window.Opened += (_, _) =>
            {
                _boot.Log.Info($"Showing \"Updated to {AppIdentity.Version}\" (from {updatedFrom})");
                ViewModel.ShowToast(Loc.Tr("ui.update.updated", ("version", AppIdentity.Version)));
            };
        }

        _boot.Settings.Changed +=UiDispatcher.Marshal<SettingsChangedEventArgs>(OnSettingsChanged);
        InstanceRequestHub.Subscribe(request => UiDispatcher.Post(() => OnInstanceRequest(request)));

        if (!_boot.IsSample)
        {
            _tray = new TrayController(this, _boot.Log);
            _tray.Attach(ViewModel.Catalog);
            _tray.SetEnabled(settings.General.MinimiseToTray);
            _notifications = new NotificationRouter(
                () => _boot.Settings.Current.Appearance,
                () => Window.IsVisible && Window.WindowState != WindowState.Minimized,
                () => _tray?.IsActive == true,
                ViewModel.ShowToast,
                (text, icon) => _tray?.ShowBalloonAsync(text, icon) ?? Task.FromResult(false));
            StartIntegration(settings);
        }

        // Upstream (window.py:475): start hidden when both tray settings are on, and only with a tray to come back from.
        var startHidden = settings.General is { MinimiseToTray: true, LaunchMinimisedToTray: true } && _tray?.IsActive == true;

        if (!_boot.IsSample)
        {
            // Show the window first; start the services once its first frame is on screen.
            Window.Opened += (_, _) => Window.RequestAnimationFrame(_ =>
                Dispatcher.UIThread.Post(StartServices, DispatcherPriority.Background));
            if (startHidden)
            {
                Dispatcher.UIThread.Post(StartServices, DispatcherPriority.Background);
            }
        }

        if (_boot.Args.Contains(DevThrowSwitch, StringComparer.OrdinalIgnoreCase))
        {
            // Hidden: throws on the UI thread once the window is up, to check the unhandled-error window.
            Window.Opened += (_, _) => Dispatcher.UIThread.Post(
                () => throw new InvalidOperationException($"Test exception from {DevThrowSwitch}"), DispatcherPriority.Background);
        }

        if (startHidden)
        {
            // Avalonia shows the lifetime's main window when it starts; set it afterwards so the window stays hidden.
            _boot.Log.Info("Starting hidden in the tray");
            Dispatcher.UIThread.Post(() => _desktop.MainWindow = Window);
            return;
        }

        _desktop.MainWindow = Window;
        if (!duringStartup)
        {
            Window.Show();
        }
    }

    /// <summary>Brings the window to the front: shown, restored from minimised, activated.</summary>
    public void ShowWindow()
    {
        if (Window is null)
        {
            return;
        }

        if (!Window.IsVisible)
        {
            Window.Show();
        }

        if (Window.WindowState == WindowState.Minimized)
        {
            Window.WindowState = WindowState.Normal;
        }

        // Off-screen test runs (--window-pos) never take the foreground.
        if (!WindowPositionOverride.IsActive)
        {
            Window.Activate();
        }
    }

    /// <summary>Tray "Favorites": the window on the Favorites view.</summary>
    public void ShowFavorites()
    {
        ShowWindow();
        ViewModel.Navigate(SidebarSection.Installed, SidebarView.FavoritesKey);
    }

    /// <summary>
    /// Hides the window to the tray (close button, tray "Hide", on-launch "close"). The first time, a quiet balloon
    /// says the launcher is still running (upstream's <c>tray_icon_notified</c>).
    /// </summary>
    public void HideToTray()
    {
        if (Window is null || _tray?.IsActive != true)
        {
            return;
        }

        SavePlacement();
        Window.Hide();
        if (!_boot.Settings.Current.Internal.TrayNoticeShown)
        {
            _ = _tray.ShowBalloonAsync(Loc.Tr("ui.tray.hidden_notice"), TrayBalloonIcon.Info, silent: true);
            _boot.Settings.Update(s => s with { Internal = s.Internal with { TrayNoticeShown = true } });
        }
    }

    /// <summary>Tray entries: launch a build.</summary>
    public void Launch(InstalledBuildRowViewModel row) => ViewModel.Actions.Launch(row);

    /// <summary>Middle click and the tray's first entry: the default build; without one, the window says how to set it.</summary>
    public void LaunchDefault()
    {
        if (ViewModel.Catalog.DefaultBuild is { IsRecognised: true } row)
        {
            Launch(row);
            return;
        }

        ShowWindow();
        _ = DialogService.InfoAsync(Loc.Tr("ui.tray.no_default"), Loc.Tr("ui.tray.no_default_message"), Window);
    }

    /// <summary>
    /// The shell integration: registers the hotkey, refreshes the Add/Remove Programs entry and
    /// carries out the wizard's pending shortcuts and file association once, after the window is up.
    /// </summary>
    private void StartIntegration(AppSettings settings)
    {
        _integration = new ShellIntegration(Platform(), _boot.Settings, _boot.Log);
        _integration.HotkeyPressed += (_, _) => UiDispatcher.Post(OnHotkeyPressed);
        Dispatcher.UIThread.Post(async () =>
        {
            if (_quitting || _integration is not { } integration)
            {
                return;
            }

            ReportHotkey(await integration.ApplyHotkeyAsync(settings.Launching).ConfigureAwait(true));
            await Task.Run(() => integration.SyncAppRegistration(_boot.Settings.Current.General.RegisterInAddRemovePrograms)).ConfigureAwait(true);
            if (await integration.ApplyPendingAsync(ConfirmReplaceShortcutAsync).ConfigureAwait(true) is { } failed)
            {
                ViewModel.ShowToast(failed);
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>Asks before a launcher shortcut replaces one that starts something else (upstream's has the same name).</summary>
    internal Task<bool> ConfirmReplaceShortcutAsync(string path) => DialogService.ConfirmAsync(
        Loc.Tr("ui.integration.replace_title"),
        Loc.Tr("ui.integration.replace_message", ("path", path)),
        Loc.Tr("ui.integration.replace"),
        Loc.Tr("ui.integration.keep"),
        owner: _settingsWindow ?? (Window is { IsVisible: true } w ? (Avalonia.Controls.Window?)w : null));

    /// <summary>The hotkey: the default build, as the tray's middle click; ignored while Settings or the wizard is open.</summary>
    private void OnHotkeyPressed()
    {
        if (_quitting || _settingsWindow is not null || FirstRunWindow is not null || Window is null)
        {
            _boot.Log.Debug("Quick-launch hotkey ignored (Settings open)");
            return;
        }

        _boot.Log.Info("Quick-launch hotkey pressed");
        LaunchDefault();
    }

    /// <summary>A hotkey failure: a dialog over Settings while it is open (the user just changed it), else a toast.</summary>
    private void ReportHotkey(string? failure)
    {
        if (failure is null)
        {
            return;
        }

        if (_settingsWindow is { } settings)
        {
            _ = DialogService.WarningAsync(Loc.Tr("ui.integration.hotkey_title"), failure, settings);
            return;
        }

        ViewModel.ShowToast(failure);
    }

    /// <summary>Quits: confirm (hook), save the placement, hide, stop the services in order, end the process.</summary>
    public async Task QuitAsync()
    {
        if (_quitting || _confirmingQuit)
        {
            return;
        }

        _confirmingQuit = true;
        try
        {
            if (!await ConfirmQuitAsync().ConfigureAwait(true))
            {
                return;
            }
        }
        finally
        {
            _confirmingQuit = false;
        }

        _quitting = true;
        SavePlacement();
        Window.Hide();
        await ShutDownAsync().ConfigureAwait(true);
        _desktop.Shutdown(0);
    }

    /// <summary>
    /// Upstream <c>quit_</c>: while downloads or installs are queued or running, lists them and asks; true to
    /// quit. Over the window when it is on screen, otherwise (tray "Quit") on its own.
    /// </summary>
    private async Task<bool> ConfirmQuitWhileBusyAsync()
    {
        var tasks = BusyTasks();
        if (tasks.Count == 0)
        {
            return true;
        }

        var message = Loc.Tr("ui.quit.message") + "\n\n" + string.Join("\n", tasks.Select(t => "•  " + t)) + "\n\n" + Loc.Tr("ui.quit.question");
        var answer = await DialogService.ShowAsync(new DialogRequest
        {
            Kind = DialogKind.Warning,
            Title = Loc.Tr("ui.quit.title"),
            Message = message,
            KeepLineBreaks = true,
            Buttons =
            [
                new PromptButton(Loc.Tr("act.quit"), IsDanger: true),
                new PromptButton(Loc.Tr("act.cancel"), IsPrimary: true, IsCancel: true),
            ],
        }, Window is { IsVisible: true } window && window.WindowState != WindowState.Minimized ? window : null).ConfigureAwait(true);
        _boot.Log.Info($"Quit with {tasks.Count} task(s) running: {(answer == 0 ? "confirmed" : "cancelled")}");
        return answer == 0;
    }

    /// <summary>The tasks quitting would cancel, as text ("Blender 4.2 LTS (4.2.3) · 42%"): the catalog's busy rows.</summary>
    internal IReadOnlyList<string> BusyTasks()
    {
        var rows = ViewModel?.Catalog.Busy.ToList() ?? [];
        if (rows.Count > 0)
        {
            return rows.Select(r => r.IsIndeterminate || r.Progress <= 0
                    ? $"{r.Name} ({r.Version})"
                    : $"{r.Name} ({r.Version}) · {Math.Clamp(r.Progress, 0, 1):P0}")
                .ToArray();
        }

        return _services?.Install.Jobs.Select(j => j.Build.Build.DisplayVersion).ToArray() ?? [];
    }

    /// <summary>
    /// Opens Settings modal over the main window, or brings it forward when open. On close, "Restart now"
    /// restarts the app (<see cref="RestartAsync"/>).
    /// </summary>
    public async Task OpenSettingsAsync()
    {
        if (_settingsWindow is { } open)
        {
            open.Activate();
            return;
        }

        ShowWindow();
        var credentials = _boot.IsSample ? SampleCredentials : Platform().Credentials;
        using var viewModel = new SettingsViewModel(_boot.Settings, credentials, new SettingsEnvironment
        {
            Services = _services,
            Directories = _boot.IsSample ? null : _boot.Directories,
            Platform = _boot.IsSample ? null : Platform(),
            ExePath = LauncherExe.Path,
            ConfirmReplaceShortcut = ConfirmReplaceShortcutAsync,
        });
        var window = new SettingsWindow(viewModel);
        WindowPositionOverride.Apply(window);
        _settingsWindow = window;
        try
        {
            await window.ShowDialog(Window).ConfigureAwait(true);
        }
        finally
        {
            _settingsWindow = null;
        }

        if (window.RestartRequested)
        {
            await RestartAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// "Restart now": saves the settings, stops the services, releases the single-instance pipe, starts a new
    /// copy with the same non-command arguments (<see cref="AppRestart"/>) and exits. The new copy waits for the pipe.
    /// </summary>
    public async Task RestartAsync()
    {
        if (_quitting || _confirmingQuit)
        {
            return;
        }

        // A restart cancels running downloads just as quitting does.
        _confirmingQuit = true;
        try
        {
            if (!await ConfirmQuitAsync().ConfigureAwait(true))
            {
                return;
            }
        }
        finally
        {
            _confirmingQuit = false;
        }

        _quitting = true;
        _boot.Log.Info("Restarting to apply settings");
        SavePlacement();
        Window.Hide();
        await ShutDownAsync().ConfigureAwait(true);
        AppRestart.StartNewCopy(_boot.Args);
        _desktop.Shutdown(0);
    }

    // Sample runs never touch Windows Credential Manager.
    private static readonly InMemoryCredentialStore SampleCredentials = new();

    /// <summary>The platform services, created once: Settings may need the credential store before the services start.</summary>
    private PlatformServiceBundle Platform() => _platform ??= WindowsPlatformServices.Create();

    private void StartServices()
    {
        if (_quitting || _servicesStart is not null)
        {
            return;
        }

        var options = new AppServicesOptions
        {
            Directories = _boot.Directories,
            Settings = _boot.Settings,
            Log = _boot.Log,
            LogLevel = _boot.Cli.Debug ? Core.Logging.LogLevel.Debug : null,
            Platform = Platform(),
            Offline = _boot.Cli.Offline,
        };
        _servicesStart = Task.Run(() => AppServices.StartAsync(options));
        _ = AttachServicesAsync(_servicesStart);
    }

    private async Task AttachServicesAsync(Task<AppServices> start)
    {
        try
        {
            var services = await start.ConfigureAwait(true);
            if (_quitting)
            {
                return;
            }

            _services = services;
            ViewModel.AttachServices(services);
            _notifications?.Attach(services, ViewModel);
            services.Launches.Launched += UiDispatcher.Marshal<BuildLaunchedEventArgs>(OnLaunched);
        }
        catch (Exception e)
        {
            _boot.Log.Error("The services could not be started", e);
            ViewModel.ShowToast(Loc.Tr("ui.toast.start_failed"));
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting)
        {
            return;
        }

        if (e.CloseReason is WindowCloseReason.OSShutdown)
        {
            // Windows is ending the session: let it close; OnExit saves and stops what it can.
            _quitting = true;
            SavePlacement();
            return;
        }

        e.Cancel = true;
        if (_boot.Settings.Current.General.MinimiseToTray && CanHideToTray())
        {
            HideToTray();
            return;
        }

        _ = QuitAsync();
    }

    /// <summary>
    /// The on-launch action (Settings → Blender Builds, upstream <c>on_blender_launch_action</c>), which the launch
    /// service reports only when a build goes from none to one running process: minimise the window, or close it
    /// (to the tray when that is on, else quit, asking first while downloads run).
    /// </summary>
    private void OnLaunched(object? sender, BuildLaunchedEventArgs e)
    {
        switch (e.Action)
        {
            case OnLaunchAction.Minimise when Window.IsVisible:
                Window.WindowState = WindowState.Minimized;
                break;
            case OnLaunchAction.Close when _boot.Settings.Current.General.MinimiseToTray && CanHideToTray():
                HideToTray();
                break;
            case OnLaunchAction.Close:
                _ = QuitAsync();
                break;
        }
    }

    /// <summary>After a check: offers a new launcher release once per version, when the window is on screen.</summary>
    private void OfferUpdate()
    {
        if (_updateDialogOpen || _settingsWindow is not null || !Window.IsVisible
            || UpdateDialog.ToOffer(ViewModel.Catalog.LastCheck?.LauncherUpdate, _boot.Settings.Current.Internal.DismissedUpdateVersion) is not { } release)
        {
            return;
        }

        _ = ShowUpdateDialogAsync(release, offered: true);
    }

    /// <summary>The update dialog for <paramref name="release"/>; remembers the version so a check offers it only once.</summary>
    private async Task ShowUpdateDialogAsync(AppRelease release, bool offered)
    {
        if (_updateDialogOpen)
        {
            return;
        }

        _updateDialogOpen = true;
        try
        {
            ShowWindow();
            _boot.Settings.Update(s => s with { Internal = s.Internal with { DismissedUpdateVersion = release.Tag } });
            var notes = ViewModel.Catalog.LastCheck?.LauncherUpdate?.Notes ?? [];
            _boot.Log.Info($"Update {release.Tag} {(offered ? "offered after a check" : "requested from the status bar")}");
            if (await UpdateDialog.ShowAsync(release, notes, Window).ConfigureAwait(true))
            {
                await SelfUpdateAsync(release).ConfigureAwait(true);
            }
        }
        finally
        {
            _updateDialogOpen = false;
        }
    }

    /// <summary>
    /// The self-update: refused while downloads or installs run; a copy Velopack does not manage is
    /// offered the release page instead. Otherwise Velopack downloads the release behind the progress window, then this
    /// process shuts down and Velopack's Update.exe installs it and starts the new version, which says "Updated to X".
    /// </summary>
    private async Task SelfUpdateAsync(AppRelease release)
    {
        if (BusyTasks().Count > 0)
        {
            await DialogService.WarningAsync(Loc.Tr("ui.update.progress_title"), Loc.Tr("ui.update.busy"), Window).ConfigureAwait(true);
            return;
        }

        var preReleases = _boot.Settings.Current.General.UsePreReleaseBuilds;
        if (_services is not { } services
            || SelfUpdate.CreateManager(_boot.Args, () => services.Http.Client, preReleases, _boot.Log) is not { } manager)
        {
            await SelfUpdate.ShowNotManagedAsync(release.Tag, Window).ConfigureAwait(true);
            return;
        }

        var version = release.Version.ToString();
        _boot.Log.Info($"Self-update to {release.Tag} started");
        Velopack.VelopackAsset? package = null;
        var result = await UpdateProgressWindow.RunAsync(Window, version, async (progress, ct) =>
        {
            (var outcome, package) = await SelfUpdate.PrepareAsync(manager, progress, _boot.Log, ct).ConfigureAwait(false);
            return outcome;
        }).ConfigureAwait(true);
        if (result.Outcome == UpdateOutcome.Cancelled)
        {
            return;
        }

        if (result.Outcome == UpdateOutcome.UpToDate)
        {
            await DialogService.InfoAsync(Loc.Tr("ui.update.progress_title"), Loc.Tr("ui.update.up_to_date", ("version", AppIdentity.Version)), Window)
                .ConfigureAwait(true);
            return;
        }

        if (!result.Succeeded || package is null)
        {
            await DialogService.ErrorAsync(Loc.Tr("ui.update.failed_title"), Loc.Tr("ui.update.failed", ("version", version)),
                result.Error, _boot.Directories.Logs, Window).ConfigureAwait(true);
            return;
        }

        if (!SelfUpdate.ApplyAndRestart(manager, package, _boot.Args, _boot.Log))
        {
            await DialogService.ErrorAsync(Loc.Tr("ui.update.failed_title"), Loc.Tr("ui.update.apply_failed", ("version", version)),
                null, _boot.Directories.Logs, Window).ConfigureAwait(true);
            return;
        }

        // As "Restart now", but nothing to confirm: no downloads were running. Update.exe waits for this exit.
        _quitting = true;
        SavePlacement();
        Window.Hide();
        await ShutDownAsync().ConfigureAwait(true);
        _desktop.Shutdown(0);
    }

    private void OnInstanceRequest(InstanceRequest request)
    {
        _boot.Log.Info($"Second launch (v{request.Version}) asked to {request.Command}{(request.Path is null ? "" : " " + request.Path)}");
        if (request is { Command: InstanceCommand.Open, Path: { } path } && _servicesStart is { } start && !_quitting)
        {
            // A double-clicked .blend while the launcher runs: the picker opens here, over this process's services,
            // so settings (a saved search) and launch tracking stay in the one process that owns them.
            OpenLaunchPicker(new LaunchPickerRequest(path), start);
            OpenFileRequested?.Invoke(this, path);
            return;
        }

        ShowWindow();
    }

    /// <summary>Opens the launch picker as its own window; the main window is left as it is.</summary>
    private void OpenLaunchPicker(LaunchPickerRequest request, Task<AppServices> services)
    {
        var host = new Cli.ServicesLaunchPickerHost(ct => services.WaitAsync(ct), owns: false);
        var viewModel = new LaunchPickerViewModel(host, request);
        var window = new Views.Dialogs.LaunchPickerWindow(viewModel);
        if (!WindowPositionOverride.Apply(window))
        {
            window.Opened += (_, _) => window.Activate();
        }

        window.Show();
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.General.MinimiseToTray != e.Current.General.MinimiseToTray)
        {
            _tray?.SetEnabled(e.Current.General.MinimiseToTray);
        }

        if (_integration is { } integration)
        {
            var launching = e.Current.Launching;
            if (e.Previous.Launching.QuickLaunchHotkeyEnabled != launching.QuickLaunchHotkeyEnabled
                || (launching.QuickLaunchHotkeyEnabled && e.Previous.Launching.QuickLaunchHotkey != launching.QuickLaunchHotkey))
            {
                _ = Task.Run(() => integration.ApplyHotkeyAsync(launching))
                    .ContinueWith(t => UiDispatcher.Post(() => ReportHotkey(t.Result)), TaskContinuationOptions.OnlyOnRanToCompletion);
            }

            if (e.Previous.General.RegisterInAddRemovePrograms != e.Current.General.RegisterInAddRemovePrograms)
            {
                var wanted = e.Current.General.RegisterInAddRemovePrograms;
                _ = Task.Run(() => integration.SyncAppRegistration(wanted));
            }
        }

        var before = e.Previous.Appearance;
        var after = e.Current.Appearance;
        if (before == after)
        {
            return;
        }

        ThemeManager.Apply(after);
        ViewModel.ApplyAppearance(after);
        if (before.UseSystemTitleBar != after.UseSystemTitleBar)
        {
            WindowChrome.Apply(Window, Window.AppTitleBar, after.UseSystemTitleBar);
            WindowChrome.SetUseSystemTitleBar(after.UseSystemTitleBar);
        }
    }

    private void SavePlacement()
    {
        if (_placement is null || WindowPositionOverride.IsActive || !Window.IsVisible)
        {
            return;
        }

        try
        {
            _boot.Settings.Update(s => s with
            {
                Internal = s.Internal with { Window = _placement.Capture(s.Internal.Window) },
            });
        }
        catch (ObjectDisposedException)
        {
            // The services failed to start and took the store with them; nothing to save to.
        }
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (Volatile.Read(ref _shutDown) != 0)
        {
            return;
        }

        // Reached without QuitAsync (OS session end): stop everything, waiting at most ExitTimeout.
        // The shutdown runs on the thread pool, so blocking the UI thread here cannot deadlock it.
        try
        {
            Task.Run(ShutDownAsync).Wait(ExitTimeout);
        }
        catch (AggregateException ex)
        {
            _boot.Log.Error("Shutting down failed", ex);
        }
    }

    /// <summary>Stops in order: services (which flush the settings and library state), pipe, log.</summary>
    private async Task ShutDownAsync()
    {
        if (Interlocked.Exchange(ref _shutDown, 1) != 0)
        {
            return;
        }

        InstanceRequestHub.Unsubscribe();
        // Removes the icon (the tray thread does the Win32 work), so none is left behind in the notification area.
        _tray?.Dispose();
        _integration?.Dispose();
        var log = _boot.Log;
        AppServices? services = _services;
        if (services is null && _servicesStart is { } start)
        {
            try
            {
                services = await start.WaitAsync(ExitTimeout).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Start failed (and disposed the settings store) or is hanging: nothing more to stop.
                log.Warning("The services had not started when the launcher exited", e);
            }
        }

        try
        {
            if (services is not null)
            {
                await services.DisposeAsync().ConfigureAwait(false);
            }
            else if (_servicesStart is null)
            {
                await _boot.Settings.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            log.Error("Stopping the services failed", e);
        }

        if (_boot.Pipe is { } pipe)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }

        log.Info($"{AppIdentity.DisplayName} exited");
        ErrorHandling.Log = null;
        await log.DisposeAsync().ConfigureAwait(false);
    }
}
