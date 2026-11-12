using System.Runtime.CompilerServices;
using System.Text.Json;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Scraping.PullRequests;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Scraping.Builder;

/// <summary>
/// Daily, experimental and patch builds from builder.blender.org's JSON listing (a port of upstream's
/// <c>ScraperAutomated</c> and <c>ScraperPatch</c>, <c>threads/scraping/automated.py</c>).
/// One class, three instances: <see cref="Daily"/>, <see cref="Experimental"/> and <see cref="Patch"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The archive toggle (<see cref="ScrapeContext.IncludesArchive"/>) scrapes <c>{branch}/archive</c> instead of
/// <c>{branch}</c>, as upstream's <c>scraper.py:147-163</c> does. The two listings do not overlap.</item>
/// <item>Entries are selected with <see cref="BuildPlatform.SelectBuilderEntries{T}"/> (platform, architecture equal
/// to <see cref="ScrapeContext.Machine"/>, file name matched from the start); when none match the machine, every
/// entry for the platform is taken and labelled with its architecture, and a warning is logged.</item>
/// <item>For the patch source with <see cref="ScrapeContext.FetchPrTitles"/>, pull-request titles fill
/// <see cref="AvailableBuild.Description"/>. A title lookup failure never fails the source.</item>
/// <item>An entry that cannot be mapped (no URL, unparsable version, no time) is logged and skipped. An HTTP error,
/// a timeout or a listing that is not a JSON array throws <see cref="ScrapeSourceException"/>.</item>
/// </list>
/// </remarks>
public sealed class BuilderBuildSource : IBuildSource
{
    private readonly Func<ScrapeContext, PullRequestTitleService>? _titles;

    private BuilderBuildSource(string id, string branch, Channel channel, Func<ScrapeContext, PullRequestTitleService>? titles)
    {
        Id = id;
        Branch = branch;
        Channel = channel;
        Channels = [channel];
        _titles = titles;
    }

    /// <summary>The daily source (<see cref="BuildSourceIds.Daily"/>).</summary>
    public static BuilderBuildSource Daily() => new(BuildSourceIds.Daily, "daily", Channel.Daily, null);

    /// <summary>The experimental-branch source (<see cref="BuildSourceIds.Experimental"/>).</summary>
    public static BuilderBuildSource Experimental() => new(BuildSourceIds.Experimental, "experimental", Channel.Experimental, null);

    /// <summary>
    /// The pull-request source (<see cref="BuildSourceIds.Patch"/>).
    /// </summary>
    /// <param name="titles">
    /// The title service to use when <see cref="ScrapeContext.FetchPrTitles"/> is on; pass the app's shared
    /// instance so "Fetch PR name" sees the same cache. Null creates one per scrape from the context
    /// (<see cref="ScrapeContext.Http"/>, <see cref="ScrapeContext.CacheDirectory"/>).
    /// </param>
    /// <remarks>Whether the number is prepended to a title comes from <see cref="ScrapeContext.PrependPrNumber"/>.</remarks>
    public static BuilderBuildSource Patch(PullRequestTitleService? titles = null) =>
        new(BuildSourceIds.Patch, "patch", Channel.Patch, titles is null ? null : _ => titles);

    public string Id { get; }

    /// <summary>The branch in the URL and in every build: <c>daily</c>, <c>experimental</c> or <c>patch</c>.</summary>
    public string Branch { get; }

    public Channel Channel { get; }

    public IReadOnlyList<Channel> Channels { get; }

    /// <summary>The listing URL: upstream's <c>https://builder.blender.org/download/{branch}/?format=json&amp;v=1</c>.</summary>
    public static string ListingUrl(string branchPath) => $"https://builder.blender.org/download/{branchPath}/?format=json&v=1";

    /// <summary>The URL this source fetches under <paramref name="context"/> (with <c>/archive</c> when its toggle is on).</summary>
    public string UrlFor(ScrapeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ListingUrl(context.IncludesArchive(Channel) ? Branch + "/archive" : Branch);
    }

    public async IAsyncEnumerable<AvailableBuild> ScrapeAsync(ScrapeContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var entries = await FetchAsync(context, cancellationToken).ConfigureAwait(false);
        var builds = Map(entries, context);

        if (Channel == Channel.Patch && context.FetchPrTitles)
        {
            builds = await AddTitlesAsync(builds, context, cancellationToken).ConfigureAwait(false);
        }

        foreach (var build in builds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return build.Available;
        }
    }

