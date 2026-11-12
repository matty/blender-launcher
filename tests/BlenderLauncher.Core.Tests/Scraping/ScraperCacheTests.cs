using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Tests.Scraping;

/// <summary>
/// Ported from upstream's <c>tests/backend/test_scraper_cache.py</c>, plus upstream's bundled
/// <c>stable_builds_api_windows.json</c>.
/// </summary>
public class ScraperCacheTests
{
    // STABLE_FOLDER_CACHE and SCRAPER_CACHE_DICT from the upstream test, as JSON. The asset entry has
    // file_version 1.3 and no is_frozen key.
    private const string StableFolderCache =
        """
        {
          "assets": [
            [
              "https://download.blender.org/release/Blender4.3/blender-4.3.2-linux-x64.tar.xz",
              {
                "file_version": "1.3",
                "blinfo": [
                  {
                    "branch": "stable",
                    "subversion": "4.3.2",
                    "build_hash": null,
                    "commit_time": "2024-12-17T03:40:55-05:00",
                    "custom_name": "",
                    "is_favorite": false,
                    "custom_executable": null
                  }
                ]
              }
            ]
          ],
          "modified_date": "2024-12-17T08:41:00"
        }
        """;

    private const string ScraperCacheDict =
        $$"""
        {
          "folders": {
            "4.2.0": {"assets": [], "modified_date": "2024-12-17T09:02:00"},
            "4.3.0": {{StableFolderCache}}
          }
        }
        """;

    private const string Link = "https://download.blender.org/release/Blender4.3/blender-4.3.2-linux-x64.tar.xz";

    private static readonly IsoTimestamp ExpectedCommitTime =
        IsoTimestamp.Aware(new DateTime(2024, 12, 17, 3, 40, 55), TimeSpan.FromHours(-5));

    private static void AssertUpstreamAsset(BuildInfo build)
    {
        Assert.Equal(Link, build.Link);
        Assert.Equal("4.3.2", build.Subversion);
        Assert.Null(build.BuildHash);
        Assert.Equal(ExpectedCommitTime, build.CommitTime);
        Assert.Equal(TimeSpan.FromHours(-5), build.CommitTime.Offset);
        Assert.Equal("stable", build.Branch);
        Assert.Equal("", build.CustomName);
        Assert.False(build.IsFavorite);
        Assert.Null(build.CustomExecutable);
        Assert.False(build.IsFrozen);
    }

    [Fact]
    public void Upstream_stable_folder_deserialize()
    {
        var cache = ScraperCache.FromJson("""{"folders": {"4.3.0": """ + StableFolderCache + "}}");
        var folder = cache[new SemVer(4, 3, 0)];

        AssertUpstreamAsset(Assert.Single(folder.Assets));
        Assert.Equal(IsoTimestamp.Naive(new DateTime(2024, 12, 17, 8, 41, 0)), folder.ModifiedDate);
        Assert.True(folder.ModifiedDate.IsNaive);
    }

    [Fact]
    public void Upstream_scraper_cache_deserialize()
    {
        var cache = ScraperCache.FromJson(ScraperCacheDict);

        Assert.Null(cache.ApiFileVersion);
        Assert.Equal([new SemVer(4, 2, 0), new SemVer(4, 3, 0)], cache.Folders.Keys);
        Assert.Empty(cache[new SemVer(4, 2, 0)].Assets);
        Assert.Equal(IsoTimestamp.Naive(new DateTime(2024, 12, 17, 9, 2, 0)), cache[new SemVer(4, 2, 0)].ModifiedDate);
        AssertUpstreamAsset(Assert.Single(cache[new SemVer(4, 3, 0)].Assets));
        Assert.Equal(IsoTimestamp.Naive(new DateTime(2024, 12, 17, 8, 41, 0)), cache[new SemVer(4, 3, 0)].ModifiedDate);
    }

    [Fact]
    public void Round_trip_keeps_naive_folder_dates_and_commit_time_offsets()
    {
        var json = ScraperCache.FromJson(ScraperCacheDict).ToJson();

        Assert.Contains("\"modified_date\": \"2024-12-17T08:41:00\"", json, StringComparison.Ordinal);
        Assert.Contains("\"commit_time\": \"2024-12-17T03:40:55-05:00\"", json, StringComparison.Ordinal);
        Assert.Contains("\"file_version\": \"1.5\"", json, StringComparison.Ordinal); // rewritten as the port writes
        Assert.DoesNotContain("api_file_version", json, StringComparison.Ordinal);
        Assert.StartsWith("{\n \"folders\": {\n  \"4.2.0\": {", json.ReplaceLineEndings("\n"), StringComparison.Ordinal);

        var again = ScraperCache.FromJson(json);
        Assert.True(again[new SemVer(4, 3, 0)].ModifiedDate.IsNaive);
        AssertUpstreamAsset(Assert.Single(again[new SemVer(4, 3, 0)].Assets));
        Assert.Equal(json, again.ToJson());
    }

