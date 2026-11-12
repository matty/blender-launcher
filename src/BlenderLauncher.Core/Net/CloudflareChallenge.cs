namespace BlenderLauncher.Core.Net;

/// <summary>
/// Upstream's Cloudflare challenge check (<c>threads/scraping/stable.py:36-42</c>): a page
/// containing any of the markers is a challenge, not a listing. Upstream retries three times, 2 s apart.
/// </summary>
public static class CloudflareChallenge
{
    /// <summary>The markers, as upstream (case-sensitive).</summary>
    public static IReadOnlyList<string> Markers { get; } = ["Just a moment", "cf_chl_opt", "challenge-platform"];

    public const int Attempts = 3;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public static bool IsChallenge(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        foreach (var marker in Markers)
        {
            if (body.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
