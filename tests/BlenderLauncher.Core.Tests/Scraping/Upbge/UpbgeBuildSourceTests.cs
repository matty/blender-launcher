using System.Net;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Scraping.Upbge;
using BlenderLauncher.Core.Tests.Net;

namespace BlenderLauncher.Core.Tests.Scraping.Upbge;

/// <summary>
/// The UPBGE sources over the GitHub responses recorded (and trimmed) in <c>Fixtures/Net/upbge/</c>, plus inline
/// cases. Upstream has no UPBGE scraper tests; the rules come from <c>threads/scraping/upbge.py</c>.
/// </summary>
public sealed class UpbgeBuildSourceTests
{
    private static readonly string Releases = UpbgeReleaseFeed.ReleasesUrl.AbsoluteUri;

    private static readonly string Tags = UpbgeReleaseFeed.TagsUrl.AbsoluteUri;

    private static ScrapeContext Context(HttpClient http, HostOs os = HostOs.Windows) =>
        new() { Http = http, CacheDirectory = Path.GetTempPath(), Os = os, Machine = "amd64" };

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

    private static UpbgeReleaseSnapshot Snapshot(params GitHubRelease[] releases) =>
        new(releases, new Dictionary<string, string> { ["weekly-build-7"] = "0123456789ab" });

    private static GitHubRelease Release(string tag, params string[] assets) => new()
    {
        TagName = tag,
        PublishedAt = "2026-11-08T09:26:10Z",
        Assets = [.. assets.Select(a => new GitHubReleaseAsset { Name = a, BrowserDownloadUrl = "https://github.com/UPBGE/upbge/releases/download/" + tag + "/" + a, Size = 42 })],
    };

    private static GitHubRelease Set(GitHubRelease release, Action<GitHubRelease> change)
    {
        change(release);
        return release;
    }

    [Fact]
    public async Task Recorded_stable_releases()
    {
        var handler = RecordedResponseHandler.FromFixtures("upbge");
        using var http = handler.CreateClient();

        var (builds, error) = await Collect(new UpbgeBuildSource(UpbgeKind.Stable), Context(http));

        Assert.Null(error);
        handler.AssertAllMatched();

        // v0.50, v0.44, v0.36.1 and v0.30; the 0.2.x releases are below 0.30, and the windows7 packages give way.
        Assert.Equal(
            [
                "https://github.com/UPBGE/upbge/releases/download/v0.50/upbge-0.50-windows-x64.7z",
                "https://github.com/UPBGE/upbge/releases/download/v0.44/upbge-0.44-windows-x86_64.7z",
                "https://github.com/UPBGE/upbge/releases/download/v0.36.1/upbge-0.36.1-windows-x86_64.7z",
                "https://github.com/UPBGE/upbge/releases/download/v0.30/UPBGE-0.30-windows-x86_64.7z",
            ],
            builds.Select(b => b.Build.Link));
        Assert.Equal(["0.50.0", "0.44.0", "0.36.1", "0.30.0"], builds.Select(b => b.Build.Subversion));
        var first = builds[0];
        Assert.Equal("upbge-stable", first.Build.Branch);
        Assert.Equal("2026-01-04T16:34:10+00:00", first.Build.CommitTime.ToString());
        Assert.Equal(293316768, first.SizeBytes);
        Assert.Equal("blender.exe", first.Build.CustomExecutable);
        Assert.All(builds, b => Assert.Null(b.Build.BuildHash));
        Assert.All(builds, b => Assert.True(b.Build.IsValid));
        Assert.All(builds, b => Assert.True(BuildPlatform.PassesPostFilter(HostOs.Windows, "amd64", b.Build.Link)));
    }

    [Fact]
    public async Task Recorded_weekly_releases()
    {
        var handler = RecordedResponseHandler.FromFixtures("upbge");
        using var http = handler.CreateClient();

        var (builds, error) = await Collect(new UpbgeBuildSource(UpbgeKind.Weekly), Context(http));

        Assert.Null(error);
        Assert.Equal(["0.53.0", "0.53.0", "0.51.0", "0.45.0"], builds.Select(b => b.Build.Subversion));
        var latest = builds[0];
        Assert.Equal("https://github.com/UPBGE/upbge/releases/download/weekly-build-108/upbge-0.53-alpha-windows-x86_64-2026-11-08.zip", latest.Build.Link);
        Assert.Equal("upbge-weekly", latest.Build.Branch);
        Assert.Equal("c11a0cccfac0", latest.Build.BuildHash);
        Assert.Equal("2026-11-08T09:26:10+00:00", latest.Build.CommitTime.ToString());
        Assert.Equal(364563735, latest.SizeBytes);
        Assert.Equal("blender.exe", latest.Build.CustomExecutable);
        Assert.All(builds, b => Assert.Equal(12, b.Build.BuildHash?.Length));

        // Two weeklies of one version stay apart by hash, as their full versions differ.
        Assert.NotEqual(builds[0].Build.FullSemVersion, builds[1].Build.FullSemVersion);
    }

