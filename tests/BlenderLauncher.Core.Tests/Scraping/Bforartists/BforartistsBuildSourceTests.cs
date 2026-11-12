using System.Net;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Scraping.Bforartists;
using BlenderLauncher.Core.Tests.Net;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Tests.Scraping.Bforartists;

/// <summary>
/// Ports of upstream's <c>tests/backend/test_bfa_scraper.py</c> (marked "Upstream:"), then the port's own tests
/// over the pages recorded in <c>Fixtures/Net/bforartists/</c>.
/// </summary>
public sealed class BforartistsBuildSourceTests : IDisposable
{
    private const string Stable = """
        <details><summary>Bforartists 5.2.0 (5 files)</summary>
        <a href="Bforartists-5.2.0-Windows.zip">Windows</a>
        <a href="Bforartists-5.2.0-Windows.zip">Duplicate</a>
        <a href="Bforartists-5.2.0-Mac-Intel.dmg">Intel</a>
        <a href="Bforartists-5.2.0-Mac-Silicon.dmg">Silicon</a>
        <a href="Bforartists-5.2.0-Linux.tar.xz">Linux</a>
        <a href="Install.exe">Installer</a>
        <a href="https://example.com/unwanted-Windows.zip">External</a>
        </details>
        """;

    private const string Dev = """
        <details><summary>2026-10-27 <span>2 files</span></summary>
        <a href="builds/windows-devbuild-27-10-2026.zip">Windows</a>
        <a href="builds/linux-devbuild-27-10-2026.tar.xz">Linux</a>
        </details><details><summary>2026-99-99</summary><a href="bad.zip">Bad</a></details>
        """;

    private const string LastModified = "Mon, 19 Oct 2026 00:00:00 GMT";

    private static readonly IsoTimestamp Date = IsoTimestamp.Aware(new DateTime(2026, 10, 19), TimeSpan.Zero);

    private static readonly string Downloads = BforartistsBuildSource.DownloadsUrl.AbsoluteUri;

