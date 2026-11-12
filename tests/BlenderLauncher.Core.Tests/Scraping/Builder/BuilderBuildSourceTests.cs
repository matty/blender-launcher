using System.Net;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Scraping.Builder;
using BlenderLauncher.Core.Tests.Net;

namespace BlenderLauncher.Core.Tests.Scraping.Builder;

/// <summary><see cref="BuilderBuildSource"/> over the recorded listings in <c>Fixtures/Net/builder</c>.</summary>
public sealed class BuilderBuildSourceTests : IDisposable
{
    private const string DailyUrl = "https://builder.blender.org/download/daily/?format=json&v=1";

    private readonly string _cache = Directory.CreateTempSubdirectory("bl-builder-tests-").FullName;

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ScrapeContext Context(HttpClient http, HostOs os = HostOs.Windows, string machine = "amd64") =>
        new() { Http = http, CacheDirectory = _cache, Os = os, Machine = machine };

    private static async Task<List<AvailableBuild>> Scrape(IBuildSource source, ScrapeContext ctx)
    {
        var list = new List<AvailableBuild>();
        await foreach (var b in source.ScrapeAsync(ctx, Ct))
        {
            list.Add(b);
        }

        return list;
    }

    [Fact]
    public void Ids_channels_and_urls()
    {
        using var http = new HttpClient();
        var ctx = Context(http);
        Assert.Equal((BuildSourceIds.Daily, Channel.Daily), (BuilderBuildSource.Daily().Id, BuilderBuildSource.Daily().Channels.Single()));
        Assert.Equal((BuildSourceIds.Experimental, Channel.Experimental), (BuilderBuildSource.Experimental().Id, BuilderBuildSource.Experimental().Channels.Single()));
        Assert.Equal((BuildSourceIds.Patch, Channel.Patch), (BuilderBuildSource.Patch().Id, BuilderBuildSource.Patch().Channels.Single()));
        Assert.Equal(DailyUrl, BuilderBuildSource.Daily().UrlFor(ctx));
        Assert.Equal("https://builder.blender.org/download/patch/archive/?format=json&v=1", BuilderBuildSource.Patch().UrlFor(ctx with { PatchArchive = true }));

        // Each toggle affects its own source only.
        Assert.Equal(DailyUrl, BuilderBuildSource.Daily().UrlFor(ctx with { ExperimentalArchive = true, PatchArchive = true }));
    }

    [Fact]
    public async Task Daily_on_windows_x64()
    {
        var handler = RecordedResponseHandler.FromFixtures("builder");
        using var http = handler.CreateClient();
        var builds = await Scrape(BuilderBuildSource.Daily(), Context(http));
        handler.AssertAllMatched();

        // The three amd64 zips; sha256, msi, msix and arm64 entries are filtered out.
        Assert.Equal(
            ["4.2.23-stable", "4.3.2-stable", "5.4.0-alpha"],
            builds.Select(b => b.Build.Subversion));
        Assert.All(builds, b =>
        {
            Assert.Equal("daily", b.Build.Branch);
            Assert.Contains("windows.amd64-release.zip", b.Build.Link, StringComparison.Ordinal);
            Assert.False(b.IsNew);
            Assert.Null(b.Description);
            Assert.True(BuildPlatform.PassesPostFilter(HostOs.Windows, "amd64", b.Build.Link));
        });

        var first = builds[0];
        Assert.Equal("https://cdn.builder.blender.org/download/daily/blender-4.2.23-stable+v42.d0cbe84903e8-windows.amd64-release.zip", first.Build.Link);
        Assert.Equal("d0cbe84903e8", first.Build.BuildHash);
        Assert.Equal(383_622_379, first.SizeBytes);
        Assert.Equal(IsoTimestamp.Aware(DateTimeOffset.FromUnixTimeSeconds(1786499873)), first.Build.CommitTime);
        Assert.Equal(TimeSpan.Zero, first.Build.CommitTime.Offset);
        Assert.Equal("4.2.23-stable+daily.d0cbe84903e8", first.Build.FullSemVersion.ToString());
        Assert.Equal("Alpha", builds[2].Build.DisplayLabel);
    }

