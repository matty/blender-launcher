using System.Collections.Concurrent;
using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Scraping.Bforartists;
using BlenderLauncher.Core.Scraping.Builder;
using BlenderLauncher.Core.Scraping.PullRequests;
using BlenderLauncher.Core.Scraping.Stable;
using BlenderLauncher.Core.Scraping.Upbge;
using BlenderLauncher.Core.Updates;

namespace BlenderLauncher.Core.Scraping;

/// <summary>What a <see cref="ScrapeService"/> is built from.</summary>
public sealed record ScrapeServiceOptions
{
    /// <summary>Returns the client to use; called once per check, so a rebuilt client is picked up.</summary>
    public required Func<HttpClient> Http { get; init; }

    /// <summary>Folder for the sources' caches (<see cref="DataDirectories.Cache"/>).</summary>
    public required string CacheDirectory { get; init; }

    /// <summary>Every source, in display order (<see cref="ScrapeService.CreateSources"/> gives upstream's set).</summary>
    public required IReadOnlyList<IBuildSource> Sources { get; init; }

    /// <summary>Returns the current settings; read at each check and each reschedule.</summary>
    public required Func<ScrapeSettings> Settings { get; init; }

    /// <summary>Where the last-check time is kept. In memory when not set.</summary>
    public IScrapeStateStore? State { get; init; }

    /// <summary>Refreshed before the sources run, and the LTS table they use. Skipped when null.</summary>
    public ApiDataService? ApiData { get; init; }

    /// <summary>The launcher release check after the sources. Skipped when null.</summary>
    public AppReleaseService? Releases { get; init; }

    /// <summary>The shared pull-request title service, exposed as <see cref="ScrapeService.PullRequestTitles"/>.</summary>
    public PullRequestTitleService? PullRequestTitles { get; init; }

    /// <summary>The running launcher version, for the release check.</summary>
    public string CurrentVersion { get; init; } = AppIdentity.Version;

    public Log? Log { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public HostOs Os { get; init; } = BuildPlatform.CurrentOs;

    public string Machine { get; init; } = BuildPlatform.CurrentMachine;
}

/// <summary>
/// Owns the list of available builds and when it is refreshed (port of upstream's <c>threads/scraper.py</c>
/// and <c>main_window/window.py:645-760</c>).
/// </summary>
/// <remarks>
/// <para><b>A check</b> (<see cref="CheckAsync"/>):</para>
/// <list type="number">
/// <item>Refreshes the API data (<see cref="ApiDataService.RefreshAsync"/>); its failures are reported in
/// <see cref="ScrapeSnapshot.Errors"/>, never fatal.</item>
/// <item>Runs every source enabled by the settings (<see cref="ScrapeSettings.IsEnabled"/>) at the same time, each on
/// its own task, so one slow or rate-limited source never delays another. Each build passes the Windows post-filter
/// (<see cref="BuildPlatform.PassesPostFilter"/>), is dropped when its full version string was already listed in this
/// check (upstream's downloads-list rule), and is marked new when its commit time is after the last check, except on
/// the first check after start. A source's failure is caught and kept with the builds it yielded before it.</item>
/// <item>Checks for a launcher release (<see cref="AppReleaseService.CheckAsync"/>).</item>
/// <item>Stores the check time and publishes the new <see cref="Current"/> snapshot.</item>
/// </list>
/// <para><b>Threading.</b> Checks run on the thread pool. <see cref="CheckStarted"/>, <see cref="SourceCompleted"/>
/// (once per source, as each finishes, possibly concurrently for different sources) and <see cref="CheckCompleted"/>
/// are raised on worker threads; view models marshal to the UI thread. Only one check runs at a time: a request
/// while one runs joins it and gets its snapshot, even when the request was forced and the running check was not.</para>
/// <para><b>Scheduling.</b> <see cref="StartAsync"/> loads the last-check time and checks at once when
/// <see cref="ScrapeSettings.CheckOnStartup"/> is on. After every check, and after <see cref="Reschedule"/>, a
/// single-shot timer on <see cref="ScrapeServiceOptions.Time"/> is armed for <see cref="ScrapeSettings.CheckInterval"/>
/// when <see cref="ScrapeSettings.CheckAutomatically"/> is on (upstream's re-armed <c>QTimer</c>); a check that starts
/// for any reason stops it. <see cref="DisposeAsync"/> stops the timer and cancels a running check.</para>
/// <para>Differences from upstream: the sources run concurrently rather than one after another; a build is listed
/// once even when a source yields it twice; and a check in which every source failed does not move the last-check
/// time on, so builds published while offline are still shown as new later.</para>
/// </remarks>
public sealed class ScrapeService : IAsyncDisposable
{
    private readonly ScrapeServiceOptions _options;
    private readonly IScrapeStateStore _state;
    private readonly TimeProvider _time;
    private readonly Log? _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private TaskCompletionSource<ScrapeSnapshot>? _running;
    private ITimer? _timer;
    private DateTimeOffset? _nextCheck;
    private DateTimeOffset? _lastChecked;
    private bool _stateLoaded;
    private bool _completedOne;
    private bool _disposed;
    private ScrapeSnapshot _current = ScrapeSnapshot.Empty;

