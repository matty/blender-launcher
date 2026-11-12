using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using BlenderLauncher.Core.Net;

namespace BlenderLauncher.Core.Scraping.Upbge;

/// <summary>The UPBGE release list and the tag-to-commit map from one fetch.</summary>
/// <param name="Releases">The first page of releases, newest first, as GitHub lists them.</param>
/// <param name="TagCommits">Tag name to the first 12 characters of its commit hash; empty when the tags could not be read.</param>
public sealed record UpbgeReleaseSnapshot(IReadOnlyList<GitHubRelease> Releases, IReadOnlyDictionary<string, string> TagCommits);

/// <summary>
/// Fetches UPBGE's GitHub releases and tags for the two UPBGE sources. One page of 100 of each,
/// as upstream (no pagination). Sources that share a feed (<see cref="UpbgeBuildSource.CreatePair"/>) share one
/// fetch per check: a snapshot younger than <see cref="ReuseWindow"/> is reused, which halves the calls against
/// GitHub's 60-an-hour anonymous limit.
/// </summary>
/// <remarks>
/// Upstream fetched the tags once per scraper object, which lives for the whole session, so weekly builds released
/// after start-up never got a hash. The feed fetches them with every release list. A failed tag fetch is logged and
/// leaves the map empty, as upstream; a failed release fetch throws (rate limit: <see cref="ScrapeError.GitHubRateLimited"/>).
/// </remarks>
public sealed class UpbgeReleaseFeed
{
    public static readonly Uri ReleasesUrl = new("https://api.github.com/repos/UPBGE/upbge/releases?per_page=100");

    public static readonly Uri TagsUrl = new("https://api.github.com/repos/UPBGE/upbge/tags?per_page=100");

    /// <summary>How long a fetched snapshot is reused by the next caller.</summary>
    public static readonly TimeSpan ReuseWindow = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, UpbgeReleaseSnapshot Snapshot)? _last;

    /// <summary>
    /// The current snapshot, fetched unless a recent one exists. Errors are reported against <paramref name="sourceId"/>
    /// and are not cached, so the next caller tries again.
    /// </summary>
    public async Task<UpbgeReleaseSnapshot> GetAsync(ScrapeContext context, string sourceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = context.Time.GetUtcNow();
            if (_last is { } last && now - last.At < ReuseWindow && now >= last.At)
            {
                return last.Snapshot;
            }

            var releases = await FetchReleasesAsync(context, sourceId, ct).ConfigureAwait(false);
            var tags = await FetchTagCommitsAsync(context, ct).ConfigureAwait(false);
            var snapshot = new UpbgeReleaseSnapshot(releases, tags);
            _last = (context.Time.GetUtcNow(), snapshot);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<List<GitHubRelease>> FetchReleasesAsync(ScrapeContext context, string sourceId, CancellationToken ct)
    {
        using var cts = NetTimeouts.Linked(ct, NetTimeouts.Request);
        using var request = NewRequest(ReleasesUrl);
        using var response = await context.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (GitHubRateLimit.IsRateLimited(response, body))
        {
            var error = ScrapeError.GitHubRateLimited(sourceId, response);
            context.Log?.Warning($"GitHub API rate limit exceeded. UPBGE builds will not be available. {error.Detail}");
            throw new ScrapeSourceException(error);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            context.Log?.Error($"GitHub API error for UPBGE: HTTP {status.ToString(CultureInfo.InvariantCulture)}");
            throw new ScrapeSourceException(ScrapeError.HttpStatusError(sourceId, response.StatusCode, $"{ReleasesUrl} answered {status}"));
        }

        return JsonSerializer.Deserialize(body, UpbgeJsonContext.Default.ListGitHubRelease)
               ?? throw new JsonException("The UPBGE release list is the JSON literal null.");
    }

    private static async Task<Dictionary<string, string>> FetchTagCommitsAsync(ScrapeContext context, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var cts = NetTimeouts.Linked(ct, NetTimeouts.Request);
            using var request = NewRequest(TagsUrl);
            using var response = await context.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                context.Log?.Warning($"GitHub API error while fetching UPBGE tags: HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}");
                return map;
            }

            foreach (var tag in JsonSerializer.Deserialize(body, UpbgeJsonContext.Default.ListGitHubTag) ?? [])
            {
                if (!string.IsNullOrEmpty(tag.Name) && tag.Commit?.Sha is { Length: > 0 } sha)
                {
                    map[tag.Name] = sha.Length > 12 ? sha[..12] : sha;
                }
            }

            context.Log?.Debug($"Fetched {map.Count.ToString(CultureInfo.InvariantCulture)} UPBGE tag commits");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException || NetTimeouts.IsTimeout(e, ct))
        {
            context.Log?.Debug($"Failed to fetch UPBGE tag commits: {e.Message}");
        }

        return map;
    }

    private static HttpRequestMessage NewRequest(Uri url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return request;
    }
}