    [Fact]
    public async Task Daily_on_windows_arm64()
    {
        var handler = RecordedResponseHandler.FromFixtures("builder");
        using var http = handler.CreateClient();
        var builds = await Scrape(BuilderBuildSource.Daily(), Context(http, machine: "arm64"));

        Assert.Equal(["4.3.2-stable", "4.4.3-stable"], builds.Select(b => b.Build.Subversion));
        Assert.All(builds, b => Assert.Contains("windows.arm64-release.zip", b.Build.Link, StringComparison.Ordinal));
        Assert.Equal(377_487_524, builds[0].SizeBytes);
    }

    [Fact]
    public async Task Any_architecture_fallback_labels_each_build()
    {
        var handler = RecordedResponseHandler.FromFixtures("builder");
        using var http = handler.CreateClient();

        // No x86 builds exist, so every Windows zip is taken and labelled; amd64 is shown as x86_64.
        var builds = await Scrape(BuilderBuildSource.Daily(), Context(http, machine: "x86"));
        Assert.Equal(
            ["4.2.23-stable | x86_64", "4.3.2-stable | x86_64", "5.4.0-alpha | x86_64", "4.3.2-stable | arm64", "4.4.3-stable | arm64"],
            builds.Select(b => b.Build.Subversion));
        Assert.All(builds, b => Assert.NotNull(b.Build.SemVersion));
        Assert.Equal("stable | arm64", builds[3].Build.SemVersion!.Value.Prerelease);
    }

    [Fact]
    public async Task Linux_arm64_falls_back_to_x86_64_and_macos_matches_its_architecture()
    {
        var handler = RecordedResponseHandler.FromFixtures("builder");
        using var http = handler.CreateClient();

        var linux = await Scrape(BuilderBuildSource.Daily(), Context(http, HostOs.Linux, "aarch64"));
        Assert.Equal(["4.2.23-stable | x86_64", "4.3.2-stable | x86_64"], linux.Select(b => b.Build.Subversion));
        Assert.All(linux, b => Assert.EndsWith(".tar.xz", b.Build.Link, StringComparison.Ordinal));

        var linuxX64 = await Scrape(BuilderBuildSource.Daily(), Context(http, HostOs.Linux, "x86_64"));
        Assert.Equal(["4.2.23-stable", "4.3.2-stable"], linuxX64.Select(b => b.Build.Subversion));

        var mac = await Scrape(BuilderBuildSource.Daily(), Context(http, HostOs.MacOS, "arm64"));
        Assert.Equal(["4.2.23-stable", "4.3.2-stable"], mac.Select(b => b.Build.Subversion));
        Assert.All(mac, b => Assert.Contains("darwin.arm64", b.Build.Link, StringComparison.Ordinal));

        var intelMac = await Scrape(BuilderBuildSource.Daily(), Context(http, HostOs.MacOS, "x86_64"));
        Assert.Equal(["4.2.23-stable"], intelMac.Select(b => b.Build.Subversion));
    }

    [Fact]
    public async Task Experimental_uses_the_branch_as_prerelease()
    {
        // experimental.json is hand-written (the live listing was empty when recorded); see the folder's README.
        var handler = RecordedResponseHandler.FromFixtures("builder");
        using var http = handler.CreateClient();
        var builds = await Scrape(BuilderBuildSource.Experimental(), Context(http));

        Assert.Equal(["5.1.0-npr-prototype", "5.0.0-vulkan-dev"], builds.Select(b => b.Build.Subversion));
        Assert.All(builds, b => Assert.Equal("experimental", b.Build.Branch));
        Assert.Equal("1a2b3c4d5e6f", builds[0].Build.BuildHash);
        Assert.Equal(412_345_678, builds[0].SizeBytes);
        Assert.Equal("Npr Prototype", builds[0].Build.DisplayLabel);
    }