    public ScrapeService(ScrapeServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _state = options.State ?? new InMemoryScrapeStateStore();
        _time = options.Time;
        _log = options.Log;
        Sources = [.. options.Sources];
        PullRequestTitles = options.PullRequestTitles;
    }

    /// <summary>
    /// The standard service: upstream's sources over <paramref name="http"/> and caches in <see cref="DataDirectories.Cache"/>.
    /// The last-check time goes to <paramref name="state"/> (the app passes <see cref="Settings.SettingsScrapeStateStore"/>),
    /// or is kept in memory when it is null.
    /// </summary>
    public static ScrapeService Create(
        HttpClientProvider http,
        DataDirectories directories,
        Func<ScrapeSettings> settings,
        ApiDataService? apiData,
        AppReleaseService? releases,
        IScrapeStateStore? state = null,
        Log? log = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(directories);
        HttpClient Client() => http.Client;
        var titles = new PullRequestTitleService(Client, directories.Cache, log);
        return new ScrapeService(new ScrapeServiceOptions
        {
            Http = Client,
            CacheDirectory = directories.Cache,
            Sources = CreateSources(titles),
            Settings = settings,
            State = state ?? new InMemoryScrapeStateStore(),
            ApiData = apiData,
            Releases = releases,
            PullRequestTitles = titles,
            Log = log,
        });
    }

    /// <summary>
    /// Upstream's sources in its order: stable, daily, experimental, patch (with <paramref name="titles"/>),
    /// Bforartists, UPBGE stable and weekly (sharing one release feed).
    /// </summary>
    public static IReadOnlyList<IBuildSource> CreateSources(PullRequestTitleService? titles)
    {
        var (upbgeStable, upbgeWeekly) = UpbgeBuildSource.CreatePair();
        return
        [
            new StableBuildSource(),
            BuilderBuildSource.Daily(),
            BuilderBuildSource.Experimental(),
            BuilderBuildSource.Patch(titles),
            new BforartistsBuildSource(),
            upbgeStable,
            upbgeWeekly,
        ];
    }

    /// <summary>Every source, enabled or not, in display order.</summary>
    public IReadOnlyList<IBuildSource> Sources { get; }

    /// <summary>The shared pull-request title service (also used by the patch source), for "Fetch PR name". Null when none was given.</summary>
    public PullRequestTitleService? PullRequestTitles { get; }

    /// <summary>The result of the last completed check; <see cref="ScrapeSnapshot.Empty"/> before the first.</summary>
    public ScrapeSnapshot Current => Volatile.Read(ref _current);

    /// <summary>True while a check runs.</summary>
    public bool IsChecking
    {
        get
        {
            lock (_gate)
            {
                return _running is not null;
            }
        }
    }

