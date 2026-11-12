using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Updates;

namespace BlenderLauncher.Core.Scraping;

/// <summary>
/// What one source produced in one check, published as soon as that source finishes
/// (<see cref="ScrapeService.SourceCompleted"/>). Immutable.
/// </summary>
/// <param name="SourceId">The source (<see cref="IBuildSource.Id"/>).</param>
/// <param name="Channels">The channels the source fills.</param>
/// <param name="Builds">
/// The builds in the order the source yielded them, after the Windows post-filter and de-duplication, with
/// <see cref="AvailableBuild.IsNew"/> set. When the source failed part-way, the builds it yielded before failing.
/// </param>
/// <param name="Error">Why the source failed, or null.</param>
/// <param name="Duration">How long the source took.</param>
/// <param name="FilteredOut">Builds dropped by the Windows post-filter.</param>
/// <param name="Duplicates">Builds dropped because an equal full version string was already listed.</param>
public sealed record ScrapeSourceResult(
    string SourceId,
    IReadOnlyList<Channel> Channels,
    IReadOnlyList<AvailableBuild> Builds,
    ScrapeError? Error,
    TimeSpan Duration,
    int FilteredOut,
    int Duplicates)
{
    public bool Succeeded => Error is null;
}

/// <summary>Raised when a check starts (<see cref="ScrapeService.CheckStarted"/>).</summary>
/// <param name="Forced">A forced check: every visible channel, not only the enabled ones.</param>
/// <param name="SourceIds">The sources that will run, in the service's order.</param>
/// <param name="StartedAt">When it started.</param>
public sealed record ScrapeCheckStarted(bool Forced, IReadOnlyList<string> SourceIds, DateTimeOffset StartedAt);

/// <summary>
/// The result of one complete check: the available-builds list and everything that went wrong. Immutable; the
/// service replaces <see cref="ScrapeService.Current"/> with a new one after each check.
/// </summary>
public sealed record ScrapeSnapshot
{
    /// <summary>Before the first check: no builds, no errors.</summary>
    public static ScrapeSnapshot Empty { get; } = new()
    {
        Sources = [],
        Builds = [],
        Errors = [],
        StartedAt = DateTimeOffset.MinValue,
        CompletedAt = DateTimeOffset.MinValue,
    };

    /// <summary>Per-source results in the service's source order (not completion order).</summary>
    public required IReadOnlyList<ScrapeSourceResult> Sources { get; init; }

    /// <summary>Every available build: the sources' builds concatenated in source order.</summary>
    public required IReadOnlyList<AvailableBuild> Builds { get; init; }

    /// <summary>
    /// Every error of the check: the API-data refresh's (source <see cref="ApiDataService.SourceId"/>), each failed
    /// source's, and the launcher release check's (source <see cref="AppReleaseService.SourceId"/>).
    /// </summary>
    public required IReadOnlyList<ScrapeError> Errors { get; init; }

    /// <summary>The API-data refresh, when one ran.</summary>
    public ApiRefreshResult? ApiRefresh { get; init; }

    /// <summary>The launcher release check, when one ran.</summary>
    public UpdateCheckResult? LauncherUpdate { get; init; }

    public bool Forced { get; init; }

    /// <summary>True for the first check after the service started, when nothing is marked new (upstream's <c>just_started</c>).</summary>
    public bool FirstCheck { get; init; }

    /// <summary>The last-check time builds were compared against to mark them new.</summary>
    public DateTimeOffset? ComparedWith { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset CompletedAt { get; init; }

    /// <summary>How many builds are marked new.</summary>
    public int NewCount => Builds.Count(b => b.IsNew);

    /// <summary>The builds of <paramref name="channel"/> (by branch; LTS builds belong to <see cref="Channel.Lts"/>).</summary>
    public IEnumerable<AvailableBuild> ForChannel(Channel channel)
    {
        var branch = Channels.BranchOf(channel);
        return Builds.Where(b => b.Build.Branch == branch);
    }
}
