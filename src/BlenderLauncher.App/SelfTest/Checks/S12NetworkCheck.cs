using System.Diagnostics;
using System.Net;
using BlenderLauncher.Core.Net;

namespace BlenderLauncher.App.SelfTest.Checks;

/// <summary>
/// Spike S12: an HTTPS GET of the stable release index returns 200 with a real listing, proving TLS and the
/// launcher's <see cref="HttpClientProvider"/> pipeline (redirect, token and logging handlers over
/// <see cref="SocketsHttpHandler"/>) in the published exe. The harness skips it under <c>--offline</c>; it also
/// skips itself if run directly with <see cref="SelfTestContext.Offline"/> set.
/// </summary>
internal sealed class S12NetworkCheck : ISelfTestCheck
{
    private static readonly Uri ReleaseIndex = new("https://download.blender.org/release/");

    public string Id => "S12";
    public string Name => "HTTPS GET of download.blender.org";
    public bool NeedsUiThread => false;
    public bool NeedsNetwork => true;

    public async Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct)
    {
        if (ctx.Offline)
        {
            return SelfTestResult.Skip("offline");
        }

        using var provider = new HttpClientProvider(ConnectionOptions.Default);
        using var cts = NetTimeouts.Linked(ct, NetTimeouts.Request);

        var watch = Stopwatch.StartNew();
        using var response = await provider.Client.GetAsync(ReleaseIndex, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        watch.Stop();

        var detail = $"{(int)response.StatusCode} HTTP/{response.Version} {body.Length} chars in {watch.ElapsedMilliseconds} ms";
        ctx.Log(detail);
        ctx.Log("User-Agent: " + UserAgent.For(null));
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return SelfTestResult.Fail(detail);
        }

        if (CloudflareChallenge.IsChallenge(body))
        {
            return SelfTestResult.Fail("Cloudflare challenge page; " + detail);
        }

        return body.Contains("href=\"Blender4.1/\"", StringComparison.Ordinal)
            ? SelfTestResult.Pass(detail)
            : SelfTestResult.Fail("200 but no Blender4.1/ link; " + detail);
    }
}