    /// <summary>The last completed check's time (stored), or null when there has been none.</summary>
    public DateTimeOffset? LastChecked
    {
        get
        {
            lock (_gate)
            {
                return _lastChecked;
            }
        }
    }

    /// <summary>When the timer will start the next check, or null when no check is scheduled.</summary>
    public DateTimeOffset? NextScheduledCheck
    {
        get
        {
            lock (_gate)
            {
                return _nextCheck;
            }
        }
    }

    /// <summary>A check started. Raised on a worker thread.</summary>
    public event EventHandler<ScrapeCheckStarted>? CheckStarted;

    /// <summary>A source finished (successfully or not). Raised on worker threads, possibly concurrently for different sources.</summary>
    public event EventHandler<ScrapeSourceResult>? SourceCompleted;

    /// <summary>A check finished and <see cref="Current"/> was replaced. Raised on a worker thread.</summary>
    public event EventHandler<ScrapeSnapshot>? CheckCompleted;

    /// <summary>
    /// Start-up: loads the last-check time, then checks at once when <see cref="ScrapeSettings.CheckOnStartup"/> is
    /// on (the returned task completes with that check's snapshot) or otherwise arms the timer and returns
    /// <see langword="null"/>. Call once.
    /// </summary>
    public async Task<ScrapeSnapshot?> StartAsync(CancellationToken ct = default)
    {
        await EnsureStateLoadedAsync(ct).ConfigureAwait(false);
        if (_options.Settings().CheckOnStartup)
        {
            return await CheckAsync(forced: false, ct).ConfigureAwait(false);
        }

        Reschedule();
        return null;
    }

    /// <summary>
    /// Runs a check, or joins the one already running. <paramref name="forced"/> also scrapes every channel the
    /// <c>show_*</c> settings make visible (Shift+Check). <paramref name="ct"/> only stops this caller waiting; the
    /// check itself is cancelled only by <see cref="DisposeAsync"/>, which cancels the returned task.
    /// </summary>
    public Task<ScrapeSnapshot> CheckAsync(bool forced = false, CancellationToken ct = default)
    {
        TaskCompletionSource<ScrapeSnapshot> completion;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running is { } running)
            {
                _log?.Debug("A check is already running; joining it");
                return ct.CanBeCanceled ? running.Task.WaitAsync(ct) : running.Task;
            }

            completion = new TaskCompletionSource<ScrapeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _running = completion;
            StopTimerLocked();
        }

