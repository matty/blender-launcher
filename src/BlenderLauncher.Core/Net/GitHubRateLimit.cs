using System.Globalization;
using System.Net;

namespace BlenderLauncher.Core.Net;

/// <summary>
/// GitHub's rate-limit headers on a response (<c>X-RateLimit-Limit</c>, <c>-Remaining</c>, <c>-Reset</c> as epoch
/// seconds, <c>-Resource</c>). Any of them may be missing.
/// </summary>
public readonly record struct GitHubRateLimit(int? Limit, int? Remaining, DateTimeOffset? Reset, string? Resource)
{
    public static GitHubRateLimit From(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new GitHubRateLimit(
            ReadInt(response, "X-RateLimit-Limit"),
            ReadInt(response, "X-RateLimit-Remaining"),
            ReadInt64(response, "X-RateLimit-Reset") is { } s and >= 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(s) : null,
            Read(response, "X-RateLimit-Resource"));
    }

    /// <summary>
    /// True when the response is GitHub refusing for the rate limit: 403 or 429 with no requests remaining or a
    /// <c>Retry-After</c> header, or, as upstream checked (<c>upbge.py:66-73</c>), a body whose text contains
    /// "rate limit" (case-insensitive) on a 403 or 429.
    /// </summary>
    public static bool IsRateLimited(HttpResponseMessage response, string? body = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
        {
            return false;
        }

        return From(response).Remaining == 0 ||
               response.Headers.RetryAfter is not null ||
               (body is not null && body.Contains("rate limit", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Read(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;

    private static int? ReadInt(HttpResponseMessage response, string name) =>
        int.TryParse(Read(response, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static long? ReadInt64(HttpResponseMessage response, string name) =>
        long.TryParse(Read(response, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
