using System.Net;
using BlenderLauncher.Core.Net;

namespace BlenderLauncher.Core.Tests.Net;

public class HttpClientProviderTests
{
    [Fact]
    public void User_agent_has_no_user_id_unless_one_is_set()
    {
        Assert.Equal("BlenderLauncherNet/1.2.3 (Windows 10.0.26100; x64)", UserAgent.Build("1.2.3", "Windows 10.0.26100", "x64", null));
        Assert.Equal("BlenderLauncherNet/1.2.3 (Windows 10.0.26100; x64)", UserAgent.Build("1.2.3", "Windows 10.0.26100", "x64", ""));
        Assert.Equal(
            "BlenderLauncherNet/1.2.3 (Windows 10.0.26100; x64; UserID-CachingBot)",
            UserAgent.Build("1.2.3", "Windows 10.0.26100", "x64", "CachingBot"));
    }

    [Theory]
    [InlineData("short")] // under 8
    [InlineData("has space here")]
    [InlineData("abcdefgh\r\nX-Evil: 1")]
    [InlineData("abcdefgh\n")]
    [InlineData("ünïcödéxx")]
    public void Invalid_user_ids_are_left_out(string userId)
    {
        Assert.False(ConnectionOptions.IsValidUserId(userId));
        Assert.Equal("BlenderLauncherNet/1 (os; arch)", UserAgent.Build("1", "os", "arch", userId));
    }

