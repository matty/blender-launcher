using System.Net;
using BlenderLauncher.Core.Net;

namespace BlenderLauncher.Core.Tests.Net;

public class RedirectHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Relative_location_resolves_against_the_current_url()
    {
        var handler = new RecordedResponseHandler()
            .AddRedirect("https://download.blender.org/release/Blender4.2", "Blender4.2/", 301)
            .AddText("https://download.blender.org/release/Blender4.2/", "listing");
        using var client = handler.CreateClient();
        using var response = await client.GetAsync("https://download.blender.org/release/Blender4.2", Ct);

        Assert.Equal("listing", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("https://download.blender.org/release/Blender4.2/", response.RequestMessage!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task See_other_turns_post_into_get()
    {
        var handler = new RecordedResponseHandler()
            .AddRedirect("https://example.org/form", "https://example.org/done", 303, "POST")
            .AddText("https://example.org/done", "ok");
        using var client = handler.CreateClient();
        using var response = await client.PostAsync("https://example.org/form", new StringContent("x"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpMethod.Get, handler.Requests.Last().Method);
    }

    [Fact]
    public async Task Temporary_redirect_keeps_head()
    {
        var handler = new RecordedResponseHandler()
            .AddRedirect("https://example.org/a.zip", "https://cdn.example.org/a.zip", 307, "HEAD")
            .Add(new FixtureEntry { Method = "HEAD", Url = "https://cdn.example.org/a.zip", Headers = [new("Content-Length", "42")] });
        using var client = handler.CreateClient();
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://example.org/a.zip"), Ct);

        Assert.Equal(42, response.Content.Headers.ContentLength);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Head, r.Method));
    }

    [Fact]
    public async Task Https_to_http_is_not_followed()
    {
        var handler = new RecordedResponseHandler().AddRedirect("https://example.org/", "http://example.org/plain");
        using var client = handler.CreateClient();
        using var response = await client.GetAsync("https://example.org/", Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Redirect_loops_stop_after_the_limit()
    {
        var handler = new RecordedResponseHandler().AddRedirect("https://example.org/loop", "/loop");
        using var client = handler.CreateClient();
        using var response = await client.GetAsync("https://example.org/loop", Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(RedirectHandler.MaxRedirects + 1, handler.Requests.Count);
    }

    [Fact]
    public async Task Redirect_without_location_is_returned()
    {
        var handler = new RecordedResponseHandler().Add(new FixtureEntry { Url = "https://example.org/", Status = 302 });
        using var client = handler.CreateClient();
        using var response = await client.GetAsync("https://example.org/", Ct);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
    }

    [Fact]
    public async Task Cookies_and_authorization_set_by_the_caller_do_not_cross_origins()
    {
        var handler = new RecordedResponseHandler()
            .AddRedirect("https://a.example/", "https://b.example/")
            .AddText("https://b.example/", "ok");
        using var client = handler.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://a.example/");
        request.Headers.TryAddWithoutValidation("Authorization", "Basic abc");
        request.Headers.TryAddWithoutValidation("Cookie", "c=1");
        request.Headers.TryAddWithoutValidation("Accept", "text/html");
        using var _ = await client.SendAsync(request, Ct);

        var second = handler.Requests.Last();
        Assert.False(second.Headers.ContainsKey("Authorization"));
        Assert.False(second.Headers.ContainsKey("Cookie"));
        Assert.Equal("text/html", second.Headers["Accept"]);
    }
}