    [Fact]
    public async Task Patch_uses_the_pr_as_prerelease_without_titles_by_default()
    {
        var handler = RecordedResponseHandler.FromFixtures("builder");
        using var http = handler.CreateClient();
        var builds = await Scrape(BuilderBuildSource.Patch(), Context(http));
        handler.AssertAllMatched();

        Assert.Equal(
            ["5.3.0-PR163602", "5.4.0-PR155204", "5.4.0-PR164649", "4.5.12-PR162828", "4.5.16-PR166147"],
            builds.Select(b => b.Build.Subversion));
        Assert.All(builds, b =>
        {
            Assert.Equal("patch", b.Build.Branch);
            Assert.Null(b.Description);
        });
        Assert.DoesNotContain(handler.Requests, r => r.Uri.Host == "projects.blender.org");
        Assert.Equal(409_944_161, builds[0].SizeBytes);
        Assert.Equal("1cd6374bf750", builds[0].Build.BuildHash);
    }

    [Theory]
    [InlineData("daily", 2, "4.5.12-stable")]
    [InlineData("experimental", 0, null)]
    [InlineData("patch", 2, "5.3.0-PR144181")]
    public async Task Archive_toggle_scrapes_the_archive_instead(string branch, int count, string? firstSubversion)
    {
        var handler = RecordedResponseHandler.FromFixtures("builder");
        using var http = handler.CreateClient();
        var ctx = Context(http) with { DailyArchive = true, ExperimentalArchive = true, PatchArchive = true };
        var source = branch switch
        {
            "daily" => BuilderBuildSource.Daily(),
            "experimental" => BuilderBuildSource.Experimental(),
            _ => BuilderBuildSource.Patch(),
        };

        var builds = await Scrape(source, ctx);

        Assert.Equal(count, builds.Count);
        Assert.Equal(firstSubversion, builds.FirstOrDefault()?.Build.Subversion);
        Assert.All(builds, b => Assert.Equal(branch, b.Build.Branch)); // "/archive" is not part of the branch
        Assert.All(handler.Requests, r => Assert.Contains($"/download/{branch}/archive/", r.Uri.AbsoluteUri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Http_error_throws_a_source_exception_with_the_status()
    {
        var handler = new RecordedResponseHandler().AddText(DailyUrl, "Service Unavailable", "text/plain", 503);
        using var http = handler.CreateClient();
        var e = await Assert.ThrowsAsync<ScrapeSourceException>(() => Scrape(BuilderBuildSource.Daily(), Context(http)));
        Assert.Equal(ScrapeMessageKeys.HttpStatus, e.Error.MessageKey);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, e.Error.Status);
        Assert.Equal(BuildSourceIds.Daily, e.Error.SourceId);
    }

    [Fact]
    public async Task Connection_failure_throws_a_connection_error()
    {
        var handler = new RecordedResponseHandler().Add(new FixtureEntry { Url = DailyUrl, Fail = "connection" });
        using var http = handler.CreateClient();
        var e = await Assert.ThrowsAsync<ScrapeSourceException>(() => Scrape(BuilderBuildSource.Daily(), Context(http)));
        Assert.Equal(ScrapeMessageKeys.ConnectionFailed, e.Error.MessageKey);
        Assert.IsType<HttpRequestException>(e.InnerException);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"builds\": []}")]
    public async Task Malformed_json_throws_a_generic_error(string body)
    {
        var handler = new RecordedResponseHandler().AddText(DailyUrl, body, "application/json");
        using var http = handler.CreateClient();
        var e = await Assert.ThrowsAsync<ScrapeSourceException>(() => Scrape(BuilderBuildSource.Daily(), Context(http)));
        Assert.Equal(ScrapeMessageKeys.Generic, e.Error.MessageKey);
    }

    [Fact]
    public async Task Bad_entries_are_skipped_and_missing_fields_tolerated()
    {
        const string json = """
            [
              { "url": "https://cdn.example/blender-4.3.0-alpha+main.aaa-windows.amd64-release.zip", "version": "not a version",
                "platform": "windows", "architecture": "amd64", "file_name": "blender-4.3.0-alpha+main.aaa-windows.amd64-release.zip", "file_mtime": 1 },
              { "url": "https://cdn.example/blender-4.3.0-alpha+main.bbb-windows.amd64-release.zip", "version": "4.3.0",
                "platform": "windows", "architecture": "amd64", "file_name": "blender-4.3.0-alpha+main.bbb-windows.amd64-release.zip" },
              { "version": "4.3.0", "platform": "windows", "architecture": "amd64", "file_mtime": 1,
                "file_name": "blender-4.3.0-alpha+main.ccc-windows.amd64-release.zip" },
              { "url": "https://cdn.example/blender-4.3.0-alpha+main.ddd-windows.amd64-release.zip", "version": "4.3.0", "release_cycle": "alpha",
                "platform": "windows", "architecture": "AMD64", "file_name": "blender-4.3.0-alpha+main.ddd-windows.amd64-release.zip",
                "file_mtime": 1700000000.5, "hash": "ddd", "extra": { "ignored": true } },
              { "platform": "windows" }
            ]
            """;
        var handler = new RecordedResponseHandler().AddText(DailyUrl, json, "application/json");
        using var http = handler.CreateClient();
        var builds = await Scrape(BuilderBuildSource.Daily(), Context(http));

        var only = Assert.Single(builds);
        Assert.Equal("4.3.0-alpha", only.Build.Subversion);
        Assert.Null(only.SizeBytes);
        Assert.Equal(IsoTimestamp.Aware(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_500)), only.Build.CommitTime);
    }

