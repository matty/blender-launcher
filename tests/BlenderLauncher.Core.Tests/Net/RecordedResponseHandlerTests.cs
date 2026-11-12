using System.Net;
using BlenderLauncher.Core.Net;

namespace BlenderLauncher.Core.Tests.Net;

public class RecordedResponseHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Serves_the_recorded_body_and_headers()
    {
        var handler = RecordedResponseHandler.FromFixtures("sample");
        using var client = handler.CreateClient();
        using var response = await client.GetAsync("https://builder.blender.org/download/daily/?format=json&v=1", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero), response.Content.Headers.LastModified);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("windows.amd64-release.zip", body, StringComparison.Ordinal);
        Assert.Equal(body.Length, response.Content.Headers.ContentLength);
        handler.AssertAllMatched();
    }

    [Fact]
    public async Task Exact_match_is_query_sensitive()
    {
        var handler = RecordedResponseHandler.FromFixtures("sample");
        using var client = handler.CreateClient();
        var e = await Assert.ThrowsAsync<RecordedResponseMissingException>(
            () => client.GetAsync("https://builder.blender.org/download/daily/?format=json&v=2", Ct));
        Assert.Contains("GET https://builder.blender.org/download/daily/?format=json&v=2", e.Message, StringComparison.Ordinal);
        Assert.Single(handler.Unmatched);
        Assert.Throws<Xunit.Sdk.FailException>(handler.AssertAllMatched);
    }

    [Fact]
    public async Task Ignore_query_entries_match_any_query()
    {
        var handler = RecordedResponseHandler.FromFixtures("sample");
        using var client = handler.CreateClient();
        using var a = await client.GetAsync("https://example.org/ignored?page=2", Ct);
        using var b = await client.GetAsync("https://example.org/ignored", Ct);
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
    }

    [Fact]
    public async Task Repeated_entries_are_served_in_order_and_the_last_repeats()
    {
        var handler = RecordedResponseHandler.FromFixtures("sample");
        using var client = handler.CreateClient();
        var first = await client.GetStringAsync("https://download.blender.org/release/", Ct);
        var second = await client.GetStringAsync("https://download.blender.org/release/", Ct);
        var third = await client.GetStringAsync("https://download.blender.org/release/", Ct);

        Assert.True(CloudflareChallenge.IsChallenge(first));
        Assert.False(CloudflareChallenge.IsChallenge(second));
        Assert.Equal(second, third);
    }

    [Fact]
    public async Task Head_gets_the_recorded_length_and_date()
    {
        var handler = RecordedResponseHandler.FromFixtures("sample");
        using var client = handler.CreateClient();
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://www.bforartists.de/data/bfa-1.0.zip"), Ct);

        Assert.Equal(123456789, response.Content.Headers.ContentLength);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 30, 0, TimeSpan.Zero), response.Content.Headers.LastModified);
    }

    [Fact]
    public async Task Method_is_part_of_the_key()
    {
        var handler = RecordedResponseHandler.FromFixtures("sample");
        using var client = handler.CreateClient();
        await Assert.ThrowsAsync<RecordedResponseMissingException>(() => client.GetAsync("https://www.bforartists.de/data/bfa-1.0.zip", Ct));
    }

    [Fact]
    public async Task Rate_limit_headers_are_served()
    {
        var handler = RecordedResponseHandler.FromFixtures("sample");
        using var client = handler.CreateClient();
        using var ok = await client.GetAsync("https://api.github.com/repos/UPBGE/upbge/releases", Ct);
        var limit = GitHubRateLimit.From(ok);
        Assert.Equal(new GitHubRateLimit(60, 59, DateTimeOffset.FromUnixTimeSeconds(1793000000), "core"), limit);
        Assert.False(GitHubRateLimit.IsRateLimited(ok));

        using var limited = await client.GetAsync("https://api.github.com/rate-limited", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, limited.StatusCode);
        Assert.True(GitHubRateLimit.IsRateLimited(limited));
    }

    [Fact]
    public async Task Recorded_connection_failures_throw_http_request_exception()
    {
        var handler = RecordedResponseHandler.FromFixtures("sample");
        using var client = handler.CreateClient();
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://down.example.org/", Ct));
        Assert.Equal(HttpRequestError.ConnectionError, e.HttpRequestError);
    }

    [Fact]
    public async Task Recorded_timeouts_throw_task_cancelled()
    {
        var handler = new RecordedResponseHandler().Add(new FixtureEntry { Url = "https://slow.example/", Fail = "timeout" });
        using var client = handler.CreateClient();
        var e = await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync("https://slow.example/", Ct));
        Assert.True(NetTimeouts.IsTimeout(e, Ct));
    }

    [Fact]
    public void Missing_source_folder_fails_loudly()
    {
        Assert.Throws<FileNotFoundException>(() => RecordedResponseHandler.FromFixtures("no-such-source"));
    }

    [Fact]
    public void Manifest_round_trips()
    {
        var folder = Directory.CreateTempSubdirectory("bl-manifest-").FullName;
        try
        {
            var path = Path.Combine(folder, FixtureManifest.FileName);
            FixtureEntry[] entries =
            [
                new() { Url = "https://a.example/x?y=1", IgnoreQuery = true, File = "x.json", Headers = [new("Content-Type", "application/json")], Recorded = "2026-11-11T00:00:00Z" },
                new() { Method = "HEAD", Url = "https://b.example/", Status = 302, Headers = [new("Location", "https://c.example/")] },
                new() { Url = "https://d.example/", Fail = "connection" },
            ];
            FixtureManifest.Save(path, entries);
            var loaded = FixtureManifest.Load(path);

            Assert.Equal(3, loaded.Count);
            Assert.Equal(entries[0].Url, loaded[0].Url);
            Assert.True(loaded[0].IgnoreQuery);
            Assert.Equal("x.json", loaded[0].File);
            Assert.Equal("application/json", loaded[0].HeaderValue("content-type"));
            Assert.Equal("HEAD", loaded[1].Method);
            Assert.Equal(302, loaded[1].Status);
            Assert.Equal("https://c.example/", loaded[1].HeaderValue("Location"));
            Assert.Equal("connection", loaded[2].Fail);
            Assert.Equal(200, loaded[2].Status);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Every_net_fixture_manifest_is_valid_and_its_files_exist()
    {
        foreach (var dir in Directory.GetDirectories(RecordedResponseHandler.FixturesRoot))
        {
            var manifest = Path.Combine(dir, FixtureManifest.FileName);
            if (!File.Exists(manifest))
            {
                continue;
            }

            foreach (var entry in FixtureManifest.Load(manifest))
            {
                Assert.True(Uri.TryCreate(entry.Url, UriKind.Absolute, out _), $"{manifest}: bad url {entry.Url}");
                if (entry.File is not null)
                {
                    Assert.True(File.Exists(Path.Combine(dir, entry.File)), $"{manifest}: missing file {entry.File}");
                }
            }
        }
    }
}