    [Fact]
    public void New_build_defaults_to_the_epoch_and_api_file_version_is_written_first()
    {
        var cache = new ScraperCache { ApiFileVersion = "29.1" };
        var folder = cache.NewBuild(new SemVer(5, 3, 0));
        cache.NewBuild(new SemVer(5, 4, 0), IsoTimestamp.Naive(new DateTime(2026, 11, 1, 10, 0, 0)));

        Assert.Same(folder, cache[new SemVer(5, 3, 0)]);
        Assert.True(cache.Contains(new SemVer(5, 3, 0)));
        Assert.Equal(IsoTimestamp.Epoch, folder.ModifiedDate);
        Assert.Equal(
            """{"api_file_version":"29.1","folders":{"5.3.0":{"assets":[],"modified_date":"1970-01-01T00:00:00+00:00"},"5.4.0":{"assets":[],"modified_date":"2026-11-01T10:00:00"}}}"""
                .Replace("+", @"\u002B", StringComparison.Ordinal), // the default encoder escapes "+"
            cache.ToJson(indent: 0));
    }

    [Fact]
    public void Bundled_windows_stable_cache_has_the_spec_counts_and_round_trips()
    {
        var json = Fixtures.ReadText("upstream-api-stable_builds_api_windows.json");
        var cache = ScraperCache.FromJson(json);
        var assets = cache.Folders.Values.SelectMany(f => f.Assets).ToList();

        Assert.Equal("29.0", cache.ApiFileVersion);
        Assert.Equal(54, cache.Folders.Count);
        Assert.Equal(new SemVer(2, 48, 0), cache.Folders.Keys.First());
        Assert.Equal(new SemVer(5, 2, 0), cache.Folders.Keys.Last());
        Assert.Equal(255, assets.Count);
        Assert.Equal(147, assets.Count(b => b.Branch == "lts"));
        Assert.Equal(108, assets.Count(b => b.Branch == "stable"));
        // Upstream's cache has every hash null, except that two 2.70 "vc2013_preview" zips carry a mangled one.
        Assert.Equal(253, assets.Count(b => b.BuildHash is null));
        Assert.Equal(2, assets.Count(b => b.BuildHash == "vc2013_previ"));
        Assert.All(assets, b => Assert.True(b.IsValid, b.Subversion));
        Assert.All(assets, b => Assert.False(b.CommitTime.IsNaive));
        Assert.All(cache.Folders.Values, f => Assert.True(f.ModifiedDate.IsNaive));

        var again = ScraperCache.FromJson(cache.ToJson());
        Assert.Equal(cache.Folders.Keys, again.Folders.Keys);
        Assert.Equal(
            cache.Folders.Values.SelectMany(f => f.Assets),
            again.Folders.Values.SelectMany(f => f.Assets));
        Assert.Equal(cache.ToJson(), again.ToJson());
    }

    [Fact]
    public void Lts_promotion_on_load_uses_the_given_table()
    {
        var api = ApiData.Parse(Fixtures.ReadText("upstream-api-blender_launcher_api.json"));
        var json = ScraperCacheDict.Replace("4.3.2", "4.2.9", StringComparison.Ordinal);

        Assert.Equal("stable", ScraperCache.FromJson(ScraperCacheDict, api)[new SemVer(4, 3, 0)].Assets[0].Branch);
        Assert.Equal("lts", ScraperCache.FromJson(json, api)[new SemVer(4, 3, 0)].Assets[0].Branch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"folders": {"4.3": {"assets": [], "modified_date": "2024-12-17T09:02:00"}}}""")] // key not strict semver
    [InlineData("""{"folders": {"4.3.0": {"assets": [], "modified_date": "yesterday"}}}""")]
    [InlineData("""{"folders": {"4.3.0": {"assets": [["link"]], "modified_date": "2024-12-17T09:02:00"}}}""")]
    public async Task Load_failures_give_an_empty_cache(string? content)
    {
        var path = Path.Combine(Path.GetTempPath(), "bl-cache-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            if (content is not null)
            {
                await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
            }

            var cache = await ScraperCache.LoadOrEmptyAsync(path, ct: TestContext.Current.CancellationToken);
            Assert.Empty(cache.Folders);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), "bl-cache-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var cache = ScraperCache.FromJson(ScraperCacheDict);
            cache.ApiFileVersion = "29.1";
            await cache.SaveAsync(path, TestContext.Current.CancellationToken);

            var loaded = await ScraperCache.LoadOrEmptyAsync(path, ct: TestContext.Current.CancellationToken);
            Assert.Equal("29.1", loaded.ApiFileVersion);
            Assert.Equal(cache.ToJson(), loaded.ToJson());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
