using System.Net;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Net;

namespace BlenderLauncher.Core.Tests.Net;

public class GitHubTokenTests
{
    private const string Token = "ghp_0123456789abcdefghijABCDEFGHIJ012345";
    private static readonly ConnectionOptions WithToken = ConnectionOptions.Default with { GitHubToken = Token };

    [Theory]
    [InlineData("https://api.github.com/repos/UPBGE/upbge/releases", true)]
    [InlineData("https://API.GitHub.com/repos/x", true)]
    [InlineData("https://api.github.com:443/repos/x", true)]
    [InlineData("http://api.github.com/repos/x", false)]
    [InlineData("https://api.github.com:8443/repos/x", false)]
    [InlineData("https://api.github.com.evil.example/repos/x", false)]
    [InlineData("https://evil.example/?u=api.github.com", false)]
    [InlineData("https://raw.githubusercontent.com/Victor-IX/Blender-Launcher-V2/main/x.json", false)]
    [InlineData("https://objects.githubusercontent.com/asset", false)]
    [InlineData("https://github.com/UPBGE/upbge/releases", false)]
    public async Task Token_goes_to_api_github_com_only(string url, bool expected)
    {
        Assert.Equal(expected, GitHubTokenHandler.IsGitHubApi(new Uri(url)));

        var handler = new RecordedResponseHandler().AddText(url, "{}", "application/json");
        using var client = handler.CreateClient(WithToken);
        using var _ = await client.GetAsync(url, TestContext.Current.CancellationToken);

        var auth = Assert.Single(handler.Requests).Authorization;
        Assert.Equal(expected ? "token " + Token : null, auth);
    }

    [Fact]
    public async Task No_token_when_none_is_set()
    {
        var handler = new RecordedResponseHandler().AddText("https://api.github.com/x", "{}");
        using var client = handler.CreateClient();
        using var _ = await client.GetAsync("https://api.github.com/x", TestContext.Current.CancellationToken);
        Assert.Null(Assert.Single(handler.Requests).Authorization);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("ghp_abc def", false)]
    [InlineData("ghp_abc\r\nX: y", false)]
    [InlineData(" ghp_abc ", true)]
    public void Unusable_tokens_are_not_sent(string? token, bool usable)
    {
        Assert.Equal(usable, GitHubTokenHandler.IsUsableToken(token));
    }

    [Fact]
    public async Task Redirect_to_another_host_drops_the_token()
    {
        // GitHub release assets: api.github.com answers 302 to objects.githubusercontent.com.
        var handler = RecordedResponseHandler.FromFixtures("sample");
        using var client = handler.CreateClient(WithToken);
        using var response = await client.GetAsync("https://api.github.com/repos/UPBGE/upbge/releases/assets/1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://objects.githubusercontent.com/asset/1?sig=abc", response.RequestMessage!.RequestUri!.AbsoluteUri);
        var requests = handler.Requests.ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal("token " + Token, requests[0].Authorization);
        Assert.Null(requests[1].Authorization);
        Assert.Equal(requests[0].UserAgent, requests[1].UserAgent);
        handler.AssertAllMatched();
    }

    [Fact]
    public async Task Redirect_within_api_github_com_keeps_the_token()
    {
        // A renamed repository answers 301 to its new API URL.
        var handler = new RecordedResponseHandler()
            .AddRedirect("https://api.github.com/repos/old/name/releases", "/repositories/123/releases", 301)
            .AddText("https://api.github.com/repositories/123/releases", "[]", "application/json");
        using var client = handler.CreateClient(WithToken);
        using var _ = await client.GetAsync("https://api.github.com/repos/old/name/releases", TestContext.Current.CancellationToken);

        Assert.All(handler.Requests, r => Assert.Equal("token " + Token, r.Authorization));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Redirect_from_elsewhere_to_api_github_com_gains_the_token()
    {
        var handler = new RecordedResponseHandler()
            .AddRedirect("https://example.org/latest", "https://api.github.com/repos/a/b/releases/latest")
            .AddText("https://api.github.com/repos/a/b/releases/latest", "{}", "application/json");
        using var client = handler.CreateClient(WithToken);
        using var _ = await client.GetAsync("https://example.org/latest", TestContext.Current.CancellationToken);

        var requests = handler.Requests.ToList();
        Assert.Null(requests[0].Authorization);
        Assert.Equal("token " + Token, requests[1].Authorization);
    }

    [Fact]
    public async Task Token_never_reaches_the_log()
    {
        var folder = Directory.CreateTempSubdirectory("bl-net-log-").FullName;
        var lines = new List<string>();
        try
        {
            await using (var log = Log.Open(folder, LogLevel.Debug, new LogOptions { Listener = (_, l) => { lock (lines) { lines.Add(l); } } }))
            {
                var handler = RecordedResponseHandler.FromFixtures("sample")
                    .AddText("https://api.github.com/unauthorised", "{}", "application/json", 401);
                using var provider = new HttpClientProvider(WithToken with { ProxyUser = "u", ProxyPassword = "hunter2" }, log, _ => handler);
                var ct = TestContext.Current.CancellationToken;
                using (await provider.Client.GetAsync("https://api.github.com/repos/UPBGE/upbge/releases/assets/1", ct))
                using (await provider.Client.GetAsync("https://api.github.com/unauthorised", ct))
                {
                }

                await Assert.ThrowsAsync<HttpRequestException>(() => provider.Client.GetAsync("https://down.example.org/", ct));
                await log.FlushAsync();
            }

            var all = string.Join("\n", lines) + File.ReadAllText(Path.Combine(folder, Log.FileName));
            Assert.DoesNotContain(Token, all, StringComparison.Ordinal);
            Assert.DoesNotContain("0123456789abcdefghij", all, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", all, StringComparison.Ordinal);
            Assert.Contains("GET https://api.github.com/repos/UPBGE/upbge/releases/assets/1 (authenticated) -> 302", all, StringComparison.Ordinal);
            Assert.Contains("GET https://objects.githubusercontent.com/asset/1?sig=abc -> 200", all, StringComparison.Ordinal);
            Assert.Contains("returned 401", all, StringComparison.Ordinal);
            Assert.Contains("GET https://down.example.org/ failed", all, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