    private static readonly string DevBuilds = BforartistsBuildSource.DevBuildsUrl.AbsoluteUri;

    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "bl-bfa-tests-" + Guid.NewGuid().ToString("N"));

    public BforartistsBuildSourceTests() => Directory.CreateDirectory(_cacheDir);

    private string CachePath => Path.Combine(_cacheDir, BforartistsBuildSource.CacheFileName);

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

    /// <summary>Upstream's fixture: both inline pages, and a <c>HEAD</c> answer with <see cref="LastModified"/> for every file.</summary>
    private static RecordedResponseHandler UpstreamHandler(int stableStatus = 200)
    {
        var handler = new RecordedResponseHandler()
            .AddText(Downloads, stableStatus == 200 ? Stable : "unavailable", status: stableStatus)
            .AddText(DevBuilds, Dev);
        foreach (var file in new[] { "Windows.zip", "Mac-Intel.dmg", "Mac-Silicon.dmg", "Linux.tar.xz" })
        {
            handler.Add(new FixtureEntry
            {
                Method = "HEAD",
                Url = Downloads + "Bforartists-5.2.0-" + file,
                Headers = [new("Last-Modified", LastModified), new("Content-Length", "123456")],
            });
        }

        return handler;
    }

    private ScrapeContext Context(HttpClient http, HostOs os = HostOs.Windows, string machine = "amd64") =>
        new() { Http = http, CacheDirectory = _cacheDir, Os = os, Machine = machine };

    private static async Task<(List<AvailableBuild> Builds, ScrapeSourceException? Error)> Collect(IBuildSource source, ScrapeContext context)
    {
        var builds = new List<AvailableBuild>();
        try
        {
            await foreach (var b in source.ScrapeAsync(context, TestContext.Current.CancellationToken))
            {
                builds.Add(b);
            }
        }
        catch (ScrapeSourceException e)
        {
            return (builds, e);
        }

        return (builds, null);
    }

    // ---- Upstream: test_indexes ----

    [Theory]
    [InlineData(HostOs.Windows, "amd64", "Windows.zip", 2)]
    [InlineData(HostOs.Linux, "amd64", "Linux.tar.xz", 2)]
    [InlineData(HostOs.MacOS, "arm64", "Mac-Silicon.dmg", 1)]
    [InlineData(HostOs.MacOS, "amd64", "Mac-Intel.dmg", 1)]
    public async Task Upstream_indexes(HostOs os, string machine, string suffix, int count)
    {
        var handler = UpstreamHandler();
        using var http = handler.CreateClient();

        var (builds, error) = await Collect(new BforartistsBuildSource(), Context(http, os, machine));

        Assert.Null(error);
        handler.AssertAllMatched();
        Assert.Equal(count, builds.Count);
        var first = builds[0].Build;
        Assert.Equal(Downloads + "Bforartists-5.2.0-" + suffix, first.Link);
        Assert.Equal(Date, first.CommitTime);
        Assert.True(first.IsValid);
        Assert.Equal(123456, builds[0].SizeBytes);
        if (count == 2)
        {
            var dev = builds[1].Build;
            Assert.True(dev.IsBfaDev);
            Assert.Equal("Dev", dev.DisplayVersion);
            Assert.Equal(IsoTimestamp.Aware(new DateTime(2026, 10, 27), TimeSpan.Zero), dev.CommitTime);
            Assert.False(dev.Matches(first));
            Assert.Null(builds[1].SizeBytes);
        }
    }

    // ---- Upstream: test_replaces_legacy_cache_and_reuses_new_timestamps ----

    [Fact]
    public async Task Upstream_replaces_legacy_cache_and_reuses_new_timestamps()
    {
        var old = new ScraperCache();
        old.NewBuild(new SemVer(5, 2, 0), Date).Assets.Add(new BuildInfo
        {
            Link = "https://cloud.bforartists.de/old.zip", Subversion = "5.2.0", CommitTime = Date, Branch = "bforartists",
        });
        await old.SaveAsync(CachePath, TestContext.Current.CancellationToken);
        var source = new BforartistsBuildSource();
        var handler = UpstreamHandler();
        using var http = handler.CreateClient();

        var (builds, _) = await Collect(source, Context(http));

        var saved = await ScraperCache.LoadOrEmptyAsync(CachePath, ct: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(saved.Folders.Values.SelectMany(f => f.Assets), a => a.Link.Contains("cloud.bforartists.de", StringComparison.Ordinal));
        var sent = handler.Requests.Count;

        var (again, _) = await Collect(source, Context(http));

        Assert.Equal(builds, again);
        Assert.All(handler.Requests.Skip(sent), r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    // ---- Upstream: test_failed_index_does_not_hide_other_repository ----

    [Fact]
    public async Task Upstream_failed_index_does_not_hide_other_repository()
    {
        var handler = UpstreamHandler(stableStatus: 503);
        using var http = handler.CreateClient();

        var (builds, error) = await Collect(new BforartistsBuildSource(), Context(http));

        var build = Assert.Single(builds);
        Assert.True(build.Build.IsBfaDev);

        // The port also reports the failed index once the other one is done (upstream only logged it).
        Assert.NotNull(error);
        Assert.Equal(ScrapeMessageKeys.HttpStatus, error.Error.MessageKey);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.Error.Status);
        Assert.Equal(BuildSourceIds.Bforartists, error.Error.SourceId);
    }

    // ---- Upstream: test_no_wrong_architecture_fallback ----

    [Fact]
    public void Upstream_no_wrong_architecture_fallback() =>
        Assert.Empty(BforartistsBuildSource.FilterMacCandidates(["Mac-Silicon.dmg"], n => n, "amd64"));

    // ---- Upstream: test_legacy_windows_archives_and_source_exclusion ----

    [Theory]
    [InlineData("bforartists3_322.zip", true)]
    [InlineData("bforartists_win64_100.zip", true)]
    [InlineData("bforartists_win32_100.zip", false)]
    [InlineData("Bforartists_master_100.zip", false)]
    public void Upstream_legacy_windows_archives_and_source_exclusion(string name, bool expected) =>
        Assert.Equal(expected, BforartistsBuildSource.MatchesPlatform(HostOs.Windows, name));

    // ---- Upstream: test_developer_identity_and_update_isolation (identity half) ----
    // The update-detection half (_new_version_available) belongs to the update manager, which is not ported yet.

    [Fact]
    public void Upstream_developer_identity()
    {
        var stable = new BuildInfo { Link = "stable", Subversion = "5.2.0", CommitTime = Date, Branch = "bforartists" };
        var devTime = IsoTimestamp.Aware(new DateTime(2026, 10, 27), TimeSpan.Zero);
        var dev = new BuildInfo { Link = "dev", Subversion = "0.0.20261027-dev", CommitTime = devTime, Branch = "bforartists" };
        var installed = new BuildInfo { Link = "installed", Subversion = "5.3.0-dev", BuildHash = "abc", CommitTime = devTime, Branch = "bforartists" };

        Assert.True(installed.Matches(dev));
        Assert.False(installed.Matches(stable));
        Assert.False(dev.Matches(stable));
    }

    // ---- Upstream: test_macos_prefers_native_over_universal ----

    [Theory]
    [InlineData("arm64", "Silicon")]
    [InlineData("amd64", "Intel")]
    public void Upstream_macos_prefers_native_over_universal(string machine, string expected)
    {
        string[] candidates = ["Universal", "Intel", "Silicon"];
        var selected = BforartistsBuildSource.FilterMacCandidates(candidates.Select(a => $"Bforartists-4.5.2-Mac-{a}.dmg"), n => n, machine);

        Assert.Contains(expected, Assert.Single(selected), StringComparison.Ordinal);
    }

    // ---- Upstream: test_macos_universal_fallback ----

    [Theory]
    [InlineData("arm64", "Intel")]
    [InlineData("amd64", "Silicon")]
    public void Upstream_macos_universal_fallback(string machine, string opposite)
    {
        string[] candidates = [$"Mac-{opposite}.dmg", "Mac-Universal.dmg"];

        Assert.Equal(candidates[1..], BforartistsBuildSource.FilterMacCandidates(candidates, n => n, machine));
    }

    // ---- The port's own tests ----

    [Theory]
    [InlineData("Bforartists 5.2.0 (5 files)", false, "5.2.0", null)]
    [InlineData("bforartists   4.5.2", false, "4.5.2", null)]
    [InlineData("Bforartists 5.02.0", false, null, null)]
    [InlineData("Bforartists 1.0", false, null, null)]
    [InlineData("2026-10-27 2 files", true, "0.0.20261027-dev", "2026-10-27T00:00:00+00:00")]
    [InlineData("2026-99-99", true, null, null)]
    [InlineData("Bforartists 5.2.0", true, null, null)]
    public void Headings(string title, bool developer, string? version, string? date)
    {
        var ok = BforartistsBuildSource.TryReadHeading(title, developer, out var v, out var d);

        Assert.Equal(version is not null, ok);
        if (ok)
        {
            Assert.Equal(version, v.ToString());
            Assert.Equal(date, d?.ToString());
        }
    }

    [Theory]
    [InlineData(HostOs.Windows, "Bforartists-5.2.3-Windows.zip", true)]
    [InlineData(HostOs.Windows, "Bforartists-4.1.1-mac-silicon.zip", false)]
    [InlineData(HostOs.Windows, "bforartists2_alpha_090.zip", false)]
    [InlineData(HostOs.Windows, "Bforartists-5.2.3-Windows.exe", false)]
    [InlineData(HostOs.Linux, "linux-devbuild-05-07-2026.tar.xz", true)]
    [InlineData(HostOs.Linux, "Bforartists-5.2.3-Linux.tar.gz", true)]
    [InlineData(HostOs.Linux, "Bforartists-5.2.3-Linux.zip", false)]
    [InlineData(HostOs.MacOS, "Bforartists-5.2.3-Mac.DMG", true)]
    public void Platform_file_names(HostOs os, string name, bool expected) =>
        Assert.Equal(expected, BforartistsBuildSource.MatchesPlatform(os, name));

    [Fact]
    public void File_name_is_unescaped() =>
        Assert.Equal("Bforartists 5.2.0 Windows.zip",
            BforartistsBuildSource.FileNameOf(new Uri("https://downloads.bforartists.com/sub/Bforartists%205.2.0%20Windows.zip")));

    [Fact]
    public async Task Recorded_indexes_give_every_windows_build_with_date_and_size()
    {
        var handler = RecordedResponseHandler.FromFixtures("bforartists");
        using var http = handler.CreateClient();
        var source = new BforartistsBuildSource();

        var (builds, error) = await Collect(source, Context(http));

        Assert.Null(error);
        handler.AssertAllMatched();
        var stable = builds.Where(b => !b.Build.IsBfaDev).ToList();
        var dev = builds.Where(b => b.Build.IsBfaDev).ToList();

        // Every "Bforartists M.m.p" section with a Windows zip, 5.2.3 down to 0.9.5 (win64 only for 1.0.0). The
        // "Other" and "Pre-releases" sections have no version in the heading, so they give nothing.
        Assert.Equal(56, stable.Count);
        Assert.Equal("https://downloads.bforartists.com/Bforartists_win64_095.zip", stable[^1].Build.Link);
        Assert.Equal("0.9.5", stable[^1].Build.Subversion);
        Assert.Equal(33, dev.Count);
        Assert.All(builds, b => Assert.Equal("bforartists", b.Build.Branch));
        Assert.All(builds, b => Assert.Equal("bforartists.exe", b.Build.CustomExecutable));
        Assert.All(builds, b => Assert.True(b.Build.IsValid));
        Assert.All(builds, b => Assert.True(BuildPlatform.PassesPostFilter(HostOs.Windows, "amd64", b.Build.Link)));
        Assert.All(stable, b => Assert.NotNull(b.SizeBytes));
        Assert.All(dev, b => Assert.Null(b.SizeBytes));

        var latest = stable[0];
        Assert.Equal("https://downloads.bforartists.com/Bforartists-5.2.3-Windows.zip", latest.Build.Link);
        Assert.Equal("5.2.3", latest.Build.Subversion);
        Assert.Equal("2026-10-29T23:27:07+00:00", latest.Build.CommitTime.ToString());
        Assert.Equal(510767508, latest.SizeBytes);

        var newestDev = dev[0];
        Assert.Equal("https://devbuilds.bforartists.com/builds/windows-devbuild-10-11-2026.zip", newestDev.Build.Link);
        Assert.Equal("0.0.20261110-dev", newestDev.Build.Subversion);
        Assert.Equal("2026-11-10T00:00:00+00:00", newestDev.Build.CommitTime.ToString());

        // The cache holds exactly what was yielded, compact as upstream writes it, with aware dates.
        var text = await File.ReadAllTextAsync(CachePath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain("api_file_version", text, StringComparison.Ordinal);
        var saved = ScraperCache.FromJson(text);
        Assert.Equal(builds.Select(b => b.Build.Link), saved.Folders.Values.SelectMany(f => f.Assets).Select(a => a.Link));
        Assert.All(saved.Folders.Values, f => Assert.False(f.ModifiedDate.IsNaive));
        Assert.Equal(latest.Build.CommitTime, saved[new SemVer(5, 2, 3)].ModifiedDate);
    }

    [Fact]
    public async Task Second_scrape_reads_dates_from_the_cache_and_sizes_from_the_sidecar()
    {
        var handler = RecordedResponseHandler.FromFixtures("bforartists");
        using var http = handler.CreateClient();
        var source = new BforartistsBuildSource();
        var (first, _) = await Collect(source, Context(http));
        var sent = handler.Requests.Count;

        var (second, error) = await Collect(source, Context(http));

        Assert.Null(error);
        Assert.Equal(first, second);
        Assert.Equal(2, handler.Requests.Count - sent);

        // A new instance (an app restart) has the dates from the cache file and the sizes from the sidecar,
        // the same way the stable source keeps its sizes.
        sent = handler.Requests.Count;
        var (restarted, _) = await Collect(new BforartistsBuildSource(), Context(http));
        Assert.Equal(first, restarted);
        Assert.Equal(2, handler.Requests.Count - sent);
        Assert.Contains(restarted, b => b.SizeBytes is not null);
        Assert.True(File.Exists(Path.Combine(_cacheDir, BforartistsBuildSource.SizesFileName)));
    }

    [Fact]
    public async Task Failed_index_keeps_its_cached_entries_for_the_next_scrape()
    {
        var recorded = RecordedResponseHandler.FromFixtures("bforartists");
        using (var http = recorded.CreateClient())
        {
            await Collect(new BforartistsBuildSource(), Context(http));
        }

        var failing = new RecordedResponseHandler()
            .Add(new FixtureEntry { Url = Downloads, Fail = "connection" })
            .AddText(DevBuilds, Dev);
        using var http2 = failing.CreateClient();

        var (builds, error) = await Collect(new BforartistsBuildSource(), Context(http2));

        Assert.All(builds, b => Assert.True(b.Build.IsBfaDev));
        Assert.Single(builds);
        Assert.NotNull(error);
        Assert.Equal(ScrapeMessageKeys.ConnectionFailed, error.Error.MessageKey);

        var saved = await ScraperCache.LoadOrEmptyAsync(CachePath, ct: TestContext.Current.CancellationToken);
        var links = saved.Folders.Values.SelectMany(f => f.Assets).Select(a => a.Link).ToList();
        Assert.Contains("https://downloads.bforartists.com/Bforartists-5.2.3-Windows.zip", links);
        Assert.Equal(56, links.Count(l => l.StartsWith(Downloads, StringComparison.Ordinal)));

        // The dev index answered, so its old entries are replaced by what it lists now.
        Assert.Equal(["https://devbuilds.bforartists.com/builds/windows-devbuild-27-10-2026.zip"],
            links.Where(l => l.StartsWith(DevBuilds, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Entry_without_a_date_is_skipped()
    {
        var handler = new RecordedResponseHandler()
            .AddText(Downloads, Stable)
            .AddText(DevBuilds, "<html></html>")
            .Add(new FixtureEntry { Method = "HEAD", Url = Downloads + "Bforartists-5.2.0-Windows.zip", Headers = [new("Content-Length", "5")] });
        using var http = handler.CreateClient();

        var (builds, error) = await Collect(new BforartistsBuildSource(), Context(http));

        Assert.Null(error);
        Assert.Empty(builds);
    }

    [Fact]
    public async Task Head_failure_or_error_status_skips_the_entry()
    {
        var handler = new RecordedResponseHandler()
            .AddText(Downloads, Stable)
            .AddText(DevBuilds, Dev)
            .Add(new FixtureEntry { Method = "HEAD", Url = Downloads + "Bforartists-5.2.0-Windows.zip", Status = 404 });
        using var http = handler.CreateClient();

        var (builds, error) = await Collect(new BforartistsBuildSource(), Context(http));

        Assert.Null(error);
        Assert.True(Assert.Single(builds).Build.IsBfaDev);
        Assert.False(File.ReadAllText(CachePath).Contains("5.2.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Malformed_cache_is_ignored()
    {
        await File.WriteAllTextAsync(CachePath, "{not json", TestContext.Current.CancellationToken);
        var handler = UpstreamHandler();
        using var http = handler.CreateClient();

        var (builds, error) = await Collect(new BforartistsBuildSource(), Context(http));

        Assert.Null(error);
        Assert.Equal(2, builds.Count);
        Assert.Equal(2, ScraperCache.FromJson(File.ReadAllText(CachePath)).Folders.Count);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var handler = UpstreamHandler();
        using var http = handler.CreateClient();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in new BforartistsBuildSource().ScrapeAsync(Context(http), cts.Token))
            {
            }
        });
    }

    [Fact]
    public void Source_identity()
    {
        var source = new BforartistsBuildSource();
        Assert.Equal("bforartists", source.Id);
        Assert.Equal([Channel.Bforartists], source.Channels);
        Assert.Equal("bforartists.exe", BforartistsBuildSource.ExecutableFor(HostOs.Windows));
        Assert.Equal("bforartists", BforartistsBuildSource.ExecutableFor(HostOs.Linux));
        Assert.Null(BforartistsBuildSource.ExecutableFor(HostOs.MacOS));
    }

    /// <summary>Live smoke test (never in a normal run): <c>dotnet test … -- --explicit only --filter-method "*Live_smoke"</c>.</summary>
    [Fact(Explicit = true)]
    public async Task Live_smoke()
    {
        var options = Core.Net.ConnectionOptions.Default;
        using var http = Core.Net.HttpClientProvider.CreateClient(options, Core.Net.HttpClientProvider.CreatePrimaryHandler(options));

        var (builds, error) = await Collect(new BforartistsBuildSource(), Context(http));

        Assert.Null(error);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"bforartists: {builds.Count(b => !b.Build.IsBfaDev)} stable, {builds.Count(b => b.Build.IsBfaDev)} dev; newest {builds.FirstOrDefault()?.Build.Subversion}");
        Assert.Contains(builds, b => !b.Build.IsBfaDev);
        Assert.Contains(builds, b => b.Build.IsBfaDev);
    }
}
