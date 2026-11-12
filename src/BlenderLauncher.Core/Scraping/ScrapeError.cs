using System.Globalization;
using System.Net;
using System.Text.Json;
using BlenderLauncher.Core.Net;

namespace BlenderLauncher.Core.Scraping;

/// <summary>
/// Translation keys for scrape errors. The first four are upstream's (<c>msg.en.yml</c>); the <c>ui.err.scrape.*</c>
/// keys are new and live in <c>ui.en.yml</c>.
/// </summary>
public static class ScrapeMessageKeys
{
    /// <summary>"blender.org is temporarily blocking automated requests (Cloudflare challenge) …". No placeholders.</summary>
    public const string CloudflareBlocked = "msg.err.stable.cloudflare_blocked";

    /// <summary>"No releases were scraped from the Blender Foundation server …". No placeholders.</summary>
    public const string NoStableReleases = "msg.err.stable.no_releases";

    /// <summary>"Error: connection failed at %{time}" (upstream's status-bar text after a failed check).</summary>
    public const string ConnectionFailed = "msg.err.connection_failed";

    /// <summary>"An error has occurred: %{err} …".</summary>
    public const string Generic = "msg.err.generic";

    /// <summary>New: GitHub refused for its rate limit. Placeholder <c>%{source}</c>. Upstream only logged this (<c>upbge.py:69-70</c>).</summary>
    public const string GitHubRateLimited = "ui.err.scrape.github_rate_limited";

    /// <summary>New: a source answered with an unexpected HTTP status. Placeholders <c>%{source}</c>, <c>%{status}</c>.</summary>
    public const string HttpStatus = "ui.err.scrape.http_status";
}

/// <summary>
/// Why a source failed, ready for the UI: the source, a translation key with its placeholder values, and the HTTP
/// status when there was one. <see cref="Detail"/> is for the log only (never shown, never translated).
/// </summary>
public sealed record ScrapeError
{
    public ScrapeError(string sourceId, string messageKey, HttpStatusCode? status = null, string? detail = null,
        IReadOnlyList<KeyValuePair<string, string>>? messageArgs = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(messageKey);
        SourceId = sourceId;
        MessageKey = messageKey;
        Status = status;
        Detail = detail;
        MessageArgs = messageArgs ?? [];
    }

    public string SourceId { get; }

    /// <summary>One of <see cref="ScrapeMessageKeys"/>.</summary>
    public string MessageKey { get; }

    /// <summary>Placeholder values for <see cref="MessageKey"/>, by name (for example <c>time</c>).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> MessageArgs { get; }

    public HttpStatusCode? Status { get; }

    public string? Detail { get; }

    /// <summary>
    /// Maps any exception a source threw to an error (the scrape service calls this):
    /// <list type="bullet">
    /// <item><see cref="ScrapeSourceException"/>: its own error;</item>
    /// <item><see cref="HttpRequestException"/> with a status (from <c>EnsureSuccessStatusCode</c>): <see cref="ScrapeMessageKeys.HttpStatus"/>;</item>
    /// <item>other <see cref="HttpRequestException"/>, <see cref="IOException"/> and timeouts: <see cref="ScrapeMessageKeys.ConnectionFailed"/>
    /// with <c>time</c> as local <c>HH:mm</c>, as upstream's status bar;</item>
    /// <item>anything else (for example <see cref="JsonException"/>): <see cref="ScrapeMessageKeys.Generic"/>.</item>
    /// </list>
    /// Do not call it for a cancellation the caller asked for; that is not an error.
    /// </summary>
    public static ScrapeError FromException(string sourceId, Exception exception, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var detail = $"{exception.GetType().Name}: {exception.Message}";
        return exception switch
        {
            ScrapeSourceException s => s.Error,
            HttpRequestException { StatusCode: { } status } => HttpStatusError(sourceId, status, detail),
            HttpRequestException or IOException or OperationCanceledException => ConnectionFailed(sourceId, time, detail),
            _ => new ScrapeError(sourceId, ScrapeMessageKeys.Generic, null, detail, [new("err", exception.Message)]),
        };
    }

    /// <summary><see cref="ScrapeMessageKeys.ConnectionFailed"/> with <c>time</c> set to local <c>HH:mm</c> now.</summary>
    public static ScrapeError ConnectionFailed(string sourceId, TimeProvider? time = null, string? detail = null) =>
        new(sourceId, ScrapeMessageKeys.ConnectionFailed, null, detail,
            [new("time", (time ?? TimeProvider.System).GetLocalNow().ToString("HH:mm", CultureInfo.InvariantCulture))]);

    /// <summary><see cref="ScrapeMessageKeys.HttpStatus"/> for an unexpected status.</summary>
    public static ScrapeError HttpStatusError(string sourceId, HttpStatusCode status, string? detail = null) =>
        new(sourceId, ScrapeMessageKeys.HttpStatus, status, detail,
            [new("source", sourceId), new("status", ((int)status).ToString(CultureInfo.InvariantCulture))]);

    /// <summary><see cref="ScrapeMessageKeys.GitHubRateLimited"/>; the detail notes the reset time when known.</summary>
    public static ScrapeError GitHubRateLimited(string sourceId, HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var limit = GitHubRateLimit.From(response);
        var detail = limit.Reset is { } reset
            ? $"GitHub rate limit exhausted until {reset.ToString("u", CultureInfo.InvariantCulture)}"
            : "GitHub rate limit exceeded";
        return new ScrapeError(sourceId, ScrapeMessageKeys.GitHubRateLimited, response.StatusCode, detail, [new("source", sourceId)]);
    }
}

/// <summary>
/// Thrown by a source that cannot produce (the rest of) its builds, possibly after yielding some; see
/// <see cref="IBuildSource"/>. The message is the error's detail, for the log.
/// </summary>
public sealed class ScrapeSourceException : Exception
{
    public ScrapeSourceException(ScrapeError error, Exception? innerException = null)
        : base(error?.Detail ?? error?.MessageKey, innerException)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public ScrapeError Error { get; }
}
