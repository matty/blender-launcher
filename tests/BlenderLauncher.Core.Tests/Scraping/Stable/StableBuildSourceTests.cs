using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Scraping.Stable;
using BlenderLauncher.Core.Tests.Net;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Tests.Scraping.Stable;

/// <summary>
/// <see cref="StableBuildSource"/> against the pages recorded in <c>Fixtures/Net/stable/</c> on 11 Nov 2026 (the
/// release index and the Blender4.5, 5.2 and 5.3 folders) and small inline pages.
/// </summary>
public sealed class StableBuildSourceTests : IDisposable
{
    private const string Index = "https://download.blender.org/release/";
    private const string Folder45 = Index + "Blender4.5/";
    private const string Folder52 = Index + "Blender5.2/";
    private const string Folder53 = Index + "Blender5.3/";

    private const string ChallengePage =
        "<html><head><title>Just a moment...</title></head><body><script src=\"/cdn-cgi/challenge-platform/h/b/orchestrate\"></script></body></html>";

    private static readonly ApiData Lts = ApiData.Parse(Fixtures.ReadText("upstream-api-blender_launcher_api.json"));
    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "bl-stable-tests-" + Guid.NewGuid().ToString("N"));
    private readonly StableBuildSource _source = new();
    private readonly FakeTime _time = new(Plus2);

    public StableBuildSourceTests() => Directory.CreateDirectory(_cacheDir);

    private string CachePath => Path.Combine(_cacheDir, StableBuildSource.CacheFileName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Identity()
    {
        Assert.Equal("stable", _source.Id);
        Assert.Equal([Channel.Stable, Channel.Lts], _source.Channels);
    }

    [Fact]
    public async Task Fresh_scrape_yields_each_windows_x64_zip_once_with_size_and_local_commit_time()
    {
        var handler = RecordedResponseHandler.FromFixtures("stable");
        var builds = await ScrapeAsync(handler, Context(handler) with { MinimumStableVersion = new SemVer(5, 2, 0) });

        handler.AssertAllMatched();
        Assert.Equal([Index, Folder52, Folder53], Urls(handler));
        Assert.Equal(
            ["5.2.0", "5.2.1", "5.2.2", "5.2.3", "5.2.4", "5.3.0"],
            builds.Select(b => b.Build.Subversion));
        Assert.Equal(builds.Count, builds.Select(b => b.Build.Link).Distinct().Count());
        Assert.All(builds, b => Assert.EndsWith("-windows-x64.zip", b.Build.Link, StringComparison.Ordinal));
        Assert.All(builds, b => Assert.False(b.IsNew));

        var b530 = builds[^1];
        Assert.Equal("https://download.blender.org/release/Blender5.3/blender-5.3.0-windows-x64.zip", b530.Build.Link);
        Assert.Equal(425_299_259, b530.SizeBytes);
        Assert.Null(b530.Build.BuildHash);
        Assert.Equal("stable", b530.Build.Branch);

        // 10-Nov-2026 08:58 GMT, in the clock's local zone (UTC+2), as upstream's astimezone().
        Assert.Equal("2026-11-10T10:58:00+02:00", b530.Build.CommitTime.ToString());
        Assert.Equal(404_954_661, builds[0].SizeBytes);
        Assert.Equal("2026-07-14T09:24:00+02:00", builds[0].Build.CommitTime.ToString());
    }

    [Fact]
    public async Task Lts_series_are_promoted_with_the_context_table()
    {
        var handler = RecordedResponseHandler.FromFixtures("stable");
        var builds = await ScrapeAsync(handler, Context(handler) with { MinimumStableVersion = new SemVer(4, 5, 0), Lts = Lts },
            seed: SeedFor44AndBelow());

        Assert.All(builds.Where(b => b.Build.Subversion.StartsWith("4.5.", StringComparison.Ordinal)), b => Assert.Equal("lts", b.Build.Branch));
        Assert.All(builds.Where(b => b.Build.Subversion.StartsWith("5.2.", StringComparison.Ordinal)), b => Assert.Equal("lts", b.Build.Branch));
        Assert.All(builds.Where(b => b.Build.Subversion.StartsWith("5.0.", StringComparison.Ordinal)), b => Assert.Equal("stable", b.Build.Branch));
        Assert.Equal("stable", Assert.Single(builds, b => b.Build.Subversion == "5.3.0").Build.Branch);
        Assert.Equal("LTS", builds.First(b => b.Build.Subversion == "4.5.0").Build.DisplayLabel);

        // Without the table nothing is promoted.
        var plain = RecordedResponseHandler.FromFixtures("stable");
        var stable = await ScrapeAsync(plain, Context(plain) with { MinimumStableVersion = new SemVer(5, 2, 0), CacheDirectory = NewDir() });
        Assert.All(stable, b => Assert.Equal("stable", b.Build.Branch));
    }

    [Fact]
    public async Task Arm64_machine_gets_only_arm64_zips_and_x64_none()
    {
        var handler = RecordedResponseHandler.FromFixtures("stable");
        var arm = await ScrapeAsync(handler, Context(handler) with { Machine = "arm64", MinimumStableVersion = new SemVer(5, 2, 0) });

        Assert.Equal(6, arm.Count);
        Assert.All(arm, b => Assert.EndsWith("-windows-arm64.zip", b.Build.Link, StringComparison.Ordinal));
        Assert.Equal(302_503_373, arm.Single(b => b.Build.Subversion == "5.3.0").SizeBytes);

        // The cache holds both architectures, so an x64 run from the same cache serves x64 only, without requests.
        var x64Handler = RecordedResponseHandler.FromFixtures("stable");
        var x64 = await ScrapeAsync(x64Handler, Context(x64Handler) with { MinimumStableVersion = new SemVer(5, 2, 0) });
        Assert.Equal([Index], Urls(x64Handler));
        Assert.Equal(6, x64.Count);
        Assert.All(x64, b => Assert.EndsWith("-windows-x64.zip", b.Build.Link, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Minimum_version_skips_older_series_without_requests()
    {
        var handler = RecordedResponseHandler.FromFixtures("stable");
        var builds = await ScrapeAsync(handler, Context(handler) with { MinimumStableVersion = new SemVer(5, 3, 0) });

        handler.AssertAllMatched();
        Assert.Equal([Index, Folder53], Urls(handler));
        Assert.Equal("5.3.0", Assert.Single(builds).Build.Subversion);
        Assert.Equal(["5.3.0"], CachedFolders());
    }

    [Fact]
    public async Task Unchanged_dates_are_served_from_the_cache_without_folder_requests_or_a_save()
    {
        var first = RecordedResponseHandler.FromFixtures("stable");
        var ctx = Context(first) with { MinimumStableVersion = new SemVer(5, 2, 0) };
        var fresh = await ScrapeAsync(first, ctx);
        Assert.Equal("0.1", ApiFileVersionOnDisk());
        var written = File.GetLastWriteTimeUtc(CachePath);

        var second = RecordedResponseHandler.FromFixtures("stable");
        var cached = await ScrapeAsync(second, ctx with { Http = second.CreateClient() });

        second.AssertAllMatched();
        Assert.Equal([Index], Urls(second));
        Assert.Equal(fresh.Select(b => (b.Build, b.SizeBytes)), cached.Select(b => (b.Build, b.SizeBytes)));
        Assert.Equal("0.1", ApiFileVersionOnDisk());
        Assert.Equal(written, File.GetLastWriteTimeUtc(CachePath));
    }

    [Fact]
    public async Task Changed_folder_date_refetches_only_that_folder_and_bumps_the_minor()
    {
        // The index is served twice: first as recorded, then the hand-edited copy where Blender5.2/ is newer.
        var handler = RecordedResponseHandler.FromFixtures("stable")
            .Add(new FixtureEntry { Url = Index, File = "index-modified.html" }, Path.Combine(RecordedResponseHandler.FixturesRoot, "stable"));
        var ctx = Context(handler) with { MinimumStableVersion = new SemVer(5, 2, 0) };

        await ScrapeAsync(handler, ctx);
        var countsBefore = Urls(handler).Count;
        var again = await ScrapeAsync(handler, ctx);

        Assert.Equal([Index, Folder52], Urls(handler).Skip(countsBefore));
        Assert.Equal(6, again.Count);
        Assert.Equal("0.2", ApiFileVersionOnDisk());
        Assert.Equal("2026-11-11T10:00:00", ModifiedDateOnDisk("5.2.0"));
        Assert.Equal("2026-11-10T09:05:00", ModifiedDateOnDisk("5.3.0"));
    }

    [Fact]
    public async Task Seeded_cache_from_the_hosted_file_is_used_and_only_changed_series_are_fetched()
    {
        File.Copy(Fixtures.PathOf("upstream-api-stable_builds_api_windows.json"), CachePath);
        var handler = RecordedResponseHandler.FromFixtures("stable");
        var builds = await ScrapeAsync(handler, Context(handler) with { MinimumStableVersion = new SemVer(4, 2, 0), Lts = Lts });

        handler.AssertAllMatched();

        // 4.2, 4.3, 4.4, 5.0 and 5.1 match the seed's dates; 4.5 and 5.2 changed after it and 5.3 is new.
        Assert.Equal([Index, Folder45, Folder52, Folder53], Urls(handler));
        Assert.Equal("29.1", ApiFileVersionOnDisk());
        Assert.Contains(builds, b => b.Build.Subversion == "4.2.0" && b.Build.Branch == "lts" && b.SizeBytes is null);
        Assert.Contains(builds, b => b.Build.Subversion == "5.2.4" && b.SizeBytes == 405_779_142);
        Assert.DoesNotContain(builds, b => b.Build.Link.Contains("arm64", StringComparison.Ordinal));
        Assert.Equal(builds.Count, builds.Select(b => b.Build.Link).Distinct().Count());
        Assert.Equal(17, builds.Count(b => b.Build.Subversion.StartsWith("4.5.", StringComparison.Ordinal)));

        // Folders below the minimum stay in the cache untouched.
        Assert.Contains("2.48.0", CachedFolders());
    }

    [Fact]
    public async Task Cloudflare_challenge_then_listing_succeeds_after_one_wait()
    {
        var handler = new RecordedResponseHandler().AddText(Index, ChallengePage, status: 403)
            .AddManifest(Path.Combine(RecordedResponseHandler.FixturesRoot, "stable"));

        // A 403 challenge is still a challenge to retry, not an HTTP error.
        var builds = await ScrapeAsync(handler, Context(handler) with { MinimumStableVersion = new SemVer(5, 3, 0) });

        Assert.Single(builds);
        Assert.Equal([Index, Index, Folder53], Urls(handler));
        Assert.Equal([CloudflareChallenge.RetryDelay], _time.Delays);
    }

    [Fact]
    public async Task Cloudflare_challenge_three_times_fails_with_upstream_key()
    {
        var handler = new RecordedResponseHandler().AddText(Index, ChallengePage);

        var e = await Assert.ThrowsAsync<ScrapeSourceException>(() => ScrapeAsync(handler, Context(handler)));

        Assert.Equal(ScrapeMessageKeys.CloudflareBlocked, e.Error.MessageKey);
        Assert.Equal("stable", e.Error.SourceId);
        Assert.Equal(3, Urls(handler).Count);
        Assert.Equal([CloudflareChallenge.RetryDelay, CloudflareChallenge.RetryDelay], _time.Delays);
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task Index_without_release_folders_fails_with_no_releases()
    {
        var handler = new RecordedResponseHandler().AddText(Index, "<html><body><pre><a href=\"../\">../</a></pre></body></html>");

        var e = await Assert.ThrowsAsync<ScrapeSourceException>(() => ScrapeAsync(handler, Context(handler)));

        Assert.Equal(ScrapeMessageKeys.NoStableReleases, e.Error.MessageKey);
        Assert.Empty(_time.Delays);
    }

    [Fact]
    public async Task Server_error_on_the_index_fails_with_its_status()
    {
        var handler = new RecordedResponseHandler().AddText(Index, "<html>Bad gateway</html>", status: 502);

        var e = await Assert.ThrowsAsync<ScrapeSourceException>(() => ScrapeAsync(handler, Context(handler)));

        Assert.Equal(ScrapeMessageKeys.HttpStatus, e.Error.MessageKey);
        Assert.Equal(HttpStatusCode.BadGateway, e.Error.Status);
        Assert.Single(Urls(handler));
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("timeout")]
    public async Task Network_failure_on_the_index_fails_with_connection_failed(string fail)
    {
        var handler = new RecordedResponseHandler().Add(new FixtureEntry { Url = Index, Fail = fail });

        var e = await Assert.ThrowsAsync<ScrapeSourceException>(() => ScrapeAsync(handler, Context(handler)));

        Assert.Equal(ScrapeMessageKeys.ConnectionFailed, e.Error.MessageKey);
        Assert.Equal("time", Assert.Single(e.Error.MessageArgs).Key);
    }

    [Fact]
    public async Task Failed_folder_keeps_the_others_and_is_retried_next_time()
    {
        var folder = Path.Combine(RecordedResponseHandler.FixturesRoot, "stable");
        var handler = new RecordedResponseHandler()
            .Add(new FixtureEntry { Url = Index, File = "index.html" }, folder)
            .Add(new FixtureEntry { Url = Folder52, File = "Blender5.2.html" }, folder)
            .AddText(Folder53, "<html>Service unavailable</html>", status: 503);
        var ctx = Context(handler) with { MinimumStableVersion = new SemVer(5, 2, 0) };

        var yielded = new List<AvailableBuild>();
        var e = await Assert.ThrowsAsync<ScrapeSourceException>(async () =>
        {
            await foreach (var b in _source.ScrapeAsync(ctx, TestContext.Current.CancellationToken))
            {
                yielded.Add(b);
            }
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, e.Error.Status);
        Assert.Equal(5, yielded.Count);
        Assert.Equal(["5.2.0"], CachedFolders());

        // The next check fetches 5.3 again (5.2 is cached).
        var retry = RecordedResponseHandler.FromFixtures("stable");
        var builds = await ScrapeAsync(retry, ctx with { Http = retry.CreateClient() });
        Assert.Equal([Index, Folder53], Urls(retry));
        Assert.Equal(6, builds.Count);
    }

    [Fact]
    public async Task Folders_of_one_series_share_a_cache_entry_dated_with_the_newest()
    {
        const string index = """
            <html><body><pre><a href="../">../</a>
            <a href="Blender2.48/">Blender2.48/</a>                                       15-Oct-2008 03:11                   -
            <a href="Blender2.48a/">Blender2.48a/</a>                                      24-Nov-2008 16:15                   -
            </pre></body></html>
            """;
        const string a = """<a href="blender-2.48-windows.zip">x</a> 14-Oct-2008 10:00  100""";
        const string b = """<a href="blender-2.48a-windows64.zip">x</a> 02-Nov-2008 15:21  200""";

        RecordedResponseHandler Handler() => new RecordedResponseHandler().AddText(Index, index)
            .AddText(Index + "Blender2.48/", $"<pre>{a}\n</pre>").AddText(Index + "Blender2.48a/", $"<pre>{b}\n</pre>");

        var first = Handler();
        var ctx = Context(first) with { MinimumStableVersion = StableBuildSource.AllVersionsMinimum };
        var builds = await ScrapeAsync(first, ctx);

        // blender-2.48-windows.zip fails the Windows filter (no "64"); the 2.48a zip is kept.
        Assert.Equal("2.48.0-a", Assert.Single(builds).Build.Subversion);
        Assert.Equal(["2.48.0"], CachedFolders());
        Assert.Equal("2008-11-24T16:15:00", ModifiedDateOnDisk("2.48.0"));

        var second = Handler();
        var again = await ScrapeAsync(second, ctx with { Http = second.CreateClient() });
        Assert.Equal([Index], Urls(second));
        Assert.Single(again);
    }

    [Fact]
    public async Task Missing_listing_date_falls_back_to_the_folder_date()
    {
        const string index = """<pre><a href="Blender9.9/">Blender9.9/</a>      01-Feb-2030 12:00     -</pre>""";
        const string folder = """<pre><a href="blender-9.9.0-windows-x64.zip">blender-9.9.0-windows-x64.zip</a></pre>""";
        var handler = new RecordedResponseHandler().AddText(Index, index).AddText(Index + "Blender9.9/", folder);

        var build = Assert.Single(await ScrapeAsync(handler, Context(handler)));

        Assert.Equal("2030-02-01T14:00:00+02:00", build.Build.CommitTime.ToString());
        Assert.Null(build.SizeBytes);
    }

    [Fact]
    public async Task Rebuild_refetches_every_series_from_2_48_and_bumps_the_major()
    {
        const string index = """<pre><a href="Blender2.49/">Blender2.49/</a>      04-Jun-2009 09:24     -</pre>""";
        const string folder = """<pre><a href="blender-2.49-windows64.zip">x</a>  04-Jun-2009 09:00   5</pre>""";
        await File.WriteAllTextAsync(CachePath, """{"api_file_version": "29.3", "folders": {"2.49.0": {"assets": [], "modified_date": "2009-06-04T09:24:00"}}}""",
            TestContext.Current.CancellationToken);
        var handler = new RecordedResponseHandler().AddText(Index, index).AddText(Index + "Blender2.49/", folder);

        var builds = await ScrapeAsync(handler, Context(handler) with { RebuildStableCache = true });

        Assert.Equal([Index, Index + "Blender2.49/"], Urls(handler));
        Assert.Single(builds);
        Assert.Equal("30.0", ApiFileVersionOnDisk());
    }

    [Fact]
    public async Task Saved_cache_is_upstream_format_and_reloads()
    {
        var handler = RecordedResponseHandler.FromFixtures("stable");
        await ScrapeAsync(handler, Context(handler) with { MinimumStableVersion = new SemVer(5, 3, 0) });

        var text = await File.ReadAllTextAsync(CachePath, TestContext.Current.CancellationToken);
        Assert.StartsWith("{\n \"api_file_version\": \"0.1\",\n \"folders\": {", text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        var cache = ScraperCache.FromJson(text);
        var folder = cache[new SemVer(5, 3, 0)];
        Assert.True(folder.ModifiedDate.IsNaive);
        Assert.Equal(2, folder.Assets.Count); // x64 and arm64 zips; the architecture is chosen when yielding
        Assert.Empty(Directory.GetFiles(_cacheDir, "*.tmp"));
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var handler = RecordedResponseHandler.FromFixtures("stable");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in _source.ScrapeAsync(Context(handler), cts.Token))
            {
            }
        });
    }

    [Theory]
    [InlineData("amd64", "blender-5.3.0-windows-x64.zip", true)]
    [InlineData("amd64", "blender-5.3.0-windows-arm64.zip", false)]
    [InlineData("arm64", "blender-5.3.0-windows-arm64.zip", true)]
    [InlineData("arm64", "blender-5.3.0-windows-x64.zip", false)]
    [InlineData("aarch64", "blender-5.3.0-linux-x64.tar.xz", false)]
    public void Architecture_rule(string machine, string link, bool passes) =>
        Assert.Equal(passes, StableBuildSource.PassesArchitecture(machine, link));

    [Fact]
    public void Folder_page_parses_dates_and_sizes()
    {
        var html = File.ReadAllText(Path.Combine(RecordedResponseHandler.FixturesRoot, "stable", "Blender5.3.html"));

        var files = StableIndexParser.ParseFolder(html, BuildPlatform.FileNameFilter(HostOs.Windows));

        Assert.Equal(
            [
                new StableFolderFile("blender-5.3.0-windows-arm64.zip", new DateTime(2026, 11, 10, 8, 56, 0), 302_503_373),
                new StableFolderFile("blender-5.3.0-windows-x64.zip", new DateTime(2026, 11, 10, 8, 58, 0), 425_299_259),
            ],
            files);
        Assert.Equal("blender-5.3.0-macos-arm64.dmg", Assert.Single(StableIndexParser.ParseFolder(html, BuildPlatform.FileNameFilter(HostOs.MacOS))).Href);
    }

    [Fact]
    public void Folder_page_date_falls_back_to_the_anchor_line()
    {
        const string html = "<pre><a href=\"blender-1.2.3-windows-x64.zip\">blender</a><!-- note --> 05-Jan-2020 10:11 7\n</pre>";

        var file = Assert.Single(StableIndexParser.ParseFolder(html, BuildPlatform.FileNameFilter(HostOs.Windows)));

        // The anchor is followed by a comment, not text, so the date comes from the line scan; there is no size.
        Assert.Equal(new DateTime(2020, 1, 5, 10, 11, 0), file.Modified);
        Assert.Null(file.SizeBytes);
    }

    private ScrapeContext Context(RecordedResponseHandler handler) => new()
    {
        Http = handler.CreateClient(),
        CacheDirectory = _cacheDir,
        Os = HostOs.Windows,
        Machine = "amd64",
        Time = _time,
    };

    private async Task<List<AvailableBuild>> ScrapeAsync(RecordedResponseHandler handler, ScrapeContext ctx, string? seed = null)
    {
        _ = handler;
        if (seed is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(ctx.CacheDirectory, StableBuildSource.CacheFileName), seed, TestContext.Current.CancellationToken);
        }

        var list = new List<AvailableBuild>();
        await foreach (var b in _source.ScrapeAsync(ctx, TestContext.Current.CancellationToken))
        {
            list.Add(b);
        }

        return list;
    }

    /// <summary>The hosted seed with only the 5.0 and 5.1 folders kept, so a 4.5+ scrape needs just the recorded folders.</summary>
    private static string SeedFor44AndBelow()
    {
        var cache = ScraperCache.FromJson(Fixtures.ReadText("upstream-api-stable_builds_api_windows.json"));
        foreach (var key in cache.Folders.Keys.Where(v => v != new SemVer(5, 0, 0) && v != new SemVer(5, 1, 0)).ToList())
        {
            cache.Folders.Remove(key);
        }

        return cache.ToJson();
    }

    private string NewDir()
    {
        var dir = Path.Combine(_cacheDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static List<string> Urls(RecordedResponseHandler handler) => handler.Requests.Select(r => r.Uri.AbsoluteUri).ToList();

    private List<string> CachedFolders() => ScraperCache.FromJson(File.ReadAllText(CachePath)).Folders.Keys.Select(v => v.ToString()).ToList();

    private string? ApiFileVersionOnDisk()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
        return doc.RootElement.GetProperty("api_file_version").GetString();
    }

    private string ModifiedDateOnDisk(string version)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
        return doc.RootElement.GetProperty("folders").GetProperty(version).GetProperty("modified_date").GetString()!;
    }

    /// <summary>A clock in a fixed zone whose timers fire at once, recording each requested delay.</summary>
    private sealed class FakeTime(TimeZoneInfo zone) : TimeProvider
    {
        private readonly ConcurrentQueue<TimeSpan> _delays = new();

        public IReadOnlyList<TimeSpan> Delays => [.. _delays];

        public override TimeZoneInfo LocalTimeZone => zone;

        public override DateTimeOffset GetUtcNow() => new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                _delays.Enqueue(dueTime);
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }

            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
