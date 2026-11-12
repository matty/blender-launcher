using System.Collections.Concurrent;
using System.Globalization;
using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Localisation;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Pipeline;
using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Core.Processes;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Core.Updates;

namespace BlenderLauncher.Core;

/// <summary>What <see cref="AppServices.StartAsync"/> builds from.</summary>
public sealed record AppServicesOptions
{
    /// <summary>The data folders; <see cref="DataDirectories.Resolve"/> (portable-aware) when null.</summary>
    public DataDirectories? Directories { get; init; }

    /// <summary>
    /// A settings store to use instead of loading <see cref="DataDirectories.SettingsFile"/> (tests, tools; for
    /// example <see cref="SettingsStore.InMemory"/>). It is disposed with the services.
    /// </summary>
    public SettingsStore? Settings { get; init; }

    /// <summary>The platform services; every one a stand-in by default.</summary>
    public PlatformServiceBundle Platform { get; init; } = PlatformServiceBundle.None;

    /// <summary>
    /// The library to open instead of <see cref="GeneralSettings.LibraryFolder"/> (the developer commands). It is not
    /// written to the settings.
    /// </summary>
    public string? LibraryOverride { get; init; }

    /// <summary>Starts the scrape service (start-up check and timer, per the settings). Off for tools that check on demand.</summary>
    public bool StartScraping { get; init; } = true;

    /// <summary>
    /// <c>--offline</c>: no build checks and no launcher update checks for this session; the scrape service is not
    /// started and <see cref="AppServices.IsOffline"/> tells the UI to refuse manual checks.
    /// </summary>
    public bool Offline { get; init; }

    /// <summary>Scans the library during start. Off only in tests.</summary>
    public bool ScanLibrary { get; init; } = true;

    /// <summary>
    /// A log opened by the caller (the app opens it before the window, for its error handlers). It is not disposed with
    /// the services. When null, the services open <see cref="DataDirectories.Logs"/> themselves and own it.
    /// </summary>
    public Log? Log { get; init; }

    /// <summary>The lowest level written to the log; the <c>log_level</c> setting when null.</summary>
    public LogLevel? LogLevel { get; init; }

    /// <summary>The UI culture for <c>language = auto</c>; the current UI culture when null.</summary>
    public CultureInfo? UiCulture { get; init; }