    private async Task<List<BuilderEntry>> FetchAsync(ScrapeContext context, CancellationToken cancellationToken)
    {
        var url = UrlFor(context);
        try
        {
            using var cts = NetTimeouts.Linked(cancellationToken, NetTimeouts.Request);
            using var response = await context.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ScrapeSourceException(ScrapeError.HttpStatusError(Id, response.StatusCode, $"GET {url} returned {(int)response.StatusCode}"));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(stream, BuilderJsonContext.Default.ListBuilderEntry, cts.Token).ConfigureAwait(false)
                   ?? throw new JsonException("The listing is the JSON literal null");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException ||
                                  (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            context.Log?.Error($"{Id}: failed to read {url}: {e.GetType().Name}: {e.Message}");
            throw new ScrapeSourceException(ScrapeError.FromException(Id, e, context.Time), e);
        }
    }

    private List<Mapped> Map(List<BuilderEntry> entries, ScrapeContext context)
    {
        var selected = BuildPlatform.SelectBuilderEntries(
            entries.Where(e => e is not null), context.Os, context.Machine, e => e.Platform, e => e.Architecture, e => e.FileName,
            out var architectureSpecific);

        if (!architectureSpecific)
        {
            context.Log?.Warning($"No builds found for {Branch} build on {BuildPlatform.BuilderPlatform(context.Os)} architecture {context.Machine}");
        }

        var builds = new List<Mapped>(selected.Count);
        foreach (var entry in selected)
        {
            if (ToBuild(entry, Branch, architectureSpecific, out var prerelease) is { } info)
            {
                builds.Add(new Mapped(new AvailableBuild(info.PromoteLts(context.Lts)) { SizeBytes = entry.FileSize }, prerelease));
            }
            else
            {
                context.Log?.Warning($"{Id}: skipped an entry that could not be read: {entry.FileName ?? entry.Url ?? "(no name)"}");
            }
        }

        return builds;
    }

    /// <summary>
    /// Upstream's <c>new_build_from_dict</c>: the prerelease is the entry's <c>patch</c> (not daily),
    /// <c>release_cycle</c> (daily) or <c>branch</c> (experimental), in that order of precedence, plus
    /// <c>" | {architecture}"</c> in the fallback pass. Null when the entry lacks a URL, a parsable version or a time.
    /// </summary>
    internal static BuildInfo? ToBuild(BuilderEntry entry, string branchType, bool architectureSpecific, out string? prerelease)
    {
        prerelease = null;
        if (string.IsNullOrEmpty(entry.Url) || entry.FileMtime is not { } mtime || double.IsNaN(mtime) || double.IsInfinity(mtime) ||
            !BlenderVersionParser.TryParse(entry.Version, out var version))
        {
            return null;
        }

        var buildVar = "";
        if (entry.Patch is not null && branchType != "daily")
        {
            buildVar = entry.Patch;
        }

        if (entry.ReleaseCycle is not null && branchType == "daily")
        {
            buildVar = entry.ReleaseCycle;
        }

        if (!string.IsNullOrEmpty(entry.Branch) && branchType == "experimental")
        {
            buildVar = entry.Branch;
        }

        if (!architectureSpecific && entry.Architecture is { } architecture)
        {
            buildVar += " | " + BuildPlatform.FallbackArchitectureLabel(architecture);
        }

        if (buildVar.Length > 0)
        {
            version = version.WithPrerelease(buildVar);
            prerelease = buildVar;
        }
        else
        {
            prerelease = version.Prerelease;
        }

        DateTimeOffset time;
        try
        {
            time = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(mtime * 1000, MidpointRounding.AwayFromZero));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        return new BuildInfo
        {
            Link = entry.Url,
            Subversion = version.ToString(),
            BuildHash = entry.Hash,
            CommitTime = IsoTimestamp.Aware(time),
            Branch = branchType,
        };
    }

    private async Task<List<Mapped>> AddTitlesAsync(List<Mapped> builds, ScrapeContext context, CancellationToken cancellationToken)
    {
        var numbers = builds.Select(b => PullRequestLabels.TryParseFromPrerelease(b.Prerelease, out var n) ? n : 0).ToArray();
        if (numbers.All(n => n == 0))
        {
            return builds;
        }

        IReadOnlyDictionary<int, string> titles;
        try
        {
            var service = _titles?.Invoke(context) ?? new PullRequestTitleService(context.Http, context.CacheDirectory, context.Log);
            titles = await service.GetTitlesAsync(numbers.Where(n => n > 0), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            context.Log?.Error($"{Id}: pull-request titles could not be fetched: {e.GetType().Name}: {e.Message}");
            return builds;
        }

        var prepend = context.PrependPrNumber;
        return builds.Select((b, i) => titles.TryGetValue(numbers[i], out var title)
            ? b with { Available = b.Available with { Description = PullRequestLabels.Format(numbers[i], title, prepend) } }
            : b).ToList();
    }

    /// <summary>A mapped build with the prerelease it was given (the PR number is read from this, not re-parsed).</summary>
    private sealed record Mapped(AvailableBuild Available, string? Prerelease);
}
