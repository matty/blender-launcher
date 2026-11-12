using System.Text.Json;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Scraping.Builder;
using BlenderLauncher.Core.Scraping.PullRequests;
using BlenderLauncher.Core.Tests.Net;

namespace BlenderLauncher.Core.Tests.Scraping.PullRequests;

/// <summary><see cref="PullRequestTitleService"/> and <see cref="PullRequestLabels"/>, over <c>Fixtures/Net/gitea</c>.</summary>
public sealed class PullRequestTitleServiceTests : IDisposable
{
    // Where each PR used below is: page 1, page 2, page 3, on no page (single lookups), and a 404.
    private const int OnPage1 = 163602;
    private const int OnPage2 = 155204;
    private const int OnPage3 = 164649;
    private const int Single1 = 162828;
    private const int Single2 = 166147;
    private const int Missing = 9999999;

    private const string Page1Title = "Anim: Support per-bone 'In Front' display";
    private const string Single1Title = "[4.5 LTS] Fix #126358: SSL authentication failure on MS-Windows";
    private const string Single2Title = "Backport several fixes in Curves & Grease Pencil code.";

    private readonly string _cache = Directory.CreateTempSubdirectory("bl-pr-tests-").FullName;

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CacheFile => Path.Combine(_cache, PullRequestTitleService.CacheFileName);

    private static List<string> Urls(RecordedResponseHandler h) => h.Requests.Select(r => r.Uri.AbsoluteUri).ToList();

    private void WriteCache(params (int Number, string Title)[] titles)
    {
        var body = string.Join(",", titles.Select(t => $"\"{t.Number}\": {JsonSerializer.Serialize(t.Title)}"));
        File.WriteAllText(CacheFile, $"{{ \"schema_version\": 1, \"titles\": {{ {body} }} }}");
    }

