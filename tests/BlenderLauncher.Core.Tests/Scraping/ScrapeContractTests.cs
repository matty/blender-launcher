using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Tests.Net;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Tests.Scraping;

/// <summary>The shared scraper types: <see cref="AvailableBuild"/>, <see cref="ScrapeContext"/>, <see cref="ScrapeError"/>, <see cref="IBuildSource"/>.</summary>
public class ScrapeContractTests
{
    private static readonly BuildInfo Build = new()
    {
        Link = "https://download.blender.org/release/Blender4.2/blender-4.2.3-windows-x64.zip",
        Subversion = "4.2.3",
        CommitTime = IsoTimestamp.Aware(new DateTime(2024, 10, 15, 8, 0, 0), TimeSpan.Zero),
        Branch = "stable",
    };

    [Fact]
    public void Available_build_is_changed_with_with()
    {
        var a = new AvailableBuild(Build) { SizeBytes = 400_000_000 };
        Assert.False(a.IsNew);
        var b = a with { IsNew = true };
        Assert.True(b.IsNew);
        Assert.False(a.IsNew);
        Assert.Equal(400_000_000, b.SizeBytes);
        Assert.Null(b.Description);
        Assert.Same(Build, b.Build);
    }

    [Fact]
    public void Context_defaults()
    {
        using var http = new HttpClient();
        var ctx = new ScrapeContext { Http = http, CacheDirectory = "cache" };
        Assert.Equal(new SemVer(3, 0, 0), ctx.MinimumStableVersion);
        Assert.Same(ApiData.Empty, ctx.Lts);
        Assert.Equal(BuildPlatform.CurrentOs, ctx.Os);
        Assert.Equal(BuildPlatform.CurrentMachine, ctx.Machine);
        Assert.Same(TimeProvider.System, ctx.Time);
        Assert.False(ctx.FetchPrTitles || ctx.RebuildStableCache || ctx.DailyArchive || ctx.ExperimentalArchive || ctx.PatchArchive);
    }

    [Theory]
    [InlineData(Channel.Daily, true, false, false)]
    [InlineData(Channel.Experimental, false, true, false)]
    [InlineData(Channel.Patch, false, false, true)]
    [InlineData(Channel.Stable, false, false, false)]
    public void Archive_toggles_by_channel(Channel channel, bool daily, bool experimental, bool patch)
    {
        using var http = new HttpClient();
        var ctx = new ScrapeContext { Http = http, CacheDirectory = "c" };
        Assert.False(ctx.IncludesArchive(channel));
        Assert.Equal(daily, (ctx with { DailyArchive = true }).IncludesArchive(channel));
        Assert.Equal(experimental, (ctx with { ExperimentalArchive = true }).IncludesArchive(channel));
        Assert.Equal(patch, (ctx with { PatchArchive = true }).IncludesArchive(channel));
    }

    [Fact]
    public void Errors_from_exceptions()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 11, 11, 14, 5, 0, TimeSpan.Zero));

        var e = ScrapeError.FromException("daily", new HttpRequestException(HttpRequestError.ConnectionError, "refused"), time);
        Assert.Equal(ScrapeMessageKeys.ConnectionFailed, e.MessageKey);
        Assert.Equal([new("time", "14:05")], e.MessageArgs);
        Assert.Null(e.Status);

        e = ScrapeError.FromException("daily", new TaskCanceledException("timeout"), time);
        Assert.Equal(ScrapeMessageKeys.ConnectionFailed, e.MessageKey);

        e = ScrapeError.FromException("upbge-stable", new HttpRequestException("bad", null, HttpStatusCode.BadGateway));
        Assert.Equal(ScrapeMessageKeys.HttpStatus, e.MessageKey);
        Assert.Equal(HttpStatusCode.BadGateway, e.Status);
        Assert.Equal([new("source", "upbge-stable"), new("status", "502")], e.MessageArgs);

        e = ScrapeError.FromException("daily", new JsonException("bad json"));
        Assert.Equal(ScrapeMessageKeys.Generic, e.MessageKey);
        Assert.Equal([new("err", "bad json")], e.MessageArgs);
        Assert.Contains("JsonException", e.Detail, StringComparison.Ordinal);

        var own = new ScrapeError("stable", ScrapeMessageKeys.CloudflareBlocked);
        Assert.Same(own, ScrapeError.FromException("ignored", new ScrapeSourceException(own)));
    }

    [Fact]
    public async Task Rate_limit_error_from_a_recorded_response()
    {
        var handler = RecordedResponseHandler.FromFixtures("sample");
        using var client = handler.CreateClient();
        using var response = await client.GetAsync("https://api.github.com/rate-limited", TestContext.Current.CancellationToken);

        var error = ScrapeError.GitHubRateLimited(BuildSourceIds.UpbgeStable, response);
        Assert.Equal(ScrapeMessageKeys.GitHubRateLimited, error.MessageKey);
        Assert.Equal(HttpStatusCode.Forbidden, error.Status);
        Assert.Contains("2026-10-26", error.Detail, StringComparison.Ordinal); // epoch 1793000000
    }

    [Fact]
    public void Upstream_message_keys_exist_in_the_english_strings()
    {
        var t = new BlenderLauncher.Core.Localisation.Translator("en");
        foreach (var key in new[] { ScrapeMessageKeys.CloudflareBlocked, ScrapeMessageKeys.NoStableReleases, ScrapeMessageKeys.ConnectionFailed, ScrapeMessageKeys.Generic })
        {
            Assert.True(t.HasKey(key, "en"), key);
        }
    }

    [Fact]
    public async Task A_source_may_fail_after_yielding_some_builds()
    {
        // The pattern the scrape service follows: keep what was yielded, then record the error.
        using var http = new HttpClient();
        var ctx = new ScrapeContext { Http = http, CacheDirectory = "c" };
        var source = new HalfFailingSource();
        var got = new List<AvailableBuild>();
        ScrapeError? error = null;
        try
        {
            await foreach (var b in source.ScrapeAsync(ctx, TestContext.Current.CancellationToken))
            {
                got.Add(b);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            error = ScrapeError.FromException(source.Id, e, ctx.Time);
        }

        Assert.Single(got);
        Assert.Equal(ScrapeMessageKeys.CloudflareBlocked, error!.MessageKey);
        Assert.Equal("stable", error.SourceId);
    }

    [Theory]
    [InlineData("<html><title>Just a moment...</title></html>", true)]
    [InlineData("<script>window._cf_chl_opt={}</script>", true)]
    [InlineData("<script src=\"/cdn-cgi/challenge-platform/h/b\"></script>", true)]
    [InlineData("<a href=\"Blender4.2/\">Blender4.2/</a>", false)]
    [InlineData("just a moment", false)]
    public void Cloudflare_markers(string body, bool challenge) => Assert.Equal(challenge, CloudflareChallenge.IsChallenge(body));

    private sealed class HalfFailingSource : IBuildSource
    {
        public string Id => BuildSourceIds.Stable;

        public IReadOnlyList<Channel> Channels => [Channel.Stable, Channel.Lts];

        public async IAsyncEnumerable<AvailableBuild> ScrapeAsync(ScrapeContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new AvailableBuild(Build.PromoteLts(context.Lts));
            throw new ScrapeSourceException(new ScrapeError(Id, ScrapeMessageKeys.CloudflareBlocked, HttpStatusCode.OK, "challenge page 3 times"));
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