    /// <summary>The primary HTTP handler factory (tests); a real <see cref="SocketsHttpHandler"/> when null.</summary>
    public Func<ConnectionOptions, HttpMessageHandler>? HttpHandlerFactory { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// The composition root: every Core service, created once, wired together, with no DI container
/// and no Avalonia. <see cref="StartAsync"/> builds and starts them; <see cref="DisposeAsync"/> stops them in
/// order and flushes the settings, the library sidecar and the log.
/// </summary>
/// <remarks>
/// Wiring:
/// <list type="bullet">
/// <item>Settings changes: connection settings rebuild the HTTP client; build-check settings re-arm the scrape timer
/// (the scrape service reads <see cref="SettingsMappings.ToScrapeSettings"/> afresh at each check); a new library
/// folder is scanned; the log level follows <c>log_level</c>. Secrets live in the credential store, so after
/// changing one call <see cref="RebuildHttpClient"/>.</item>
/// <item>A finished install is added to the library (installed-on stamped) and, per
/// <see cref="BuildsSettings.MarkNewDownloadsAsDefault"/>, becomes the default build.</item>
/// <item>After each check, and after library changes, the update list is recomputed; after each check,
/// replace-on-new-build runs.</item>
/// <item>The first scan of a library moves an imported default build into the library state
/// (<see cref="DefaultBuildImport"/>).</item>
/// </list>
/// </remarks>
public sealed class AppServices : IAsyncDisposable
{
    private readonly AppServicesOptions _options;
    private readonly bool _ownsLog;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<InstallJob, Lazy<Task<LibraryEntry?>>> _additions = new();
    private readonly Lock _gate = new();
    private readonly List<Task> _background = [];
    private bool _measurePending;
    private bool _measureRunning;
    private string? _libraryRoot;
    private int _disposed;

#pragma warning disable CS8618 // Every service is assigned in StartAsync before the instance is returned.
    private AppServices(AppServicesOptions options, DataDirectories directories, Log log, bool ownsLog)
    {
        _options = options;
        Directories = directories;
        Log = log;
        _ownsLog = ownsLog;
        Platform = options.Platform;
    }
#pragma warning restore CS8618

    public DataDirectories Directories { get; }

    public Log Log { get; }

    public PlatformServiceBundle Platform { get; }

    public SettingsStore Settings { get; private set; }

    public ICredentialStore Credentials => Platform.Credentials;

    public Translator Translator { get; private set; }

    public HttpClientProvider Http { get; private set; }

    public ApiDataService ApiData { get; private set; }

    public AppReleaseService Releases { get; private set; }

    public ScrapeService Scrape { get; private set; }

    public LibraryState LibraryState { get; private set; }

    public BuildInfoReader BuildInfoReader { get; private set; }

    public LibraryService Library { get; private set; }

    public TaskQueue Queue { get; private set; }

    public Downloader Downloader { get; private set; }

    public Extractor Extractor { get; private set; }

    public Renamer Renamer { get; private set; }

    public Remover Remover { get; private set; }

    public InstallService Install { get; private set; }

    public LaunchService Launches { get; private set; }

    public BuildUpdateService Updates { get; private set; }

    public ReplaceOnNewBuild ReplaceOnNewBuild { get; private set; }

    /// <summary><see cref="AppServicesOptions.Offline"/>: checks are off for this session.</summary>
    public bool IsOffline => _options.Offline;

    /// <summary>The library in use (the override or the setting), or null when none is set.</summary>
    public string? LibraryRoot
    {
        get
        {
            lock (_gate)
            {
                return _libraryRoot;
            }
        }
    }

    /// <summary>
    /// The start-up scrape (when <see cref="AppServicesOptions.StartScraping"/>): completes with the first check's
    /// snapshot, or null when no check runs at start. Never faults.
    /// </summary>
    public Task<ScrapeSnapshot?> StartupScrape { get; private set; } = Task.FromResult<ScrapeSnapshot?>(null);

    /// <summary>Builds every service, loads the settings and the library, and starts the scrape service.</summary>
    public static async Task<AppServices> StartAsync(AppServicesOptions? options = null, CancellationToken ct = default)
    {
        options ??= new AppServicesOptions();
        var directories = options.Directories ?? DataDirectories.Resolve();
        var log = options.Log ?? Log.Open(directories.Logs, options.LogLevel ?? LogLevel.Info);
        var services = new AppServices(options, directories, log, ownsLog: options.Log is null);
        try
        {
            await services.BuildAsync(ct).ConfigureAwait(false);
            return services;
        }
        catch
        {
            await services.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Installs <paramref name="build"/> (or joins its running job) and returns its library entry once added;
    /// null when the job failed or was cancelled.
    /// </summary>
    public async Task<LibraryEntry?> InstallAsync(AvailableBuild build, CancellationToken ct = default)
    {
        var job = Install.Install(build);
        var state = await job.Completion.WaitAsync(ct).ConfigureAwait(false);
        if (state != InstallState.Done)
        {
            return null;
        }

        return await Addition(job).WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Rebuilds the HTTP client from the settings and the credential store (after a secret changed).</summary>
    public void RebuildHttpClient()
    {
        try
        {
            Http.Rebuild(Settings.Current.ToConnectionOptions(Credentials, Log));
        }
        catch (ArgumentException e)
        {
            Log.Error("The connection settings are invalid; the HTTP client was not rebuilt", e);
        }
    }

    /// <summary>
    /// Stops everything in order: replace-on-new-build, launch tracking, the scrape service and the task queue
    /// (running installs are cancelled), then flushes the library sidecar and the settings, and closes the log.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Settings is not null)
        {
            Settings.Changed -= OnSettingsChanged;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await Step("replace-on-new-build", () => ReplaceOnNewBuild?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
        await Step("launch service", () => Launches?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
        await Step("scrape service", () => Scrape?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
        await Step("task queue", () => Queue?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
        await Step("background work", async () =>
        {
            Task[] pending;
            lock (_gate)
            {
                pending = [.. _background];
            }

            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await Step("library service", () =>
        {
            Library?.Dispose();
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        await Step("library state", () => LibraryState?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
        await Step("settings", () => Settings?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
        await Step("HTTP client", () =>
        {
            Http?.Dispose();
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        Log.Info("Services stopped");
        if (_ownsLog)
        {
            await Log.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }

    private async Task BuildAsync(CancellationToken ct)
    {
        var options = _options;
        var platform = options.Platform;
        Log.Info($"{AppIdentity.DisplayName} {AppIdentity.Version} starting; data in {Directories.Root}{(Directories.IsPortable ? " (portable)" : "")}");

        Settings = options.Settings ?? await SettingsStore.LoadAsync(new SettingsStoreOptions
        {
            FilePath = Directories.SettingsFile,
            Log = Log,
            Time = options.Time,
        }, ct).ConfigureAwait(false);
        var settings = Settings.Current;
        if (options.LogLevel is null)
        {
            Log.MinimumLevel = settings.General.LogLevel;
        }

        Translator = Translator.Create(settings.General.Language, options.UiCulture ?? CultureInfo.CurrentUICulture);

        var connection = settings.ToConnectionOptions(platform.Credentials, Log);
        Http = options.HttpHandlerFactory is { } factory
            ? new HttpClientProvider(connection, Log, factory)
            : new HttpClientProvider(connection, Log);
        ApiData = new ApiDataService(Directories, Http, Log);
        await ApiData.LoadAsync(ct).ConfigureAwait(false);
        Releases = new AppReleaseService(Http, Log);

        LibraryState = await LibraryState.LoadAsync(Directories.State, options.Time, Log, ct).ConfigureAwait(false);
        BuildInfoReader = new BuildInfoReader(platform.VersionProbe, ApiData, Log, options.Time);
        Library = new LibraryService(BuildInfoReader, LibraryState, Log, options.Time);

        Queue = new TaskQueue(settings.General.EffectiveWorkerThreadCount(), Log);
        Downloader = new Downloader(() => Http.Client, log: Log);
        Extractor = new Extractor(platform.RecycleBin, log: Log);
        Renamer = new Renamer(platform.RecycleBin, log: Log);
        Remover = new Remover(platform.RecycleBin, log: Log);
        Install = new InstallService(new InstallServiceOptions
        {
            Paths = () => InstallPaths.For(LibraryRoot ?? throw new InvalidOperationException("No library folder is set")),
            ReadBuildInfo = (request, token) => BuildInfoReader.ReadAsync(request.Folder, request.ArchiveName, request.Seed, autoWrite: true, token),
            InstallTemplate = () => Settings.Current.Builds.InstallTemplate,
        }, Queue, Downloader, Extractor, Renamer, Log);
        Install.JobStateChanged += OnJobStateChanged;

        Scrape = ScrapeService.Create(
            Http,
            Directories,
            () => Settings.Current.ToScrapeSettings(),
            ApiData,
            Releases,
            new SettingsScrapeStateStore(Settings, Directories, Log),
            Log);

        Launches = new LaunchService(platform.ProcessLauncher, Library, () => Settings.Current.Launching, Log, options.Time);
        Updates = new BuildUpdateService(() => Settings.Current.Builds, Library, Log);
        ReplaceOnNewBuild = new ReplaceOnNewBuild(
            Library,
            Updates,
            Launches,
            InstallAsync,
            Remover,
            () => Settings.Current.General.DefaultDeleteAction == DeleteAction.SendToTrash,
            Log);

        Scrape.CheckCompleted += OnCheckCompleted;
        Library.Changed += OnLibraryChanged;
        Settings.Changed += OnSettingsChanged;

        var root = options.LibraryOverride ?? settings.General.LibraryFolder;
        if (root is not null)
        {
            await OpenLibraryAsync(root, purge: settings.General.PurgeTempOnStartup, ct).ConfigureAwait(false);
        }
        else
        {
            Log.Info("No library folder is set yet");
        }

        if (options.Offline)
        {
            Log.Info("Offline: build and launcher update checks are off for this session");
        }
        else if (options.StartScraping)
        {
            StartupScrape = StartScrapingAsync();
        }

        Log.Info("Services started");
    }

    private async Task OpenLibraryAsync(string folder, bool purge, CancellationToken ct)
    {
        // A library saved relative to the exe folder (upstream's option, offered by Settings) is resolved against it.
        var layout = new LibraryLayout(Path.IsPathFullyQualified(folder) ? folder : LibraryLayout.Resolve(folder, AppLocation.BaseDirectory));
        lock (_gate)
        {
            _libraryRoot = layout.Root;
        }

        if (!Directory.Exists(layout.Root))
        {
            Log.Warning($"Library folder {layout.Root} does not exist");
            return;
        }

        if (purge && !Install.Jobs.Any())
        {
            try
            {
                await Remover.PurgeStagingAsync(layout.StagingFolder, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PipelineException)
            {
                Log.Warning($"Could not purge {layout.StagingFolder}", e);
            }
        }

        if (_options.ScanLibrary)
        {
            await Library.ScanAsync(layout.Root, ct: ct).ConfigureAwait(false);
            DefaultBuildImport.Reconcile(Library, Settings, Log);
        }
    }

    private async Task<ScrapeSnapshot?> StartScrapingAsync()
    {
        try
        {
            return await Scrape.StartAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
        catch (Exception e)
        {
            Log.Error("The start-up check failed", e);
            return null;
        }
    }

    private void OnCheckCompleted(object? sender, ScrapeSnapshot snapshot)
    {
        Updates.Refresh(snapshot.Builds);
        Track(RunReplaceAsync(snapshot.Builds));
    }

    private async Task RunReplaceAsync(IReadOnlyList<AvailableBuild> available)
    {
        try
        {
            var replaced = await ReplaceOnNewBuild.RunAsync(available, _lifetime.Token).ConfigureAwait(false);
            if (replaced.Count > 0)
            {
                Log.Info($"Replace on new build: {replaced.Count} build(s) replaced");
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception e)
        {
            Log.Error("Replace on new build failed", e);
        }
    }

    private void OnLibraryChanged(object? sender, LibraryChangedEventArgs e)
    {
        if (e.Kind is LibraryChangeKind.Added or LibraryChangeKind.Removed or LibraryChangeKind.Updated or LibraryChangeKind.ScanCompleted)
        {
            Updates.Refresh();
        }

        if (e.Kind is LibraryChangeKind.Added or LibraryChangeKind.Updated or LibraryChangeKind.ScanCompleted)
        {
            RequestMeasure();
        }
    }

    // Folder sizes and Python versions are filled in after each scan and each new or changed build:
    // one background pass at a time, rerun once if more changes arrive while it runs. Known sizes are not measured
    // again, so a pass after an install measures only the new build.
    private void RequestMeasure()
    {
        lock (_gate)
        {
            _measurePending = true;
            if (_measureRunning || _lifetime.IsCancellationRequested)
            {
                return;
            }

            _measureRunning = true;
        }

        Track(Task.Run(MeasureLoopAsync));
    }

    private async Task MeasureLoopAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_measurePending || _lifetime.IsCancellationRequested)
                {
                    _measureRunning = false;
                    return;
                }

                _measurePending = false;
            }

            try
            {
                if (Library.LibraryRoot is not null)
                {
                    await Library.MeasureAsync(ct: _lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                Log.Warning("Measuring the build folders failed", e);
            }
        }
    }

    private void OnJobStateChanged(InstallJob job)
    {
        if (job.State == InstallState.Done)
        {
            Track(Addition(job));
        }
    }

    // One library addition per finished job, however many callers ask.
    private Task<LibraryEntry?> Addition(InstallJob job) =>
        _additions.GetOrAdd(job, j => new Lazy<Task<LibraryEntry?>>(() => AddInstalledAsync(j))).Value;

    // After an install: add it to the library (stamping installed-on) and apply "mark new downloads as default".
    private async Task<LibraryEntry?> AddInstalledAsync(InstallJob job)
    {
        if (job.InstalledFolder is not { } folder)
        {
            return null;
        }

        try
        {
            var entry = await Library.AddAsync(folder, job.InstalledBuild, newInstall: true).ConfigureAwait(false);
            if (entry.Build is { } build && MarksAsDefault(Settings.Current.Builds.MarkNewDownloadsAsDefault, build.Branch))
            {
                Library.SetDefaultBuild(entry.Path);
                Log.Info($"{entry.Path} is now the default build");
            }

            return entry;
        }
        catch (Exception e)
        {
            Log.Error($"Could not add the installed build {folder} to the library", e);
            return null;
        }
    }

    /// <summary>Whether a new download of <paramref name="branch"/> becomes the default build under <paramref name="setting"/>.</summary>
    public static bool MarksAsDefault(NewDownloadDefault setting, string branch) => setting switch
    {
        NewDownloadDefault.None => false,
        NewDownloadDefault.Stable => branch is "stable" or "lts",
        NewDownloadDefault.Daily => branch == "daily",
        NewDownloadDefault.Experimental => Channels.DownloadFolderForBranch(branch) == "experimental",
        NewDownloadDefault.Bforartists => branch == "bforartists",
        NewDownloadDefault.UpbgeStable => branch == "upbge-stable",
        NewDownloadDefault.UpbgeWeekly => branch == "upbge-weekly",
        _ => false,
    };

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Affects(SettingsGroups.Connection))
        {
            RebuildHttpClient();
        }

        if (_options.StartScraping && e.Previous.ToScrapeSettings() != e.Current.ToScrapeSettings())
        {
            Scrape.Reschedule();
        }

        if (e.Affects(SettingsGroups.Builds))
        {
            Updates.Refresh();
        }

        if (e.Current.General.LogLevel != e.Previous.General.LogLevel && _options.LogLevel is null)
        {
            Log.MinimumLevel = e.Current.General.LogLevel;
        }

        if (_options.LibraryOverride is null && e.Current.General.LibraryFolder is { } folder
            && !string.Equals(folder, e.Previous.General.LibraryFolder, StringComparison.Ordinal))
        {
            Track(SwitchLibraryAsync(folder));
        }
    }

    private async Task SwitchLibraryAsync(string folder)
    {
        try
        {
            await OpenLibraryAsync(folder, purge: false, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception e)
        {
            Log.Error($"Could not open library {folder}", e);
        }
    }

    private void Track(Task task)
    {
        lock (_gate)
        {
            _background.RemoveAll(t => t.IsCompleted);
            _background.Add(task);
        }
    }

    private async Task Step(string name, Func<ValueTask> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Error($"Stopping the {name} failed", e);
        }
    }
}
