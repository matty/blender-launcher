using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Scraping.Stable;

namespace BlenderLauncher.Core.Tests.Scraping.Stable;

/// <summary>
/// Scrapes the live download.blender.org with an empty cache, twice. Explicit: it never runs in a normal test run.
/// <code>dotnet test --project tests/BlenderLauncher.Core.Tests -- --explicit only --filter-method "*Live_stable_scrape"</code>
/// </summary>
public class StableLiveSmokeTests
{
    [Fact(Explicit = true)]
    public async Task Live_stable_scrape()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Path.Combine(Path.GetTempPath(), "bl-stable-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = ConnectionOptions.Default;
            using var http = HttpClientProvider.CreateClient(options, HttpClientProvider.CreatePrimaryHandler(options));
            var lts = ApiData.Parse(Fixtures.ReadText("upstream-api-blender_launcher_api.json"));
            var ctx = new ScrapeContext { Http = http, CacheDirectory = dir, Lts = lts };
            var source = new StableBuildSource();

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var fresh = await source.ScrapeAsync(ctx, ct).ToListAsync(ct);
            var freshTime = watch.Elapsed;
            watch.Restart();
            var cached = await source.ScrapeAsync(ctx, ct).ToListAsync(ct);

            var output = TestContext.Current.TestOutputHelper;
            output?.WriteLine($"fresh: {fresh.Count} builds ({fresh.Count(b => b.Build.Branch == "lts")} lts) in {freshTime.TotalSeconds:F1} s; " +
                              $"cached: {cached.Count} in {watch.Elapsed.TotalSeconds:F1} s; with size: {fresh.Count(b => b.SizeBytes is not null)}");
            Assert.NotEmpty(fresh);
            Assert.Equal(fresh.Count, cached.Count);
            Assert.Equal(fresh.Count, fresh.Select(b => b.Build.Link).Distinct().Count());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