    [Theory]
    // Upstream's precedence: patch (not daily), then release_cycle (daily), then branch (experimental).
    [InlineData("daily", "PR1", "alpha", "main", true, "alpha")]
    [InlineData("daily", null, null, "main", true, null)]
    [InlineData("patch", "PR1", "alpha", "main-PR1", true, "PR1")]
    [InlineData("patch", null, "alpha", "main-PR1", true, null)]
    [InlineData("experimental", "", "alpha", "npr", true, "npr")]
    [InlineData("experimental", "x", "alpha", "", true, "x")]
    [InlineData("daily", null, "beta", "main", false, "beta | x86_64")]
    public void Prerelease_rules(string branchType, string? patch, string? releaseCycle, string? branch, bool architectureSpecific, string? expected)
    {
        var entry = new BuilderEntry
        {
            Url = "https://example/b.zip",
            Version = "4.2.0",
            Patch = patch,
            ReleaseCycle = releaseCycle,
            Branch = branch,
            Architecture = "amd64",
            FileMtime = 0,
        };
        var build = BuilderBuildSource.ToBuild(entry, branchType, architectureSpecific, out var prerelease);
        Assert.NotNull(build);
        Assert.Equal(expected, prerelease);
        Assert.Equal(expected is null ? "4.2.0" : "4.2.0-" + expected, build.Subversion);
        Assert.Equal(branchType, build.Branch);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var handler = RecordedResponseHandler.FromFixtures("builder");
        using var http = handler.CreateClient();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in BuilderBuildSource.Daily().ScrapeAsync(Context(http), cts.Token))
            {
            }
        });
    }

    /// <summary>
    /// Live smoke test: prints the count per source and archive. Explicit, and also needs <c>BL_LIVE_SMOKE=1</c>.
    /// <c>dotnet test --project tests/BlenderLauncher.Core.Tests -- --explicit only --filter-method "*Live_builder_counts"</c>.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Live_builder_counts()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("BL_LIVE_SMOKE") == "1", "Set BL_LIVE_SMOKE=1 to run.");
        using var provider = new HttpClientProvider(ConnectionOptions.Default);
        var ctx = new ScrapeContext { Http = provider.Client, CacheDirectory = _cache, FetchPrTitles = true };
        foreach (var archive in new[] { false, true })
        {
            var c = ctx with { DailyArchive = archive, ExperimentalArchive = archive, PatchArchive = archive };
            foreach (var source in new[] { BuilderBuildSource.Daily(), BuilderBuildSource.Experimental(), BuilderBuildSource.Patch() })
            {
                var builds = await Scrape(source, c);
                var titled = builds.Count(b => b.Description is not null);
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"{source.UrlFor(c)}: {builds.Count} builds, {titled} with PR titles, {builds.Sum(b => b.SizeBytes ?? 0) / 1_000_000} MB");
            }
        }
    }
}
