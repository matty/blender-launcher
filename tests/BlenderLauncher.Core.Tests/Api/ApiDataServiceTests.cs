using System.Net;
using System.Text.RegularExpressions;
using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Tests.Net;

namespace BlenderLauncher.Core.Tests.Api;

public sealed class ApiDataServiceTests : IDisposable
{
    private const string Base = AppIdentity.UpstreamApiBaseUrl;
    private const string ApiUrl = Base + "blender_launcher_api.json";
    private const string SeedUrl = Base + "stable_builds_api_windows.json";

    private const string CustomApi = """
        { "api_file_version": "3.1", "blender_versions": { "6.0": "LTS", "5.2": "LTS", "4.3": "LTS", "4.2": "non-LTS" } }
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("bl-apidata-").FullName;
    private readonly DataDirectories _dirs;

    public ApiDataServiceTests() => _dirs = new DataDirectories(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ApiPath => Path.Combine(_dir, "api", "blender_launcher_api.json");

    private string SeedPath => Path.Combine(_dir, "cache", "stable_builds.json");

    private ApiDataService Service(RecordedResponseHandler handler)
    {
        var client = handler.CreateClient();
        return new ApiDataService(_dirs, () => client);
    }

    private static string SeedWithVersion(string version) =>
        Regex.Replace(BundledApiFiles.ReadStableSeed(), "\"api_file_version\": \"[^\"]*\"", $"\"api_file_version\": \"{version}\"");

    private void WriteStoredSeed(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SeedPath)!);
        File.WriteAllText(SeedPath, text);
    }

    private static string? VersionIn(string path) =>
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("api_file_version").GetString();

    // ---- Bundled files ----

    [Fact]
    public void Bundled_files_are_upstreams_unchanged()
    {
        Assert.Equal(Fixtures.ReadText("upstream-api-blender_launcher_api.json").ReplaceLineEndings("\n"),
            BundledApiFiles.ReadApi().ReplaceLineEndings("\n"));
        Assert.Equal(Fixtures.ReadText("upstream-api-stable_builds_api_windows.json").ReplaceLineEndings("\n"),
            BundledApiFiles.ReadStableSeed().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Bundled_seed_is_a_valid_stable_cache()
    {
        var cache = ScraperCache.FromJson(BundledApiFiles.ReadStableSeed());

        Assert.Equal("29.0", cache.ApiFileVersion);
        Assert.Equal(54, cache.Folders.Count);
    }

    [Fact]
    public void Before_loading_the_bundled_data_is_current()
    {
        var service = Service(new RecordedResponseHandler());

        Assert.Equal(ApiDataSource.Bundled, service.CurrentSource);
        Assert.Equal(new ApiFileVersion(3, 0), service.Current.FileVersion);
        Assert.True(service.IsLts(4, 2));
        Assert.False(service.IsLts(4, 3));
    }

    [Fact]
    public void Paths_follow_the_storage_plan()
    {
        var service = Service(new RecordedResponseHandler());

        Assert.Equal(ApiPath, service.ApiFilePath);
        Assert.Equal(SeedPath, service.StableCachePath);
        Assert.Equal(ApiUrl, service.ApiFileUrl.AbsoluteUri);
        Assert.Equal(SeedUrl, service.StableSeedUrl.AbsoluteUri);
    }

    // ---- LoadAsync ----

    [Fact]
    public async Task Load_with_nothing_stored_uses_the_bundle_and_seeds_the_stable_cache()
    {
        var handler = new RecordedResponseHandler();
        var service = Service(handler);
        var raised = 0;
        service.Changed += (_, _) => raised++;

        var seed = await service.LoadAsync(Ct);

        Assert.Equal(StableSeedOutcome.Written, seed);
        Assert.Equal(ApiDataSource.Bundled, service.CurrentSource);
        Assert.Equal(0, raised);
        Assert.Equal(BundledApiFiles.ReadStableSeed(), File.ReadAllText(SeedPath));
        Assert.False(File.Exists(ApiPath));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Load_prefers_the_stored_api_file_and_raises_changed()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ApiPath)!);
        File.WriteAllText(ApiPath, CustomApi);
        var service = Service(new RecordedResponseHandler());
        ApiDataChangedEventArgs? args = null;
        service.Changed += (_, e) => args = e;

        await service.LoadAsync(Ct);

        Assert.Equal(ApiDataSource.Stored, service.CurrentSource);
        Assert.True(service.IsLts(4, 3));
        Assert.False(service.IsLts(4, 2));
        Assert.NotNull(args);
        Assert.Equal(new ApiFileVersion(3, 0), args.Previous.FileVersion);
        Assert.Same(service.Current, args.Current);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{ \"api_file_version\": \"9.0\", \"blender_versions\": {} }")]
    public async Task Load_falls_back_to_the_bundle_when_the_stored_file_is_unusable(string stored)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ApiPath)!);
        File.WriteAllText(ApiPath, stored);
        var service = Service(new RecordedResponseHandler());

        await service.LoadAsync(Ct);

        Assert.Equal(ApiDataSource.Bundled, service.CurrentSource);
        Assert.Equal(25, service.Current.Series.Count);
    }

    [Theory]
    [InlineData("29.1", StableSeedOutcome.Kept)] // the scraper bumps the minor on each save
    [InlineData("29.0", StableSeedOutcome.Kept)]
    [InlineData("28.9", StableSeedOutcome.Written)]
    [InlineData("rubbish", StableSeedOutcome.Written)] // unreadable version: replace, as upstream
    public async Task Load_seeds_the_stable_cache_only_when_the_bundle_is_newer(string stored, StableSeedOutcome expected)
    {
        WriteStoredSeed(SeedWithVersion(stored));
        var service = Service(new RecordedResponseHandler());

        Assert.Equal(expected, await service.LoadAsync(Ct));
        Assert.Equal(expected == StableSeedOutcome.Written ? "29.0" : stored, VersionIn(SeedPath));
    }

    [Fact]
    public async Task Load_replaces_a_corrupt_stable_cache()
    {
        WriteStoredSeed("{ truncated");
        var service = Service(new RecordedResponseHandler());

        Assert.Equal(StableSeedOutcome.Written, await service.LoadAsync(Ct));
        Assert.Equal("29.0", VersionIn(SeedPath));
    }

    // ---- RefreshAsync over the recorded raw files ----

    [Fact]
    public async Task Refresh_fetches_both_raw_files()
    {
        var handler = RecordedResponseHandler.FromFixtures("raw-api");
        var service = Service(handler);
        var raised = 0;
        service.Changed += (_, _) => raised++;

        var result = await service.RefreshAsync(Ct);

        handler.AssertAllMatched();
        Assert.True(result.Succeeded);
        Assert.True(result.ApiDataFetched);
        Assert.False(result.ApiDataChanged); // the hosted file equals the bundled one today
        Assert.Equal(0, raised);
        Assert.Equal(ApiDataSource.Hosted, service.CurrentSource);
        Assert.Equal(StableSeedOutcome.Written, result.StableSeed);
        Assert.False(result.StableSeedFromBundle);
        Assert.Equal(File.ReadAllText(Path.Combine(RecordedResponseHandler.FixturesRoot, "raw-api", "blender_launcher_api.json")), File.ReadAllText(ApiPath));
        Assert.Equal("29.0", VersionIn(SeedPath));
        Assert.Equal([ApiUrl, SeedUrl], handler.Requests.Select(r => r.Uri.AbsoluteUri));
        Assert.All(handler.Requests, r => Assert.Null(r.Authorization));
    }

    [Fact]
    public async Task The_token_is_never_sent_to_the_raw_host()
    {
        var handler = RecordedResponseHandler.FromFixtures("raw-api");
        var client = handler.CreateClient(Core.Net.ConnectionOptions.Default with { GitHubToken = "ghp_secret" });
        var service = new ApiDataService(_dirs, () => client);

        await service.RefreshAsync(Ct);

        Assert.All(handler.Requests, r => Assert.Null(r.Authorization));
    }

    [Fact]
    public async Task Refresh_with_new_data_raises_changed_and_lookups_see_it_without_a_restart()
    {
        var handler = new RecordedResponseHandler()
            .AddText(ApiUrl, CustomApi, "text/plain")
            .AddText(SeedUrl, BundledApiFiles.ReadStableSeed(), "text/plain");
        var service = Service(handler);
        ILtsLookup lookup = service; // what a long-lived holder keeps
        var events = new List<ApiDataChangedEventArgs>();
        service.Changed += (_, e) => events.Add(e);

        Assert.False(lookup.IsLts(6, 0));
        var result = await service.RefreshAsync(Ct);

        Assert.True(result.ApiDataChanged);
        Assert.True(lookup.IsLts(6, 0));
        var e = Assert.Single(events);
        Assert.Equal(ApiDataSource.Hosted, e.Source);
        Assert.Equal(new ApiFileVersion(3, 1), e.Current.FileVersion);
        Assert.Equal(CustomApi, File.ReadAllText(ApiPath));

        // A second service (the next start) reads the stored copy.
        var next = Service(new RecordedResponseHandler());
        await next.LoadAsync(Ct);
        Assert.True(next.IsLts(6, 0));
    }

    [Theory]
    [InlineData("29.9", "29.10", StableSeedOutcome.Written)] // upstream's float("29.10") == 29.1 < 29.9 kept it
    [InlineData("29.10", "29.9", StableSeedOutcome.Kept)]
    [InlineData("29.10", "29.10", StableSeedOutcome.Kept)]
    [InlineData("29.10", "30.0", StableSeedOutcome.Written)]
    public async Task Refresh_replaces_the_stable_cache_by_integer_version(string stored, string hosted, StableSeedOutcome expected)
    {
        WriteStoredSeed(SeedWithVersion(stored));
        var handler = new RecordedResponseHandler()
            .AddText(ApiUrl, BundledApiFiles.ReadApi(), "text/plain")
            .AddText(SeedUrl, SeedWithVersion(hosted), "text/plain");

        var result = await Service(handler).RefreshAsync(Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.StableSeed);
        Assert.Equal(expected == StableSeedOutcome.Written ? hosted : stored, VersionIn(SeedPath));
    }

    // ---- Failures ----

    [Fact]
    public async Task Network_failure_keeps_the_data_reports_and_seeds_from_the_bundle()
    {
        var handler = new RecordedResponseHandler()
            .Add(new FixtureEntry { Url = ApiUrl, Fail = "connection" })
            .Add(new FixtureEntry { Url = SeedUrl, Fail = "timeout" });
        var service = Service(handler);
        var before = service.Current;

        var result = await service.RefreshAsync(Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Errors.Count);
        Assert.All(result.Errors, e =>
        {
            Assert.Equal(ApiDataService.SourceId, e.SourceId);
            Assert.Equal(ScrapeMessageKeys.ConnectionFailed, e.MessageKey);
        });
        Assert.Same(before, service.Current);
        Assert.False(result.ApiDataChanged);
        Assert.False(result.ApiDataFetched);
        Assert.True(result.StableSeedFromBundle);
        Assert.Equal(StableSeedOutcome.Written, result.StableSeed);
        Assert.Equal("29.0", VersionIn(SeedPath));
        Assert.False(File.Exists(ApiPath));
    }

    [Fact]
    public async Task Network_failure_with_a_newer_stored_seed_keeps_it()
    {
        WriteStoredSeed(SeedWithVersion("29.4"));
        var handler = new RecordedResponseHandler()
            .Add(new FixtureEntry { Url = ApiUrl, Fail = "connection" })
            .Add(new FixtureEntry { Url = SeedUrl, Fail = "connection" });

        var result = await Service(handler).RefreshAsync(Ct);

        Assert.Equal(StableSeedOutcome.Kept, result.StableSeed);
        Assert.Equal("29.4", VersionIn(SeedPath));
    }

    [Fact]
    public async Task Http_errors_are_reported_with_their_status()
    {
        var handler = new RecordedResponseHandler()
            .AddText(ApiUrl, "404: Not Found", "text/plain", status: 404)
            .AddText(SeedUrl, "", "text/plain", status: 503);

        var result = await Service(handler).RefreshAsync(Ct);

        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable], result.Errors.Select(e => e.Status));
        Assert.All(result.Errors, e => Assert.Equal(ScrapeMessageKeys.HttpStatus, e.MessageKey));
    }

    [Fact]
    public async Task A_rate_limited_response_is_reported_as_such()
    {
        var handler = new RecordedResponseHandler()
            .AddText(ApiUrl, "rate limit exceeded", "text/plain", status: 429, headers: [new("Retry-After", "60")])
            .AddText(SeedUrl, BundledApiFiles.ReadStableSeed(), "text/plain");

        var result = await Service(handler).RefreshAsync(Ct);

        Assert.Equal(ScrapeMessageKeys.GitHubRateLimited, Assert.Single(result.Errors).MessageKey);
    }

    [Theory]
    [InlineData("<html>not json</html>")]
    [InlineData("{ \"api_file_version\": \"4.0\", \"blender_versions\": {} }")]
    public async Task An_invalid_hosted_api_file_is_rejected(string body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ApiPath)!);
        File.WriteAllText(ApiPath, CustomApi);
        var handler = new RecordedResponseHandler()
            .AddText(ApiUrl, body, "text/plain")
            .AddText(SeedUrl, BundledApiFiles.ReadStableSeed(), "text/plain");
        var service = Service(handler);
        await service.LoadAsync(Ct);

        var result = await service.RefreshAsync(Ct);

        Assert.Equal(ScrapeMessageKeys.Generic, Assert.Single(result.Errors).MessageKey);
        Assert.Equal(ApiDataSource.Stored, service.CurrentSource);
        Assert.Equal(CustomApi, File.ReadAllText(ApiPath));
    }

    [Fact]
    public async Task An_invalid_hosted_seed_is_rejected_and_the_bundle_is_used()
    {
        WriteStoredSeed(SeedWithVersion("20.0"));
        var handler = new RecordedResponseHandler()
            .AddText(ApiUrl, BundledApiFiles.ReadApi(), "text/plain")
            .AddText(SeedUrl, "{ \"api_file_version\": \"99.0\", \"folders\": { \"not-semver\": {} } }", "text/plain");

        var result = await Service(handler).RefreshAsync(Ct);

        Assert.Single(result.Errors);
        Assert.True(result.StableSeedFromBundle);
        Assert.Equal(StableSeedOutcome.Written, result.StableSeed);
        Assert.Equal("29.0", VersionIn(SeedPath));
    }

    [Fact]
    public async Task Cancellation_by_the_caller_throws()
    {
        var service = Service(RecordedResponseHandler.FromFixtures("raw-api"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync(cts.Token));
    }

    [Fact]
    public void Data_directories_derive_from_the_root()
    {
        var dirs = new DataDirectories(@"C:\data\BlenderLauncherNet");

        Assert.Equal(@"C:\data\BlenderLauncherNet\api", dirs.Api);
        Assert.Equal(@"C:\data\BlenderLauncherNet\cache", dirs.Cache);
        Assert.Equal(@"C:\data\BlenderLauncherNet\state", dirs.State);
        Assert.Equal(@"C:\data\BlenderLauncherNet\logs", dirs.Logs);
        Assert.EndsWith(@"\BlenderLauncherNet\data", DataDirectories.Default().Root, StringComparison.Ordinal);
    }
}
