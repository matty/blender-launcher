using System.Net;
using System.Runtime.CompilerServices;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Localisation;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Tests.Net;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Tests.Scraping;

/// <summary><see cref="ScrapeService"/> over fake sources: concurrency, errors, de-duplication, "new", scheduling.</summary>
public sealed class ScrapeServiceTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset Start = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bl-scrape-service-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new RecordedResponseHandler().CreateClient();
    private readonly ManualTimeProvider _time = new(Start);

    public ScrapeServiceTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _http.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_slow_source_does_not_delay_the_others()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new FakeSource(BuildSourceIds.Stable, (_, ct) => Yield([Build("stable", "4.2.0", Start.AddDays(-1))], before: gate.Task, ct: ct));
        var fast = new FakeSource(BuildSourceIds.Daily, (_, ct) => Yield([Build("daily", "5.1.0-alpha", Start.AddDays(-1), hash: "aaaaaaaaaaaa")], ct: ct));
        await using var service = Service([slow, fast]);
        var fastDone = new TaskCompletionSource<ScrapeSourceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SourceCompleted += (_, r) =>
        {
            if (r.SourceId == BuildSourceIds.Daily)
            {
                fastDone.TrySetResult(r);
            }
        };

        var check = service.CheckAsync(ct: Ct);
        var daily = await fastDone.Task.WaitAsync(Wait, Ct);

        Assert.Single(daily.Builds);
        Assert.False(check.IsCompleted);
        Assert.True(service.IsChecking);

        gate.SetResult();
        var snapshot = await check.WaitAsync(Wait, Ct);
        Assert.Equal([BuildSourceIds.Stable, BuildSourceIds.Daily], snapshot.Sources.Select(s => s.SourceId)); // source order, not completion order
        Assert.Equal(["4.2.0", "5.1.0-alpha"], snapshot.Builds.Select(b => b.Build.Subversion));
        Assert.False(service.IsChecking);
        Assert.Same(snapshot, service.Current);
    }

    [Fact]
    public async Task A_failing_source_is_reported_without_affecting_the_others()
    {
        var broken = new FakeSource(BuildSourceIds.Bforartists, (_, ct) => Yield([], after: new HttpRequestException("refused"), ct: ct));
        var ok = new FakeSource(BuildSourceIds.Daily, (_, ct) => Yield([Build("daily", "5.1.0-alpha", Start, hash: "aaaaaaaaaaaa")], ct: ct));
        var status = new FakeSource(BuildSourceIds.UpbgeStable,
            (_, ct) => Yield([], after: new HttpRequestException("bad", null, HttpStatusCode.ServiceUnavailable), ct: ct));
        await using var service = Service([broken, ok, status], ScrapeSettings.Default with { ScrapeUpbge = true });

        var snapshot = await service.CheckAsync(ct: Ct).WaitAsync(Wait, Ct);

        Assert.Single(snapshot.Builds);
        Assert.Equal(2, snapshot.Errors.Count);
        var bfa = snapshot.Sources.Single(s => s.SourceId == BuildSourceIds.Bforartists);
        Assert.False(bfa.Succeeded);
        Assert.Equal(ScrapeMessageKeys.ConnectionFailed, bfa.Error!.MessageKey);
        Assert.Equal(ScrapeMessageKeys.HttpStatus, snapshot.Sources.Single(s => s.SourceId == BuildSourceIds.UpbgeStable).Error!.MessageKey);
        Assert.True(snapshot.Sources.Single(s => s.SourceId == BuildSourceIds.Daily).Succeeded);
    }

    [Fact]
    public async Task Builds_yielded_before_a_failure_are_kept()
    {
        var error = new ScrapeError(BuildSourceIds.UpbgeStable, ScrapeMessageKeys.GitHubRateLimited, HttpStatusCode.Forbidden);
        var source = new FakeSource(BuildSourceIds.UpbgeStable, (_, ct) => Yield(
            [Build("upbge-stable", "0.36.0", Start, link: "https://github.com/UPBGE/upbge/a-upbge-windows.zip"),
             Build("upbge-stable", "0.35.0", Start, link: "https://github.com/UPBGE/upbge/b-upbge-windows.zip")],
            after: new ScrapeSourceException(error), ct: ct));
        await using var service = Service([source]);
        ScrapeSourceResult? published = null;
        service.SourceCompleted += (_, r) => published = r;

        var snapshot = await service.CheckAsync(ct: Ct).WaitAsync(Wait, Ct);

        Assert.Equal(2, snapshot.Builds.Count);
        Assert.Same(error, Assert.Single(snapshot.Errors));
        Assert.NotNull(published);
        Assert.Equal(2, published.Builds.Count);
        Assert.Same(error, published.Error);
    }

    [Fact]
    public async Task Builds_with_the_same_full_version_are_listed_once()
    {
        var source = new FakeSource(BuildSourceIds.UpbgeStable, (_, ct) => Yield(
            [Build("upbge-stable", "0.36.0", Start, link: "https://github.com/UPBGE/upbge/upbge-0.36-windows-x86_64.zip"),
             Build("upbge-stable", "0.36.0", Start, link: "https://github.com/UPBGE/upbge/upbge-0.36-windows-x86_64.7z"),
             Build("upbge-stable", "0.35.0", Start, link: "https://github.com/UPBGE/upbge/upbge-0.35-windows-x86_64.zip")],
            ct: ct));
        await using var service = Service([source]);

        var snapshot = await service.CheckAsync(ct: Ct).WaitAsync(Wait, Ct);

        Assert.Equal(["0.36.0", "0.35.0"], snapshot.Builds.Select(b => b.Build.Subversion));
        Assert.EndsWith(".zip", snapshot.Builds[0].Build.Link, StringComparison.Ordinal); // the first one wins
        Assert.Equal(1, snapshot.Sources.Single().Duplicates);
    }

    [Fact]
    public void The_deduplication_key_is_the_full_version_string()
    {
        var a = Build("daily", "5.1.0-alpha", Start, hash: "aaaaaaaaaaaa").Build;
        var b = Build("daily", "5.1.0-alpha", Start, hash: "bbbbbbbbbbbb").Build;
        Assert.Equal("5.1.0-alpha+daily.aaaaaaaaaaaa", ScrapeService.DeduplicationKey(a));
        Assert.NotEqual(ScrapeService.DeduplicationKey(a), ScrapeService.DeduplicationKey(b));
        Assert.Equal("not a version+custom",
            ScrapeService.DeduplicationKey(new BuildInfo { Link = "x", Subversion = "not a version", CommitTime = Start, Branch = "custom" }));
    }

    [Theory]
    [InlineData(HostOs.Windows, "amd64", new[] { "x64", "upbge" })]
    [InlineData(HostOs.Windows, "arm64", new[] { "arm64" })]
    [InlineData(HostOs.Linux, "x86_64", new[] { "x64", "arm64", "plain", "upbge" })]
    public async Task The_windows_post_filter_applies_to_every_source(HostOs os, string machine, string[] kept)
    {
        var source = new FakeSource(BuildSourceIds.Daily, (_, ct) => Yield(
            [Build("daily", "5.1.0-alpha", Start, hash: "aaaaaaaaaaa1", link: "https://example.org/blender-windows-x64.zip"),
             Build("daily", "5.1.0-alpha", Start, hash: "aaaaaaaaaaa2", link: "https://example.org/blender-windows-arm64.zip"),
             Build("daily", "5.1.0-alpha", Start, hash: "aaaaaaaaaaa3", link: "https://example.org/blender-plain.zip"),
             Build("daily", "5.1.0-alpha", Start, hash: "aaaaaaaaaaa4", link: "https://example.org/UPBGE-0.36.zip")],
            ct: ct));
        await using var service = Service([source], os: os, machine: machine);

        var snapshot = await service.CheckAsync(ct: Ct).WaitAsync(Wait, Ct);

        var names = new Dictionary<string, string>
        {
            ["aaaaaaaaaaa1"] = "x64", ["aaaaaaaaaaa2"] = "arm64", ["aaaaaaaaaaa3"] = "plain", ["aaaaaaaaaaa4"] = "upbge",
        };
        Assert.Equal(kept, snapshot.Builds.Select(b => names[b.Build.BuildHash!]));
        Assert.Equal(4 - kept.Length, snapshot.Sources.Single().FilteredOut);
    }

    [Fact]
    public async Task Nothing_is_new_on_the_first_check_after_start_and_later_checks_compare_with_the_last_check()
    {
        var store = new InMemoryScrapeStateStore(Start.AddDays(-10));
        var builds = new List<AvailableBuild> { Build("daily", "5.1.0-alpha", Start.AddDays(-1), hash: "aaaaaaaaaaaa") };
        var source = new FakeSource(BuildSourceIds.Daily, (_, ct) => Yield([.. builds], ct: ct));
        await using var service = Service([source], state: store);

        // First check: the build is newer than the stored time, but nothing is new right after start.
        var first = await service.CheckAsync(ct: Ct).WaitAsync(Wait, Ct);
        Assert.True(first.FirstCheck);
        Assert.Equal(0, first.NewCount);
        Assert.Equal(Start, store.LastChecked);
        Assert.Equal(Start, service.LastChecked);

        // Second check, an hour later: only what was committed after the first check is new.
        _time.Advance(TimeSpan.FromHours(1));
        builds.Add(Build("daily", "5.1.0-alpha", Start.AddMinutes(30), hash: "bbbbbbbbbbbb"));
        var second = await service.CheckAsync(ct: Ct).WaitAsync(Wait, Ct);
        Assert.False(second.FirstCheck);
        Assert.Equal(Start, second.ComparedWith);
        Assert.Equal([false, true], second.Builds.Select(b => b.IsNew));
        Assert.Equal(Start.AddHours(1), store.LastChecked);

        // Third: nothing newer than the second check.
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, (await service.CheckAsync(ct: Ct).WaitAsync(Wait, Ct)).NewCount);
    }

    [Fact]
    public async Task A_restart_reads_the_stored_time_but_still_marks_nothing_on_its_first_check()
    {
        var store = new FileScrapeStateStore(new DataDirectories(_dir));
        var source = new FakeSource(BuildSourceIds.Daily, (_, ct) => Yield([Build("daily", "5.1.0-alpha", Start.AddDays(-1), hash: "aaaaaaaaaaaa")], ct: ct));
        await using (var first = Service([source], state: store))
        {
            await first.CheckAsync(ct: Ct).WaitAsync(Wait, Ct);
        }

        Assert.Equal(Start, await store.LoadLastCheckedAsync(Ct));

        await using var restarted = Service([source], state: store);
        var snapshot = await restarted.CheckAsync(ct: Ct).WaitAsync(Wait, Ct);
        Assert.True(snapshot.FirstCheck);
        Assert.Equal(Start, snapshot.ComparedWith);
        Assert.Equal(0, snapshot.NewCount);
    }

    [Fact]
    public async Task A_check_where_every_source_failed_keeps_the_last_check_time()
    {
        var store = new InMemoryScrapeStateStore(Start.AddDays(-1));
        var source = new FakeSource(BuildSourceIds.Daily, (_, ct) => Yield([], after: new HttpRequestException("offline"), ct: ct));
        await using var service = Service([source], state: store);

        var snapshot = await service.CheckAsync(ct: Ct).WaitAsync(Wait, Ct);

        Assert.Single(snapshot.Errors);
        Assert.Equal(Start.AddDays(-1), store.LastChecked);
    }

    [Fact]
    public async Task A_second_request_joins_the_running_check()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource(BuildSourceIds.Daily, (_, ct) => Yield([Build("daily", "5.1.0-alpha", Start, hash: "aaaaaaaaaaaa")], before: gate.Task, ct: ct));
        await using var service = Service([source]);
        var started = 0;
        service.CheckStarted += (_, _) => Interlocked.Increment(ref started);

        var first = service.CheckAsync(ct: Ct);
        var second = service.CheckAsync(forced: true, Ct);
        gate.SetResult();
        var a = await first.WaitAsync(Wait, Ct);
        var b = await second.WaitAsync(Wait, Ct);

        Assert.Same(a, b);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, started);
        Assert.False(a.Forced);

        // Once it has finished, a new request starts a new check.
        var third = await service.CheckAsync(ct: Ct).WaitAsync(Wait, Ct);
        Assert.NotSame(a, third);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task A_waiter_that_gives_up_does_not_cancel_the_check()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource(BuildSourceIds.Daily, (_, ct) => Yield([Build("daily", "5.1.0-alpha", Start, hash: "aaaaaaaaaaaa")], before: gate.Task, ct: ct));
        await using var service = Service([source]);
        using var giveUp = new CancellationTokenSource();

        var impatient = service.CheckAsync(ct: giveUp.Token);
        var patient = service.CheckAsync(ct: Ct);
        await giveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
        gate.SetResult();

        Assert.Single((await patient.WaitAsync(Wait, Ct)).Builds);
    }

    [Fact]
    public async Task A_forced_check_also_runs_every_visible_channel()
    {
        var sources = AllSourceIds.Select(id => new FakeSource(id, (_, ct) => Yield([], ct: ct))).ToList();
        var settings = ScrapeSettings.Default with
        {
            ScrapeStable = false, ShowStable = true,          // visible only: forced check only
            ScrapeDaily = false, ShowDaily = false,           // neither: never
            ScrapeExperimental = true,                        // experimental and patch: always
            ScrapeBforartists = false, ShowBforartists = true,
            ScrapeUpbge = true,
            ScrapeUpbgeWeekly = false, ShowUpbgeWeekly = false,
        };
        await using var service = Service(sources, settings);
        var started = new List<ScrapeCheckStarted>();
        service.CheckStarted += (_, e) => started.Add(e);

        await service.CheckAsync(ct: Ct).WaitAsync(Wait, Ct);
        await service.CheckAsync(forced: true, Ct).WaitAsync(Wait, Ct);

        Assert.Equal([BuildSourceIds.Experimental, BuildSourceIds.Patch, BuildSourceIds.UpbgeStable], started[0].SourceIds);
        Assert.False(started[0].Forced);
        Assert.Equal(
            [BuildSourceIds.Stable, BuildSourceIds.Experimental, BuildSourceIds.Patch, BuildSourceIds.Bforartists, BuildSourceIds.UpbgeStable],
            started[1].SourceIds);
        Assert.True(started[1].Forced);
    }

    [Fact]
    public async Task Settings_reach_the_sources_through_the_context()
    {
        ScrapeContext? seen = null;
        var source = new FakeSource(BuildSourceIds.Patch, (c, ct) =>
        {
            seen = c;
            return Yield([], ct: ct);
        });
        var settings = ScrapeSettings.Default with
        {
            PatchArchive = true, FetchPrTitles = true, PrependPrNumber = false, MinimumStableVersion = new(4, 2, 0),
        };
        await using var service = Service([source], settings);

        await service.CheckAsync(ct: Ct).WaitAsync(Wait, Ct);

        Assert.NotNull(seen);
        Assert.True(seen.PatchArchive);
        Assert.True(seen.FetchPrTitles);
        Assert.False(seen.PrependPrNumber);
        Assert.Equal(new SemVer(4, 2, 0), seen.MinimumStableVersion);
        Assert.Equal(_dir, seen.CacheDirectory);
        Assert.Same(_time, seen.Time);
    }

    [Fact]
    public async Task Start_checks_at_once_when_check_on_startup_is_on()
    {
        var source = new FakeSource(BuildSourceIds.Daily, (_, ct) => Yield([], ct: ct));
        await using var service = Service([source], ScrapeSettings.Default with { CheckOnStartup = true });

        var snapshot = await service.StartAsync(Ct).WaitAsync(Wait, Ct);

        Assert.NotNull(snapshot);
        Assert.Equal(1, source.Calls);
        Assert.Null(service.NextScheduledCheck); // automatic checks are off by default
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public async Task Periodic_checks_follow_the_interval_and_restart_after_each_check()
    {
        var source = new FakeSource(BuildSourceIds.Daily, (_, ct) => Yield([], ct: ct));
        var settings = ScrapeSettings.Default with { CheckOnStartup = false, CheckAutomatically = true, CheckIntervalHours = 6 };
        await using var service = Service([source], settings);
        var completed = new SemaphoreSlim(0);
        service.CheckCompleted += (_, _) => completed.Release();

        Assert.Null(await service.StartAsync(Ct));
        Assert.Equal(0, source.Calls);
        Assert.Equal(Start.AddHours(6), service.NextScheduledCheck);

        _time.Advance(TimeSpan.FromHours(5));
        Assert.Equal(0, source.Calls);

        _time.Advance(TimeSpan.FromHours(1));
        Assert.True(await completed.WaitAsync(Wait, Ct));
        Assert.Equal(1, source.Calls);
        await WaitUntil(() => service.NextScheduledCheck is not null);
        Assert.Equal(Start.AddHours(12), service.NextScheduledCheck);

        // A manual check in between moves the next scheduled one to an interval after it.
        _time.Advance(TimeSpan.FromHours(2));
        await service.CheckAsync(ct: Ct).WaitAsync(Wait, Ct);
        Assert.True(await completed.WaitAsync(Wait, Ct));
        Assert.Equal(Start.AddHours(14), service.NextScheduledCheck);
        Assert.Equal(1, _time.ActiveTimers);

        _time.Advance(TimeSpan.FromHours(6));
        Assert.True(await completed.WaitAsync(Wait, Ct));
        Assert.Equal(3, source.Calls);
    }

    [Fact]
    public async Task Reschedule_follows_the_current_settings()
    {
        var settings = ScrapeSettings.Default with { CheckOnStartup = false, CheckAutomatically = false };
        var source = new FakeSource(BuildSourceIds.Daily, (_, ct) => Yield([], ct: ct));
        await using var service = new ScrapeService(Options([source], () => settings));

        await service.StartAsync(Ct);
        Assert.Null(service.NextScheduledCheck);

        settings = settings with { CheckAutomatically = true, CheckIntervalHours = 1 }; // clamped to 6
        service.Reschedule();
        Assert.Equal(Start.AddHours(6), service.NextScheduledCheck);

        settings = settings with { CheckAutomatically = false };
        service.Reschedule();
        Assert.Null(service.NextScheduledCheck);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Theory]
    [InlineData(-5, 6)]
    [InlineData(6, 6)]
    [InlineData(596, 596)]
    [InlineData(1000, 596)]
    public void The_interval_is_clamped_to_6_to_596_hours(int hours, int expected)
    {
        var settings = ScrapeSettings.Default with { CheckIntervalHours = hours };
        Assert.Equal(expected, settings.CheckIntervalHours);
        Assert.Equal(TimeSpan.FromHours(expected), settings.CheckInterval);
    }

    [Fact]
    public void Defaults_are_upstreams()
    {
        var d = ScrapeSettings.Default;
        Assert.Equal(12, d.CheckIntervalHours);
        Assert.True(d.CheckOnStartup);
        Assert.False(d.CheckAutomatically);
        Assert.Equal(
            [BuildSourceIds.Stable, BuildSourceIds.Daily, BuildSourceIds.Experimental, BuildSourceIds.Patch, BuildSourceIds.Bforartists, BuildSourceIds.UpbgeStable],
            AllSourceIds.Where(id => d.IsEnabled(id, forced: false)));
        Assert.False(d.IsEnabled(BuildSourceIds.UpbgeWeekly, forced: true));
        Assert.False(d.IsEnabled("unknown", forced: true));
    }

    [Fact]
    public async Task Dispose_cancels_the_running_check_and_stops_the_timer()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource(BuildSourceIds.Daily, (_, ct) => Forever(entered, ct));
        var store = new InMemoryScrapeStateStore();
        var service = Service([source], ScrapeSettings.Default with { CheckAutomatically = true }, state: store);
        var completed = false;
        service.CheckCompleted += (_, _) => completed = true;

        var check = service.CheckAsync(ct: Ct);
        await entered.Task.WaitAsync(Wait, Ct);
        await service.DisposeAsync().AsTask().WaitAsync(Wait, Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.False(completed);
        Assert.Null(store.LastChecked);
        Assert.Equal(0, _time.ActiveTimers);
        Assert.Throws<ObjectDisposedException>(() => { _ = service.CheckAsync(ct: Ct); });
        await service.DisposeAsync(); // twice is fine
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_break_the_check()
    {
        var source = new FakeSource(BuildSourceIds.Daily, (_, ct) => Yield([Build("daily", "5.1.0-alpha", Start, hash: "aaaaaaaaaaaa")], ct: ct));
        await using var service = Service([source]);
        service.CheckStarted += (_, _) => throw new InvalidOperationException("started");
        service.SourceCompleted += (_, _) => throw new InvalidOperationException("source");
        service.CheckCompleted += (_, _) => throw new InvalidOperationException("completed");

        Assert.Single((await service.CheckAsync(ct: Ct).WaitAsync(Wait, Ct)).Builds);
    }

    [Fact]
    public async Task Channels_are_read_from_the_snapshot()
    {
        var source = new FakeSource(BuildSourceIds.Stable, (_, ct) => Yield(
            [Build("stable", "4.3.0", Start), Build("lts", "4.2.5", Start), Build("lts", "3.6.9", Start)], ct: ct));
        await using var service = Service([source]);

        var snapshot = await service.CheckAsync(ct: Ct).WaitAsync(Wait, Ct);

        Assert.Single(snapshot.ForChannel(Channel.Stable));
        Assert.Equal(2, snapshot.ForChannel(Channel.Lts).Count());
        Assert.Empty(snapshot.ForChannel(Channel.Daily));
    }

    [Fact]
    public void Errors_are_translated_with_source_display_names()
    {
        var translator = new Translator("en");
        Assert.Equal("Stable: the server returned HTTP 503.",
            ScrapeErrorText.Format(translator, ScrapeError.HttpStatusError(BuildSourceIds.Stable, HttpStatusCode.ServiceUnavailable)));
        Assert.Equal("UPBGE Weekly: Error: connection failed at 12:00",
            ScrapeErrorText.Format(translator, ScrapeError.ConnectionFailed(BuildSourceIds.UpbgeWeekly, _time)));
        Assert.StartsWith("UPBGE Stable: GitHub's request limit was reached.",
            ScrapeErrorText.Format(translator, new ScrapeError(BuildSourceIds.UpbgeStable, ScrapeMessageKeys.GitHubRateLimited, messageArgs: [new("source", BuildSourceIds.UpbgeStable)])),
            StringComparison.Ordinal);
        Assert.Equal("someone-else: Error: connection failed at 12:00",
            ScrapeErrorText.Format(translator, ScrapeError.ConnectionFailed("someone-else", _time)));

        foreach (var id in AllSourceIds.Append(Core.Api.ApiDataService.SourceId).Append(Core.Updates.AppReleaseService.SourceId))
        {
            Assert.True(translator.TryGet(ScrapeErrorText.SourceKey(id), out var name), id);
            Assert.NotEqual(id, name);
        }
    }

    [Fact]
    public async Task The_state_file_round_trips_and_reads_a_bad_file_as_never()
    {
        var store = new FileScrapeStateStore(new DataDirectories(_dir));
        Assert.Null(await store.LoadLastCheckedAsync(Ct));

        var time = new DateTimeOffset(2026, 11, 11, 13, 5, 0, TimeSpan.FromHours(1));
        await store.SaveLastCheckedAsync(time, Ct);
        Assert.Equal(time, await store.LoadLastCheckedAsync(Ct));
        Assert.Contains("\"last_time_checked\": \"2026-11-11T13:05:00", await File.ReadAllTextAsync(store.FilePath, Ct), StringComparison.Ordinal);

        await File.WriteAllTextAsync(store.FilePath, "{not json", Ct);
        Assert.Null(await store.LoadLastCheckedAsync(Ct));
    }

    [Fact]
    public async Task The_size_cache_round_trips_and_prunes()
    {
        var path = Path.Combine(_dir, BuildSizeCache.FileNameFor("demo"));
        Assert.EndsWith("demo_sizes.json", path, StringComparison.Ordinal);
        var sizes = await BuildSizeCache.LoadAsync(path, Ct);
        Assert.Equal(0, sizes.Count);
        sizes.Set("https://a", 1);
        sizes.Set("https://b", 2);
        sizes.Set("https://c", null);
        await sizes.SaveAsync(["https://a", "https://b"], ct: Ct);

        var again = await BuildSizeCache.LoadAsync(path, Ct);
        Assert.Equal(1, again.Get("https://a"));
        Assert.Equal(2, again.Get("https://b"));
        Assert.Null(again.Get("https://c"));

        await again.SaveAsync(["https://b"], ct: Ct);
        var pruned = await BuildSizeCache.LoadAsync(path, Ct);
        Assert.Null(pruned.Get("https://a"));
        Assert.Equal(1, pruned.Count);

        await File.WriteAllTextAsync(path, "[1,2]", Ct);
        Assert.Equal(0, (await BuildSizeCache.LoadAsync(path, Ct)).Count);
    }

    private static readonly string[] AllSourceIds =
    [
        BuildSourceIds.Stable, BuildSourceIds.Daily, BuildSourceIds.Experimental, BuildSourceIds.Patch,
        BuildSourceIds.Bforartists, BuildSourceIds.UpbgeStable, BuildSourceIds.UpbgeWeekly,
    ];

    private ScrapeServiceOptions Options(IEnumerable<IBuildSource> sources, Func<ScrapeSettings> settings, IScrapeStateStore? state = null,
        HostOs os = HostOs.Windows, string machine = "amd64") => new()
    {
        Http = () => _http,
        CacheDirectory = _dir,
        Sources = [.. sources],
        Settings = settings,
        State = state,
        Time = _time,
        Os = os,
        Machine = machine,
    };

    private ScrapeService Service(IEnumerable<IBuildSource> sources, ScrapeSettings? settings = null, IScrapeStateStore? state = null,
        HostOs os = HostOs.Windows, string machine = "amd64")
    {
        var s = settings ?? ScrapeSettings.Default;
        return new ScrapeService(Options(sources, () => s, state, os, machine));
    }

    private static AvailableBuild Build(string branch, string version, DateTimeOffset commit, string? hash = null, string? link = null) =>
        new(new BuildInfo
        {
            Link = link ?? $"https://example.org/{branch}/blender-{version}-{hash}-windows-x64.zip",
            Subversion = version,
            BuildHash = hash,
            CommitTime = commit,
            Branch = branch,
        });

    private static async IAsyncEnumerable<AvailableBuild> Yield(
        IReadOnlyList<AvailableBuild> builds, Task? before = null, Exception? after = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (before is not null)
        {
            await before.WaitAsync(ct);
        }

        foreach (var build in builds)
        {
            await Task.Yield();
            yield return build;
        }

        if (after is not null)
        {
            throw after;
        }
    }

    private static async IAsyncEnumerable<AvailableBuild> Forever(TaskCompletionSource entered, [EnumeratorCancellation] CancellationToken ct = default)
    {
        entered.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(10, Ct);
        }
    }

    private sealed class FakeSource(string id, Func<ScrapeContext, CancellationToken, IAsyncEnumerable<AvailableBuild>> scrape) : IBuildSource
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string Id => id;

        public IReadOnlyList<Channel> Channels => [];

        public IAsyncEnumerable<AvailableBuild> ScrapeAsync(ScrapeContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return scrape(context, cancellationToken);
        }
    }
}