    [Fact]
    public void User_agent_for_this_process_uses_the_app_version()
    {
        var ua = UserAgent.For("0f1e2d3c-aaaa-bbbb-cccc-1234567890ab");
        Assert.StartsWith($"BlenderLauncherNet/{AppIdentity.Version} (", ua, StringComparison.Ordinal);
        Assert.EndsWith("; UserID-0f1e2d3c-aaaa-bbbb-cccc-1234567890ab)", ua, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("(Windows 10.0.", ua, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Requests_carry_the_user_agent()
    {
        var handler = new RecordedResponseHandler().AddText("https://download.blender.org/release/", "x");
        using var client = handler.CreateClient(ConnectionOptions.Default with { UserId = "abcdefgh-1234" });
        using var _ = await client.GetAsync("https://download.blender.org/release/", TestContext.Current.CancellationToken);

        var ua = Assert.Single(handler.Requests).UserAgent;
        Assert.Equal(UserAgent.For("abcdefgh-1234"), ua);
    }

    [Theory]
    [InlineData(ProxyType.None, null)]
    [InlineData(ProxyType.Http, "http://proxy.local:8080/")]
    [InlineData(ProxyType.Https, "https://proxy.local:8080/")]
    [InlineData(ProxyType.Socks4, "socks4a://proxy.local:8080/")]
    [InlineData(ProxyType.Socks5, "socks5://proxy.local:8080/")]
    public void Proxy_types_map_to_uris(ProxyType type, string? expected)
    {
        var options = new ConnectionOptions { ProxyType = type, ProxyHost = "proxy.local", ProxyPort = 8080 };
        Assert.Equal(expected, options.ProxyUri?.AbsoluteUri);
        Assert.Null(options.Validate());
    }

    [Fact]
    public void Proxy_type_values_match_upstream()
    {
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<ProxyType>().Select(t => (int)t));
    }

    [Theory]
    [InlineData("::1", "socks5://[::1]:1080/")]
    [InlineData("[fe80::1]", "socks5://[fe80::1]:1080/")]
    [InlineData(" 10.0.0.1 ", "socks5://10.0.0.1:1080/")]
    public void Proxy_hosts_are_normalised(string host, string expected)
    {
        var options = new ConnectionOptions { ProxyType = ProxyType.Socks5, ProxyHost = host, ProxyPort = 1080 };
        Assert.Equal(expected, options.ProxyUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("", 8080, "empty")]
    [InlineData("proxy", 0, "outside")]
    [InlineData("bad host/x", 8080, "not a valid")]
    public void Invalid_proxy_options_are_rejected(string host, int port, string message)
    {
        var options = new ConnectionOptions { ProxyType = ProxyType.Http, ProxyHost = host, ProxyPort = port };
        Assert.Contains(message, options.Validate(), StringComparison.Ordinal);
        var e = Assert.Throws<ArgumentException>(() => new HttpClientProvider(options));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_proxy_options_are_valid_whatever_the_host()
    {
        Assert.Null(new ConnectionOptions { ProxyHost = "", ProxyPort = 0 }.Validate());
        Assert.Null(ConnectionOptions.Default.ProxyUri);
    }

    [Fact]
    public void Upstream_proxy_defaults_are_kept()
    {
        Assert.Equal("255.255.255.255", ConnectionOptions.Default.ProxyHost);
        Assert.Equal(9999, ConnectionOptions.Default.ProxyPort);
        Assert.True(ConnectionOptions.Default.UseCustomTlsCertificates);
    }

    [Fact]
    public void Primary_handler_settings()
    {
        using var handler = HttpClientProvider.CreatePrimaryHandler(ConnectionOptions.Default);
        Assert.Equal(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
        Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy); // .NET's default (system) proxy
        Assert.Equal(TimeSpan.FromMinutes(10), handler.PooledConnectionLifetime);
    }

    [Fact]
    public void Primary_handler_proxy_with_credentials()
    {
        var options = new ConnectionOptions
        {
            ProxyType = ProxyType.Socks5, ProxyHost = "proxy.local", ProxyPort = 1080, ProxyUser = "me", ProxyPassword = "pw",
        };
        using var handler = HttpClientProvider.CreatePrimaryHandler(options);
        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal("socks5://proxy.local:1080/", proxy.Address!.AbsoluteUri);
        var credential = proxy.Credentials!.GetCredential(proxy.Address, "Basic");
        Assert.Equal("me", credential!.UserName);
        Assert.Equal("pw", credential.Password);
        Assert.False(proxy.BypassProxyOnLocal);
    }

    [Fact]
    public void Primary_handler_proxy_without_user_has_no_credentials()
    {
        var options = new ConnectionOptions { ProxyType = ProxyType.Http, ProxyHost = "proxy.local", ProxyPort = 3128, ProxyPassword = "ignored" };
        using var handler = HttpClientProvider.CreatePrimaryHandler(options);
        Assert.Null(Assert.IsType<WebProxy>(handler.Proxy).Credentials);
    }

    [Fact]
    public void Client_has_no_overall_timeout()
    {
        using var provider = new HttpClientProvider(ConnectionOptions.Default);
        Assert.Equal(Timeout.InfiniteTimeSpan, provider.Client.Timeout);
    }

    [Fact]
    public async Task Rebuild_swaps_the_client_and_keeps_the_old_one_usable()
    {
        var built = new List<RecordedResponseHandler>();
        using var provider = new HttpClientProvider(ConnectionOptions.Default, null, _ =>
        {
            var h = new RecordedResponseHandler().AddText("https://example.org/", "ok");
            built.Add(h);
            return h;
        });
        var first = provider.Client;

        provider.Rebuild(ConnectionOptions.Default); // equal options: nothing happens
        Assert.Same(first, provider.Client);
        Assert.Single(built);

        var next = ConnectionOptions.Default with { GitHubToken = "ghp_000000000000000000000000000000000000" };
        provider.Rebuild(next);
        Assert.NotSame(first, provider.Client);
        Assert.Equal(next, provider.Options);
        Assert.Equal(2, built.Count);

        // A request on the old client (as one already in flight would be) still completes.
        using var response = await first.GetAsync("https://example.org/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Rebuild_with_invalid_options_keeps_the_current_client()
    {
        using var provider = new HttpClientProvider(ConnectionOptions.Default, null, _ => new RecordedResponseHandler());
        var client = provider.Client;
        Assert.Throws<ArgumentException>(() => provider.Rebuild(new ConnectionOptions { ProxyType = ProxyType.Http, ProxyHost = "" }));
        Assert.Same(client, provider.Client);
        Assert.Equal(ConnectionOptions.Default, provider.Options);
    }

    [Fact]
    public void Disposed_provider_throws()
    {
        var provider = new HttpClientProvider(ConnectionOptions.Default);
        provider.Dispose();
        provider.Dispose();
        Assert.Throws<ObjectDisposedException>(() => provider.Client);
    }

    [Fact]
    public void Options_text_masks_secrets()
    {
        var options = new ConnectionOptions
        {
            ProxyType = ProxyType.Http, ProxyHost = "h", ProxyPort = 1, ProxyUser = "user1", ProxyPassword = "hunter2",
            GitHubToken = "ghp_SECRETSECRETSECRETSECRETSECRETSECRET",
        };
        var text = options.ToString();
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
        Assert.DoesNotContain("user1", text, StringComparison.Ordinal);
        Assert.Contains("GitHubToken = (set)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Per_call_timeouts_cancel_and_are_told_apart_from_the_caller()
    {
        using var caller = new CancellationTokenSource();
        using var cts = NetTimeouts.Linked(caller.Token, TimeSpan.FromMilliseconds(20));
        var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(Timeout.Infinite, cts.Token));
        Assert.True(NetTimeouts.IsTimeout(e, caller.Token));

        caller.Cancel();
        Assert.False(NetTimeouts.IsTimeout(e, caller.Token));
    }
}
