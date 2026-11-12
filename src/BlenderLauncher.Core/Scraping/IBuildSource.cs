using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.Core.Scraping;

/// <summary>
/// One place builds are scraped from (stable index, a builder.blender.org branch, Bforartists, UPBGE …). Every
/// scraper implements this; the scrape service runs the enabled sources concurrently.
/// </summary>
/// <remarks>
/// The contract:
/// <list type="bullet">
/// <item><see cref="ScrapeAsync"/> yields builds as it finds them. It does no de-duplication across sources and no
/// "new" marking (the service does both), and it does not apply <see cref="BuildPlatform.PassesPostFilter"/> (the
/// service does, once, for every source, as upstream's <c>scraper.py</c> did). It does apply its own
/// platform/architecture rules (file-name filter, builder JSON architecture match).</item>
/// <item>Stable builds are LTS-promoted with <see cref="ScrapeContext.Lts"/> before they are yielded.</item>
/// <item>All HTTP goes through <see cref="ScrapeContext.Http"/>, each call bounded with
/// <c>NetTimeouts.Linked(ct, NetTimeouts.Request)</c>.</item>
/// <item><b>Failure.</b> When the source cannot produce its list (or the rest of it), it throws
/// <see cref="ScrapeSourceException"/> carrying a <see cref="ScrapeError"/>, after having yielded whatever it
/// already had; the service keeps those builds and reports the error against <see cref="Id"/>. Any other exception
/// (<see cref="HttpRequestException"/>, a timeout's <see cref="OperationCanceledException"/>, malformed JSON) is
/// treated the same way through <see cref="ScrapeError.FromException"/>, so a source only needs to throw
/// <see cref="ScrapeSourceException"/> where it has a better message (Cloudflare, rate limit). Recoverable problems
/// with single entries (one unparsable asset) are logged and skipped, not thrown.</item>
/// <item><b>Cancellation.</b> When <c>cancellationToken</c> is cancelled the source stops and lets the
/// <see cref="OperationCanceledException"/> propagate; that is not an error.</item>
/// <item>A source keeps no per-run state between calls other than its caches, and may be called again after a
/// failure.</item>
/// </list>
/// </remarks>
public interface IBuildSource
{
    /// <summary>Stable identifier, used in logs, errors and settings; one of <see cref="BuildSourceIds"/>.</summary>
    string Id { get; }

    /// <summary>The channels this source's builds belong to (for example Stable and Lts), so the service knows which pages it fills.</summary>
    IReadOnlyList<Channel> Channels { get; }

    /// <summary>Scrapes the source. See the interface remarks for the rules.</summary>
    IAsyncEnumerable<AvailableBuild> ScrapeAsync(ScrapeContext context, CancellationToken cancellationToken);
}

/// <summary>The <see cref="IBuildSource.Id"/> values.</summary>
public static class BuildSourceIds
{
    public const string Stable = "stable";
    public const string Daily = "daily";
    public const string Experimental = "experimental";
    public const string Patch = "patch";
    public const string Bforartists = "bforartists";
    public const string UpbgeStable = "upbge-stable";
    public const string UpbgeWeekly = "upbge-weekly";
}
