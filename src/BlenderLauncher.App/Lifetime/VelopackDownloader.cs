using Velopack.Sources;

namespace BlenderLauncher.App.Lifetime;

/// <summary>
/// Velopack's downloads (the release feed, the GitHub release list and the packages) over the launcher's own
/// <see cref="HttpClient"/>, so the proxy, User-Agent and certificate settings from Settings apply to updates too.
/// <paramref name="http"/> is called per request: the provider swaps its client when the network settings change.
/// </summary>
internal sealed class VelopackDownloader(Func<HttpClient> http) : IFileDownloader
{
    private const int BufferSize = 81920;

    public async Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers,
        double timeout, CancellationToken cancelToken)
    {
        using var cts = Linked(cancelToken, timeout);
        using var request = Request(url, headers);
        using var response = await http().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        var source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var target = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            await using (target.ConfigureAwait(false))
            {
                var buffer = new byte[BufferSize];
                long done = 0;
                var reported = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                    done += read;
                    if (total is > 0 && (int)(done * 100 / total.Value) is var percent && percent != reported)
                    {
                        reported = percent;
                        progress(Math.Min(percent, 100));
                    }
                }
            }
        }

        progress(100);
    }

    public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout)
    {
        using var cts = Linked(CancellationToken.None, timeout);
        using var request = Request(url, headers);
        using var response = await http().SendAsync(request, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
    }

    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout)
    {
        using var cts = Linked(CancellationToken.None, timeout);
        using var request = Request(url, headers);
        using var response = await http().SendAsync(request, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    private static HttpRequestMessage Request(string url, IDictionary<string, string>? headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

    /// <summary>Velopack gives its timeout in minutes; zero or less means none.</summary>
    private static CancellationTokenSource Linked(CancellationToken ct, double timeoutMinutes)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMinutes > 0)
        {
            cts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
        }

        return cts;
    }
}