        _ = Task.Run(() => RunAndCompleteAsync(completion, forced));
        return ct.CanBeCanceled ? completion.Task.WaitAsync(ct) : completion.Task;
    }

    /// <summary>
    /// Re-arms the timer from now with the current settings (call after the interval or the automatic-check
    /// setting changes). Does nothing while a check runs; the timer is armed when it ends.
    /// </summary>
    public void Reschedule()
    {
        lock (_gate)
        {
            if (_disposed || _running is not null)
            {
                return;
            }

            ArmTimerLocked(_options.Settings());
        }
    }

    /// <summary>Stops the timer and cancels a running check (whose task is then cancelled).</summary>
    public async ValueTask DisposeAsync()
    {
        Task? running;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopTimerLocked();
            running = _running?.Task;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (running is not null)
        {
            try
            {
                await running.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _log?.Warning("The running check failed while shutting down", e);
            }
        }

        _lifetime.Dispose();
    }

    private async Task RunAndCompleteAsync(TaskCompletionSource<ScrapeSnapshot> completion, bool forced)
    {
        try
        {
            var snapshot = await RunCheckAsync(forced, _lifetime.Token).ConfigureAwait(false);
            Volatile.Write(ref _current, snapshot);
            lock (_gate)
            {
                _running = null;
                if (!_disposed)
                {
                    ArmTimerLocked(_options.Settings());
                }
            }

            Raise(CheckCompleted, snapshot, "CheckCompleted");
            completion.TrySetResult(snapshot);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            _log?.Info("Check cancelled");
            ClearRunning();
            completion.TrySetCanceled(_lifetime.Token);
        }
        catch (Exception e)
        {
            // Sources' failures are caught per source; this is a bug in the service or a store.
            _log?.Error("Check failed unexpectedly", e);
            ClearRunning();
            completion.TrySetException(e);
        }
    }

    private void ClearRunning()
    {
        lock (_gate)
        {
            _running = null;
            if (!_disposed)
            {
                ArmTimerLocked(_options.Settings());
            }
        }
    }

    private async Task<ScrapeSnapshot> RunCheckAsync(bool forced, CancellationToken ct)
    {
        await EnsureStateLoadedAsync(ct).ConfigureAwait(false);
        var settings = _options.Settings();
        var startedAt = _time.GetUtcNow();
        var enabled = Sources.Where(s => settings.IsEnabled(s.Id, forced)).ToList();
        bool firstCheck;
        DateTimeOffset? comparedWith;
        lock (_gate)
        {
            firstCheck = !_completedOne;
            comparedWith = _lastChecked;
        }

        _log?.Info($"Checking for new builds{(forced ? " (all visible channels)" : "")}: {string.Join(", ", enabled.Select(s => s.Id))}");
        Raise(CheckStarted, new ScrapeCheckStarted(forced, enabled.Select(s => s.Id).ToList(), startedAt), "CheckStarted");

        var errors = new List<ScrapeError>();

        // 1. API data first: it seeds the stable cache and holds the LTS table.
        ApiRefreshResult? apiRefresh = null;
        if (_options.ApiData is { } api)
        {
            apiRefresh = await api.RefreshAsync(ct).ConfigureAwait(false);
            errors.AddRange(apiRefresh.Errors);
        }

        // 2. Every enabled source at once.
        var context = new ScrapeContext
        {
            Http = _options.Http(),
            CacheDirectory = _options.CacheDirectory,
            Lts = (ILtsLookup?)_options.ApiData ?? ApiData.Empty,
            Os = _options.Os,
            Machine = _options.Machine,
            MinimumStableVersion = settings.MinimumStableVersion,
            DailyArchive = settings.DailyArchive,
            ExperimentalArchive = settings.ExperimentalArchive,
            PatchArchive = settings.PatchArchive,
            FetchPrTitles = settings.FetchPrTitles,
            PrependPrNumber = settings.PrependPrNumber,
            RebuildStableCache = settings.RebuildStableCache,
            Log = _log,
            Time = _time,
        };
        var run = new RunState(firstCheck ? null : comparedWith ?? DateTimeOffset.UnixEpoch, _time.LocalTimeZone);
        var results = await Task.WhenAll(enabled.Select(s => Task.Run(() => RunSourceAsync(s, context, run, ct), ct))).ConfigureAwait(false);
        errors.AddRange(results.Where(r => r.Error is not null).Select(r => r.Error!));

        // 3. The launcher's own release.
        UpdateCheckResult? update = null;
        if (_options.Releases is { } releases)
        {
            update = await releases.CheckAsync(_options.CurrentVersion, settings.UsePreReleaseBuilds, ct).ConfigureAwait(false);
            if (update.Error is { } updateError)
            {
                errors.Add(updateError);
            }
        }

        // 4. Remember the check, unless no source got through.
        var completedAt = _time.GetUtcNow();
        if (results.Length == 0 || results.Any(r => r.Succeeded))
        {
            await _state.SaveLastCheckedAsync(completedAt, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _lastChecked = completedAt;
            }
        }
        else
        {
            _log?.Warning("Every source failed; the last-check time is kept");
        }

        lock (_gate)
        {
            _completedOne = true;
        }

        var snapshot = new ScrapeSnapshot
        {
            Sources = results,
            Builds = results.SelectMany(r => r.Builds).ToList(),
            Errors = errors,
            ApiRefresh = apiRefresh,
            LauncherUpdate = update,
            Forced = forced,
            FirstCheck = firstCheck,
            ComparedWith = comparedWith,
            StartedAt = startedAt,
            CompletedAt = completedAt,
        };
        _log?.Info($"Check finished in {(completedAt - startedAt).TotalSeconds:0.0} s: {snapshot.Builds.Count} builds, " +
                   $"{snapshot.NewCount} new, {errors.Count} errors");
        return snapshot;
    }

    private async Task<ScrapeSourceResult> RunSourceAsync(IBuildSource source, ScrapeContext context, RunState run, CancellationToken ct)
    {
        var started = _time.GetTimestamp();
        var builds = new List<AvailableBuild>();
        var filtered = 0;
        var duplicates = 0;
        ScrapeError? error = null;
        try
        {
            await foreach (var build in source.ScrapeAsync(context, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                if (!BuildPlatform.PassesPostFilter(context.Os, context.Machine, build.Build.Link))
                {
                    filtered++;
                    _log?.Debug($"Skipping {build.Build.Link} as it doesn't match the current platform");
                    continue;
                }

                if (!run.Seen.TryAdd(DeduplicationKey(build.Build), 0))
                {
                    duplicates++;
                    continue;
                }

                builds.Add(build with { IsNew = run.IsNew(build.Build) });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            error = ScrapeError.FromException(source.Id, e, _time);
            _log?.Error($"{source.Id}: scrape failed after {builds.Count} builds: {error.Detail ?? error.MessageKey}");
        }

        var result = new ScrapeSourceResult(source.Id, source.Channels, builds, error, _time.GetElapsedTime(started), filtered, duplicates);
        _log?.Info($"{source.Id}: {builds.Count} builds in {result.Duration.TotalSeconds:0.0} s" +
                   (error is null ? "" : $" (failed: {error.MessageKey})"));
        Raise(SourceCompleted, result, "SourceCompleted");
        return result;
    }

    /// <summary>The de-duplication key: the full version string (upstream's <c>full_semversion</c>), or the raw subversion and branch when it does not parse.</summary>
    public static string DeduplicationKey(BuildInfo build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return build.FullSemVersion?.ToString() ?? $"{build.Subversion}+{build.Branch}";
    }

    private async Task EnsureStateLoadedAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_stateLoaded)
            {
                return;
            }
        }

        var loaded = await _state.LoadLastCheckedAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_stateLoaded)
            {
                _lastChecked = loaded;
                _stateLoaded = true;
            }
        }
    }

    private void ArmTimerLocked(ScrapeSettings settings)
    {
        StopTimerLocked();
        if (!settings.CheckAutomatically)
        {
            return;
        }

        var interval = settings.CheckInterval;
        _nextCheck = _time.GetUtcNow() + interval;
        _timer = _time.CreateTimer(static state => ((ScrapeService)state!).OnTimer(), this, interval, Timeout.InfiniteTimeSpan);
        _log?.Debug($"Next check at {_nextCheck:u}");
    }

    private void StopTimerLocked()
    {
        _timer?.Dispose();
        _timer = null;
        _nextCheck = null;
    }

    private void OnTimer()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        _log?.Info("Scheduled check");
        _ = CheckAsync().ContinueWith(
            static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private void Raise<T>(EventHandler<T>? handler, T args, string name)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, args);
        }
        catch (Exception e)
        {
            // A subscriber's bug must not break the check.
            _log?.Error($"A {name} handler threw", e);
        }
    }

    /// <summary>Per-check state shared by the source tasks.</summary>
    private sealed class RunState(DateTimeOffset? newerThan, TimeZoneInfo localZone)
    {
        public ConcurrentDictionary<string, byte> Seen { get; } = new(StringComparer.Ordinal);

        /// <summary>Upstream's rule: not on the first check after start, and committed after the last check.</summary>
        public bool IsNew(BuildInfo build) => newerThan is { } since && build.CommitTime.ToDateTimeOffset(localZone) > since;
    }
}