    private Dictionary<int, string> ReadCache()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(CacheFile));
        return doc.RootElement.GetProperty("titles").EnumerateObject().ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture), p => p.Value.GetString()!);
    }

    [Fact]
    public void Urls_match_upstream()
    {
        Assert.Equal("https://projects.blender.org/api/v1/repos/blender/blender/pulls?limit=50&sort=recentupdate&page=2", PullRequestTitleService.ListUrl(2));
        Assert.Equal("https://projects.blender.org/api/v1/repos/blender/blender/pulls/123", PullRequestTitleService.SingleUrl(123));
    }

    [Fact]
    public async Task Batch_walks_pages_then_looks_up_the_rest_and_writes_the_cache()
    {
        var handler = RecordedResponseHandler.FromFixtures("gitea");
        using var http = handler.CreateClient();
        var service = new PullRequestTitleService(http, _cache);

        var titles = await service.GetTitlesAsync([OnPage1, OnPage2, OnPage3, Single1, Single2, Missing], Ct);
        handler.AssertAllMatched();

        Assert.Equal(Page1Title, titles[OnPage1]);
        Assert.Equal("Experimental implementation of Dynamic Override", titles[OnPage2]);
        Assert.Equal("Sculpt: Add brush-level rake rotation toggle", titles[OnPage3]);
        Assert.Equal(Single1Title, titles[Single1]);
        Assert.Equal(Single2Title, titles[Single2]);
        Assert.False(titles.ContainsKey(Missing));

        var urls = Urls(handler);
        Assert.Equal(new[] { 1, 2, 3, 4 }.Select(PullRequestTitleService.ListUrl), urls.Take(4));
        Assert.Equal(
            new[] { Single1, Single2, Missing }.Select(PullRequestTitleService.SingleUrl).Order(),
            urls.Skip(4).Order());

        // Every title from the four pages plus the two singles, sorted by number.
        var cached = ReadCache();
        Assert.Equal(202, cached.Count);
        Assert.Equal(Single2Title, cached[Single2]);
        Assert.Equal(cached.Keys.Order(), cached.Keys);
        Assert.Empty(Directory.GetFiles(_cache, "*.tmp"));
    }

    [Fact]
    public async Task Pages_stop_once_every_wanted_number_is_known()
    {
        var handler = RecordedResponseHandler.FromFixtures("gitea");
        using var http = handler.CreateClient();
        var titles = await new PullRequestTitleService(http, _cache).GetTitlesAsync([OnPage2], Ct);

        Assert.Equal("Experimental implementation of Dynamic Override", titles[OnPage2]);
        Assert.Equal([PullRequestTitleService.ListUrl(1), PullRequestTitleService.ListUrl(2)], Urls(handler));
    }

    [Fact]
    public async Task A_page_with_nothing_new_stops_the_page_walk()
    {
        // Seed the cache with every number on page 1, as upstream's early exit expects.
        using var page1 = JsonDocument.Parse(File.ReadAllText(Path.Combine(RecordedResponseHandler.FixturesRoot, "gitea", "pulls_page1.json")));
        WriteCache([.. page1.RootElement.EnumerateArray().Select(e => (e.GetProperty("number").GetInt32(), "old"))]);

        var handler = RecordedResponseHandler.FromFixtures("gitea");
        using var http = handler.CreateClient();
        var titles = await new PullRequestTitleService(http, _cache).GetTitlesAsync([Single1], Ct);

        Assert.Equal(Single1Title, titles[Single1]);
        Assert.Equal([PullRequestTitleService.ListUrl(1), PullRequestTitleService.SingleUrl(Single1)], Urls(handler));

        // Page 1 still refreshed the titles it carried.
        Assert.Equal(Page1Title, ReadCache()[OnPage1]);
    }

    [Fact]
    public async Task Cached_titles_need_no_requests()
    {
        WriteCache((OnPage1, "Cached title"), (Single1, "Another"));
        var handler = new RecordedResponseHandler();
        using var http = handler.CreateClient();
        var service = new PullRequestTitleService(http, _cache);

        var titles = await service.GetTitlesAsync([OnPage1, Single1], Ct);
        Assert.Equal("Cached title", titles[OnPage1]);
        Assert.Equal("Cached title", await service.GetTitleAsync(OnPage1, Ct));
        Assert.Equal("Another", await service.GetCachedAsync(Single1, Ct));
        Assert.Null(await service.GetCachedAsync(Single2, Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Single_lookup_on_demand()
    {
        var handler = RecordedResponseHandler.FromFixtures("gitea");
        using var http = handler.CreateClient();
        var service = new PullRequestTitleService(http, _cache);

        Assert.Equal(Single2Title, await service.GetTitleAsync(Single2, Ct));
        Assert.Equal([PullRequestTitleService.SingleUrl(Single2)], Urls(handler));
        Assert.Equal(Single2Title, ReadCache()[Single2]);

        // Second time from the cache, and a new instance reads it from the file.
        Assert.Equal(Single2Title, await service.GetTitleAsync(Single2, Ct));
        var other = new PullRequestTitleService(new RecordedResponseHandler().CreateClient(), _cache);
        Assert.Equal(Single2Title, await other.GetTitleAsync(Single2, Ct));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Single_lookup_of_a_missing_pr_is_null_and_other_errors_throw()
    {
        var handler = RecordedResponseHandler.FromFixtures("gitea")
            .AddText(PullRequestTitleService.SingleUrl(1), "oops", "text/plain", 500)
            .AddText(PullRequestTitleService.SingleUrl(2), "{ broken", "application/json")
            .AddText(PullRequestTitleService.SingleUrl(3), "{ \"number\": 3 }", "application/json");
        using var http = handler.CreateClient();
        var service = new PullRequestTitleService(http, _cache);

        Assert.Null(await service.GetTitleAsync(Missing, Ct));
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => service.GetTitleAsync(1, Ct));
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, e.StatusCode);
        await Assert.ThrowsAnyAsync<JsonException>(() => service.GetTitleAsync(2, Ct));
        await Assert.ThrowsAnyAsync<JsonException>(() => service.GetTitleAsync(3, Ct));
        Assert.False(File.Exists(CacheFile)); // nothing learnt, nothing written
    }

    [Fact]
    public async Task Batch_survives_http_errors_and_malformed_pages()
    {
        var handler = new RecordedResponseHandler()
            .AddText(PullRequestTitleService.ListUrl(1), "[{ \"number\": ", "application/json")
            .AddText(PullRequestTitleService.SingleUrl(10), "{ \"number\": 10, \"title\": \"  Ten  \" }", "application/json")
            .AddText(PullRequestTitleService.SingleUrl(11), "bad gateway", "text/plain", 502)
            .Add(new FixtureEntry { Url = PullRequestTitleService.SingleUrl(12), Fail = "connection" })
            .AddText(PullRequestTitleService.SingleUrl(13), "<html>", "text/html");
        using var http = handler.CreateClient();
        var service = new PullRequestTitleService(http, _cache);

        var titles = await service.GetTitlesAsync([10, 11, 12, 13], Ct);

        Assert.Equal(new Dictionary<int, string> { [10] = "Ten" }, titles);
        Assert.Single(Urls(handler), u => u.Contains("page=", StringComparison.Ordinal)); // the broken page ends the page walk
        Assert.Equal(new Dictionary<int, string> { [10] = "Ten" }, ReadCache());
    }

    [Fact]
    public async Task Save_merges_with_titles_another_instance_wrote()
    {
        WriteCache((1, "one"));
        var handler = new RecordedResponseHandler()
            .AddText(PullRequestTitleService.SingleUrl(2), "{ \"number\": 2, \"title\": \"two\" }", "application/json");
        using var http = handler.CreateClient();
        var service = new PullRequestTitleService(http, _cache);
        Assert.Equal("one", await service.GetCachedAsync(1, Ct)); // loads the file now

        WriteCache((1, "one (newer elsewhere)"), (3, "three")); // another instance saves meanwhile
        Assert.Equal("two", await service.GetTitleAsync(2, Ct));

        Assert.Equal(new Dictionary<int, string> { [1] = "one", [2] = "two", [3] = "three" }, ReadCache());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"titles\": [1, 2] }")]
    [InlineData("")]
    public async Task A_corrupt_cache_reads_as_empty_and_is_replaced(string content)
    {
        File.WriteAllText(CacheFile, content);
        var handler = new RecordedResponseHandler()
            .AddText(PullRequestTitleService.SingleUrl(5), "{ \"number\": 5, \"title\": \"five\" }", "application/json");
        using var http = handler.CreateClient();

        Assert.Equal("five", await new PullRequestTitleService(http, _cache).GetTitleAsync(5, Ct));
        Assert.Equal(new Dictionary<int, string> { [5] = "five" }, ReadCache());
    }

    [Fact]
    public async Task The_cache_folder_is_created_when_saving()
    {
        var nested = Path.Combine(_cache, "a", "b");
        var handler = new RecordedResponseHandler()
            .AddText(PullRequestTitleService.SingleUrl(5), "{ \"number\": 5, \"title\": \"five\" }", "application/json");
        using var http = handler.CreateClient();
        await new PullRequestTitleService(http, nested).GetTitleAsync(5, Ct);
        Assert.True(File.Exists(Path.Combine(nested, PullRequestTitleService.CacheFileName)));
    }

    [Fact]
    public void Constructor_checks_arguments()
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentOutOfRangeException>(() => new PullRequestTitleService(http, _cache, parallelism: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PullRequestTitleService(http, _cache, parallelism: 11));
        Assert.Throws<ArgumentException>(() => new PullRequestTitleService(http, ""));
        Assert.Equal(CacheFile, new PullRequestTitleService(http, _cache).CachePath);
    }

    // --- PullRequestLabels ---

    [Theory]
    [InlineData("PR162828", 162828)]
    [InlineData("Pr12", 12)]
    [InlineData("PR162828 | x86_64", 162828)]
    [InlineData("xPR12", 0)]
    [InlineData("PR", 0)]
    [InlineData("PR0", 0)]
    [InlineData("PR99999999999", 0)]
    [InlineData(null, 0)]
    public void Number_from_a_prerelease(string? prerelease, int expected)
    {
        Assert.Equal(expected != 0, PullRequestLabels.TryParseFromPrerelease(prerelease, out var n));
        Assert.Equal(expected, n);
    }

    [Theory]
    [InlineData("PR162828", 162828)]
    [InlineData("main-PR162828", 162828)]
    [InlineData("pr5", 5)]
    [InlineData("npr-prototype", 0)]
    public void Number_from_a_branch(string branch, int expected)
    {
        Assert.Equal(expected != 0, PullRequestLabels.TryParseFromBranch(branch, out var n));
        Assert.Equal(expected, n);
    }

    [Fact]
    public void Number_of_a_build()
    {
        var time = IsoTimestamp.Epoch;
        var scraped = new BuildInfo { Link = "x", Subversion = "5.3.0-PR163602", CommitTime = time, Branch = "patch" };
        var installed = new BuildInfo { Link = "x", Subversion = "5.3.0", CommitTime = time, Branch = "PR163602" };
        var daily = new BuildInfo { Link = "x", Subversion = "5.3.0-alpha", CommitTime = time, Branch = "daily" };
        Assert.True(PullRequestLabels.TryGetNumber(scraped, out var a));
        Assert.True(PullRequestLabels.TryGetNumber(installed, out var b));
        Assert.Equal((163602, 163602), (a, b));
        Assert.False(PullRequestLabels.TryGetNumber(daily, out _));
    }

    [Fact]
    public void Label_format()
    {
        Assert.Equal("162828: Fix it", PullRequestLabels.Format(162828, "Fix it", prependNumber: true));
        Assert.Equal("Fix it", PullRequestLabels.Format(162828, "Fix it", prependNumber: false));
    }

    // --- The patch source with titles ---

    private async Task<List<AvailableBuild>> ScrapePatch(RecordedResponseHandler handler, BuilderBuildSource source, bool fetch = true, bool prepend = true)
    {
        using var http = handler.CreateClient();
        var ctx = new ScrapeContext
        {
            Http = http, CacheDirectory = _cache, Os = ScrapingHostOs, Machine = "amd64", FetchPrTitles = fetch, PrependPrNumber = prepend,
        };
        var list = new List<AvailableBuild>();
        await foreach (var b in source.ScrapeAsync(ctx, Ct))
        {
            list.Add(b);
        }

        return list;
    }

    private const HostOs ScrapingHostOs = HostOs.Windows;

    [Fact]
    public async Task Patch_scrape_fills_descriptions_then_reuses_the_cache()
    {
        var handler = RecordedResponseHandler.FromFixtures("builder", "gitea");
        var builds = await ScrapePatch(handler, BuilderBuildSource.Patch());
        handler.AssertAllMatched();

        Assert.Equal(
            [
                $"{OnPage1}: {Page1Title}",
                $"{OnPage2}: Experimental implementation of Dynamic Override",
                $"{OnPage3}: Sculpt: Add brush-level rake rotation toggle",
                $"{Single1}: {Single1Title}",
                $"{Single2}: {Single2Title}",
            ],
            builds.Select(b => b.Description));
        Assert.All(builds, b => Assert.Equal("", b.Build.CustomName)); // the title is extra text, not a rename
        Assert.True(File.Exists(CacheFile));

        // Second run: titles come from the cache file, so only the builder listing is requested.
        var second = RecordedResponseHandler.FromFixtures("builder");
        var again = await ScrapePatch(second, BuilderBuildSource.Patch(), prepend: false);
        second.AssertAllMatched();
        Assert.Single(second.Requests);
        Assert.Equal(Page1Title, again[0].Description);
        Assert.Equal(Single2Title, again[4].Description);
    }

    [Fact]
    public async Task Patch_scrape_uses_a_shared_service()
    {
        WriteCache((OnPage1, "a"), (OnPage2, "b"), (OnPage3, "c"), (Single1, "d"), (Single2, "e"));
        using var gitea = new RecordedResponseHandler().CreateClient();
        var shared = new PullRequestTitleService(gitea, _cache);

        var builds = await ScrapePatch(RecordedResponseHandler.FromFixtures("builder"), BuilderBuildSource.Patch(shared));
        Assert.Equal(["163602: a", "155204: b", "164649: c", "162828: d", "166147: e"], builds.Select(b => b.Description));
    }

    [Fact]
    public async Task Patch_scrape_without_the_setting_makes_no_title_requests()
    {
        var handler = RecordedResponseHandler.FromFixtures("builder");
        var builds = await ScrapePatch(handler, BuilderBuildSource.Patch(), fetch: false);
        handler.AssertAllMatched();
        Assert.All(builds, b => Assert.Null(b.Description));
        Assert.False(File.Exists(CacheFile));
    }

    [Fact]
    public async Task Patch_scrape_survives_a_title_service_that_cannot_connect()
    {
        var handler = RecordedResponseHandler.FromFixtures("builder")
            .Add(new FixtureEntry { Url = "https://projects.blender.org/api/v1/repos/blender/blender/pulls", IgnoreQuery = true, Fail = "connection" });
        foreach (var n in new[] { OnPage1, OnPage2, OnPage3, Single1, Single2 })
        {
            handler.Add(new FixtureEntry { Url = PullRequestTitleService.SingleUrl(n), Fail = "connection" });
        }

        var builds = await ScrapePatch(handler, BuilderBuildSource.Patch());
        handler.AssertAllMatched();
        Assert.Equal(5, builds.Count);
        Assert.All(builds, b => Assert.Null(b.Description));
    }

    [Fact]
    public async Task Daily_and_experimental_never_fetch_titles()
    {
        var handler = RecordedResponseHandler.FromFixtures("builder");
        using var http = handler.CreateClient();
        var ctx = new ScrapeContext { Http = http, CacheDirectory = _cache, Os = HostOs.Windows, Machine = "amd64", FetchPrTitles = true };
        await foreach (var _ in BuilderBuildSource.Daily().ScrapeAsync(ctx, Ct))
        {
        }

        await foreach (var _ in BuilderBuildSource.Experimental().ScrapeAsync(ctx, Ct))
        {
        }

        Assert.All(handler.Requests, r => Assert.Equal("builder.blender.org", r.Uri.Host));
    }
}
