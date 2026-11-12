using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Scraping.Bforartists;
using BlenderLauncher.Core.Scraping.PullRequests;
using BlenderLauncher.Core.Tests.Net;
using BlenderLauncher.Core.Updates;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Tests.Scraping;

/// <summary>
/// The whole pipeline: the real sources, <see cref="ApiDataService"/> and <see cref="AppReleaseService"/> inside a
/// <see cref="ScrapeService"/>, over every recorded fixture folder through one <see cref="RecordedResponseHandler"/>.
/// </summary>
public sealed class ScrapePipelineTests : IDisposable
{
    // The fake clock at the first check: a day before the fixtures were recorded (11 Nov 2026), so the second
    // check has builds committed since the first.
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

    private readonly DataDirectories _dirs = new(Path.Combine(Path.GetTempPath(), "bl-scrape-pipeline-" + Guid.NewGuid().ToString("N")));

    public ScrapePipelineTests() => Directory.CreateDirectory(_dirs.Cache);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dirs.Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Every_source_runs_against_the_recorded_sites()
    {
        var handler = RecordedResponseHandler.FromFixtures("raw-api", "stable", "builder", "bforartists", "upbge", "github-releases");
        using var http = handler.CreateClient();
        var time = new ManualTimeProvider(Now);
        var titles = new PullRequestTitleService(http, _dirs.Cache);
        var store = new InMemoryScrapeStateStore();
        var settings = ScrapeSettings.Default with
        {
            // The stable fixtures hold the 4.5, 5.2 and 5.3 folders; older series come from the hosted seed.
            MinimumStableVersion = new SemVer(4, 2, 0),
            ScrapeUpbgeWeekly = true,
        };
        await using var service = new ScrapeService(new ScrapeServiceOptions
        {
            Http = () => http,
            CacheDirectory = _dirs.Cache,
            Sources = ScrapeService.CreateSources(titles),
            Settings = () => settings,
            State = store,
            ApiData = new ApiDataService(_dirs, () => http, time: time),
            Releases = new AppReleaseService("Victor-IX/Blender-Launcher-V2", () => http, time: time),
            PullRequestTitles = titles,
            CurrentVersion = "2.7.0",
            Time = time,
            Os = HostOs.Windows,
            Machine = "amd64",
        });
        var published = new List<string>();
        service.SourceCompleted += (_, r) =>
        {
            lock (published)
            {
                published.Add(r.SourceId);
            }
        };

        var snapshot = await service.CheckAsync(ct: Ct);

        handler.AssertAllMatched();
        Assert.Empty(snapshot.Errors);
        Assert.Equal(
            [BuildSourceIds.Stable, BuildSourceIds.Daily, BuildSourceIds.Experimental, BuildSourceIds.Patch,
             BuildSourceIds.Bforartists, BuildSourceIds.UpbgeStable, BuildSourceIds.UpbgeWeekly],
            snapshot.Sources.Select(s => s.SourceId));
        Assert.Equal(snapshot.Sources.Select(s => s.SourceId).Order(), published.Order());
        Assert.All(snapshot.Sources, s => Assert.True(s.Succeeded, s.SourceId));

        // Every channel has builds, every build is listed once, nothing is new on the first check.
        foreach (var channel in new[] { Channel.Stable, Channel.Lts, Channel.Daily, Channel.Experimental, Channel.Patch, Channel.Bforartists, Channel.UpbgeStable, Channel.UpbgeWeekly })
        {
            Assert.True(snapshot.ForChannel(channel).Any(), $"no {channel} builds");
        }

        Assert.Equal(snapshot.Builds.Count, snapshot.Builds.Select(b => ScrapeService.DeduplicationKey(b.Build)).Distinct().Count());
        Assert.Equal(0, snapshot.NewCount);
        Assert.All(snapshot.Builds, b => Assert.True(BuildPlatform.PassesPostFilter(HostOs.Windows, "amd64", b.Build.Link), b.Build.Link));

        // The API refresh ran first (stored file, seeded cache) and the LTS table reached the stable source.
        Assert.NotNull(snapshot.ApiRefresh);
        Assert.True(snapshot.ApiRefresh.ApiDataFetched);
        Assert.True(File.Exists(Path.Combine(_dirs.Api, BundledApiFiles.ApiFileName)));
        Assert.Contains(snapshot.ForChannel(Channel.Lts), b => b.Build.Subversion.StartsWith("4.2.", StringComparison.Ordinal));
        Assert.Contains(snapshot.ForChannel(Channel.Lts), b => b.Build.Subversion.StartsWith("4.5.", StringComparison.Ordinal));

        // The launcher release check ran last.
        Assert.NotNull(snapshot.LauncherUpdate);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, snapshot.LauncherUpdate.Status);
        Assert.Equal("v2.8.2", snapshot.LauncherUpdate.Latest!.Tag);
        Assert.Equal(Now, store.LastChecked);

        // Sizes: stable from the listing, Bforartists stable from HEAD, both kept in sidecars.
        Assert.Contains(snapshot.ForChannel(Channel.Stable), b => b.SizeBytes is not null);
        Assert.Contains(snapshot.ForChannel(Channel.Bforartists), b => b.SizeBytes is not null);
        Assert.True(File.Exists(Path.Combine(_dirs.Cache, BforartistsBuildSource.SizesFileName)));

        // A second check a day later compares with the first: builds committed since then are new, the rest are
        // not, and the caches answer without HEAD requests.
        var sent = handler.Requests.Count;
        time.Advance(TimeSpan.FromDays(1));
        var second = await service.CheckAsync(ct: Ct);
        handler.AssertAllMatched();
        Assert.Empty(second.Errors);
        Assert.Equal(Now, second.ComparedWith);
        Assert.Equal(snapshot.Builds.Select(b => b.Build), second.Builds.Select(b => b.Build));
        Assert.InRange(second.NewCount, 1, second.Builds.Count - 1);
        Assert.All(second.Builds, b => Assert.Equal(b.Build.CommitTime.ToDateTimeOffset(TimeZoneInfo.Utc) > Now, b.IsNew));
        Assert.DoesNotContain(handler.Requests.Skip(sent), r => r.Method == HttpMethod.Head);
        Assert.Contains(second.ForChannel(Channel.Bforartists), b => b.SizeBytes is not null);
        Assert.Equal(Now.AddDays(1), store.LastChecked);
    }
}