    [Fact]
    public async Task Pair_shares_one_fetch()
    {
        var handler = RecordedResponseHandler.FromFixtures("upbge");
        using var http = handler.CreateClient();
        var (stable, weekly) = UpbgeBuildSource.CreatePair();

        var results = await Task.WhenAll(Collect(stable, Context(http)), Collect(weekly, Context(http)));

        Assert.Equal(4, results[0].Builds.Count);
        Assert.Equal(4, results[1].Builds.Count);
        Assert.Equal(1, handler.Requests.Count(r => r.Uri.AbsoluteUri == Releases));
        Assert.Equal(1, handler.Requests.Count(r => r.Uri.AbsoluteUri == Tags));
        Assert.Equal("upbge-stable", stable.Id);
        Assert.Equal("upbge-weekly", weekly.Id);
        Assert.Equal([Channel.UpbgeStable], stable.Channels);
        Assert.Equal([Channel.UpbgeWeekly], weekly.Channels);
    }

    [Fact]
    public async Task Separate_sources_fetch_each_time()
    {
        var handler = RecordedResponseHandler.FromFixtures("upbge");
        using var http = handler.CreateClient();

        await Collect(new UpbgeBuildSource(UpbgeKind.Stable), Context(http));
        await Collect(new UpbgeBuildSource(UpbgeKind.Weekly), Context(http));

        Assert.Equal(2, handler.Requests.Count(r => r.Uri.AbsoluteUri == Releases));
        Assert.All(handler.Requests, r => Assert.Contains("application/vnd.github+json", r.Headers["Accept"], StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rate_limit_headers_give_the_rate_limit_error_with_reset_time()
    {
        var handler = new RecordedResponseHandler().AddText(Releases,
            """{"message":"API rate limit exceeded for 203.0.113.1.","documentation_url":"https://docs.github.com/rest"}""",
            "application/json", 403, "GET",
            new("X-RateLimit-Limit", "60"), new("X-RateLimit-Remaining", "0"), new("X-RateLimit-Reset", "1794405160"));
        using var http = handler.CreateClient();

        var (builds, error) = await Collect(new UpbgeBuildSource(UpbgeKind.Stable), Context(http));

        Assert.Empty(builds);
        Assert.NotNull(error);
        Assert.Equal(ScrapeMessageKeys.GitHubRateLimited, error.Error.MessageKey);
        Assert.Equal(HttpStatusCode.Forbidden, error.Error.Status);
        Assert.Equal("upbge-stable", error.Error.SourceId);
        Assert.Equal([new("source", "upbge-stable")], error.Error.MessageArgs);
        Assert.Contains(DateTimeOffset.FromUnixTimeSeconds(1794405160).ToString("u", System.Globalization.CultureInfo.InvariantCulture),
            error.Error.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.AbsoluteUri == Tags);
    }

    [Fact]
    public async Task Rate_limit_message_alone_is_recognised_on_429()
    {
        var handler = new RecordedResponseHandler().AddText(Releases, """{"message":"You have exceeded a secondary rate limit."}""", "application/json", 429);
        using var http = handler.CreateClient();

        var (_, error) = await Collect(new UpbgeBuildSource(UpbgeKind.Weekly), Context(http));

        Assert.Equal(ScrapeMessageKeys.GitHubRateLimited, error?.Error.MessageKey);
        Assert.Equal("upbge-weekly", error?.Error.SourceId);
        Assert.Equal("GitHub rate limit exceeded", error?.Error.Detail);
    }

    [Fact]
    public async Task Other_http_errors_give_the_status()
    {
        var handler = new RecordedResponseHandler().AddText(Releases, """{"message":"Server Error"}""", "application/json", 502);
        using var http = handler.CreateClient();

        var (_, error) = await Collect(new UpbgeBuildSource(UpbgeKind.Stable), Context(http));

        Assert.Equal(ScrapeMessageKeys.HttpStatus, error?.Error.MessageKey);
        Assert.Equal(HttpStatusCode.BadGateway, error?.Error.Status);
    }

    [Fact]
    public async Task Connection_failure_propagates_for_the_service_to_map()
    {
        var handler = new RecordedResponseHandler().Add(new FixtureEntry { Url = Releases, Fail = "connection" });
        using var http = handler.CreateClient();

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => Collect(new UpbgeBuildSource(UpbgeKind.Stable), Context(http)));
        Assert.Equal(ScrapeMessageKeys.ConnectionFailed, ScrapeError.FromException("upbge-stable", e).MessageKey);
    }

    [Fact]
    public async Task Failed_tag_fetch_leaves_builds_without_hashes()
    {
        var fixtures = Path.Combine(RecordedResponseHandler.FixturesRoot, "upbge");
        var handler = new RecordedResponseHandler()
            .AddText(Releases, File.ReadAllText(Path.Combine(fixtures, "releases.json")), "application/json")
            .AddText(Tags, """{"message":"API rate limit exceeded"}""", "application/json", 403, "GET", new KeyValuePair<string, string>("X-RateLimit-Remaining", "0"));
        using var http = handler.CreateClient();

        var (builds, error) = await Collect(new UpbgeBuildSource(UpbgeKind.Weekly), Context(http));

        Assert.Null(error);
        Assert.Equal(4, builds.Count);
        Assert.All(builds, b => Assert.Null(b.Build.BuildHash));
    }

    [Fact]
    public void Release_filters()
    {
        var snapshot = Snapshot(
            Set(Release("v0.50", "upbge-0.50-windows-x64.7z"), r => r.Draft = true),
            Set(Release("v0.51", "upbge-0.51-windows-x64.zip"), r => r.PublishedAt = null),
            Release("", "upbge-0.52-windows-x64.zip"),
            Release("v0.53", "upbge-0.53-alpha-windows-x64.zip"),
            Release("v0.2.5", "upbge-0.2.5-windows-x64.zip"),
            Release("vnext", "upbge-next-windows-x64.zip"),
            Release("v0.54", "upbge-0.54-windows-x64.zip", "upbge-0.54-linux-x64.tar.xz", "UPBGE-0.54-windows-arm64.zip", "upbge-0.54-windows-x64.zip.sha256"),
            Release("weekly-build-7", "upbge-0.55-windows-x64.zip"),
            Set(Release("weekly-build-8", "upbge-0.55-alpha-windows-x64.zip"), r => r.PublishedAt = "not a date"));

        var stable = UpbgeBuildSource.SelectBuilds(snapshot, UpbgeKind.Stable, HostOs.Windows);
        var weekly = UpbgeBuildSource.SelectBuilds(snapshot, UpbgeKind.Weekly, HostOs.Windows);

        // "vnext" passes the release filter (an unparsable tag is included, as upstream) but gives no version.
        Assert.Equal(["upbge-0.54-windows-x64.zip", "UPBGE-0.54-windows-arm64.zip"], stable.Select(b => b.Build.Link.Split('/')[^1]));
        Assert.All(stable, b => Assert.Equal(42, b.SizeBytes));
        Assert.Empty(weekly);
    }

    [Fact]
    public void Weekly_version_falls_back_to_the_tag_number()
    {
        var snapshot = Snapshot(
            Release("weekly-build-7", "upbge-alpha-windows-x86_64.zip", "upbge-0.55-alpha-linux.tar.xz"),
            Release("weekly-build-x", "upbge-alpha-windows-x86_64.zip"),
            Release("weekly-build-9", "upbge-.-alpha-windows-x86_64.zip"));

        var build = Assert.Single(UpbgeBuildSource.SelectBuilds(snapshot, UpbgeKind.Weekly, HostOs.Windows));

        Assert.Equal("0.0.7-weekly", build.Build.Subversion);
        Assert.Equal("0123456789ab", build.Build.BuildHash);
        Assert.Equal("upbge-weekly", build.Build.Branch);
    }

    [Fact]
    public void Windows7_packages_give_way_to_the_others_but_are_kept_alone()
    {
        var both = Snapshot(Release("v0.36", "upbge-0.36-windows7-x86_64.7z", "upbge-0.36-windows-x86_64.7z"));
        var only = Snapshot(Release("v0.36", "upbge-0.36-windows7-x86_64.7z"));

        Assert.EndsWith("upbge-0.36-windows-x86_64.7z", Assert.Single(UpbgeBuildSource.SelectBuilds(both, UpbgeKind.Stable, HostOs.Windows)).Build.Link, StringComparison.Ordinal);
        Assert.EndsWith("windows7-x86_64.7z", Assert.Single(UpbgeBuildSource.SelectBuilds(only, UpbgeKind.Stable, HostOs.Windows)).Build.Link, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_platforms()
    {
        var snapshot = Snapshot(Release("v0.50", "upbge-0.50-linux-x64.tar.xz", "upbge-0.50-macos-arm64.dmg", "upbge-0.50-windows-x64.7z"));

        var linux = Assert.Single(UpbgeBuildSource.SelectBuilds(snapshot, UpbgeKind.Stable, HostOs.Linux));
        var mac = Assert.Single(UpbgeBuildSource.SelectBuilds(snapshot, UpbgeKind.Stable, HostOs.MacOS));

        Assert.EndsWith(".tar.xz", linux.Build.Link, StringComparison.Ordinal);
        Assert.Equal("blender", linux.Build.CustomExecutable);
        Assert.EndsWith(".dmg", mac.Build.Link, StringComparison.Ordinal);
        Assert.Equal("Blender.app/Contents/MacOS/Blender", mac.Build.CustomExecutable);
    }

    [Theory]
    [InlineData(HostOs.Windows, "upbge-0.50-windows-x64.7z", true)]
    [InlineData(HostOs.Windows, "UPBGE-0.30-Windows-x86_64.ZIP", true)]
    [InlineData(HostOs.Windows, "upbge-0.50-windows-x64.rar", false)]
    [InlineData(HostOs.Windows, "x-upbge-0.50-windows-x64.zip", false)]
    [InlineData(HostOs.Windows, "UPBGEv0.2.5b-b2.79Windows64.7z", false)]
    [InlineData(HostOs.MacOS, "upbge-0.36.1-macOS-x86_64.dmg", true)]
    [InlineData(HostOs.MacOS, "upbge-0.36.1-macOS-x86_64.zip", true)]
    [InlineData(HostOs.Linux, "upbge-0.50-linux-x64.tar.xz", true)]
    [InlineData(HostOs.Linux, "upbge-0.53-alpha-linux-x86_64-2026-11-08.tar.gz", true)]
    [InlineData(HostOs.Linux, "upbge-0.50-linux-x64.zip", false)]
    public void Asset_filters(HostOs os, string name, bool expected)
    {
        var m = UpbgeBuildSource.AssetFilter(os).Match(name);
        Assert.Equal(expected, m.Success && m.Index == 0);
    }

    [Theory]
    [InlineData("v0.50", "", false, "0.50.0")]
    [InlineData("vv0.36.1", "", false, "0.36.1")]
    [InlineData("v0.2.5b", "", false, "0.2.0-.5b")] // parse_blender_ver's reading; below 0.30 either way
    [InlineData("weekly-build-108", "upbge-0.53-alpha-windows-x86_64-2026-11-08.zip", true, "0.53.0")]
    [InlineData("weekly-build-108", "upbge-windows.zip", true, "0.0.108-weekly")]
    [InlineData("weekly-build-", "upbge-windows.zip", true, null)]
    public void Versions(string tag, string asset, bool weekly, string? expected) =>
        Assert.Equal(expected, UpbgeBuildSource.ParseVersion(tag, asset, weekly)?.ToString());

    /// <summary>
    /// Live smoke test (never in a normal run): <c>dotnet test … -- --explicit only --filter-method "*Live_smoke"</c>.
    /// Uses <c>BL_GITHUB_TOKEN</c> when set.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Live_smoke()
    {
        var options = Core.Net.ConnectionOptions.Default with { GitHubToken = Environment.GetEnvironmentVariable("BL_GITHUB_TOKEN") };
        using var http = Core.Net.HttpClientProvider.CreateClient(options, Core.Net.HttpClientProvider.CreatePrimaryHandler(options));
        var (stable, weekly) = UpbgeBuildSource.CreatePair();

        var s = await Collect(stable, Context(http));
        var w = await Collect(weekly, Context(http));

        Assert.Null(s.Error);
        Assert.Null(w.Error);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"upbge: {s.Builds.Count} stable (newest {s.Builds.FirstOrDefault()?.Build.Subversion}), {w.Builds.Count} weekly (newest {w.Builds.FirstOrDefault()?.Build.Link})");
        Assert.NotEmpty(s.Builds);
        Assert.NotEmpty(w.Builds);
    }
}
