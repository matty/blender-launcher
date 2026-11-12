using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Scraping.Upbge;

/// <summary>Which UPBGE releases a <see cref="UpbgeBuildSource"/> yields.</summary>
public enum UpbgeKind
{
    /// <summary>Tagged releases (<c>vM.m</c>) with no alpha assets, from 0.30 on: branch <c>upbge-stable</c>.</summary>
    Stable,

    /// <summary><c>weekly-build-N</c> releases with at least one alpha asset: branch <c>upbge-weekly</c>.</summary>
    Weekly,
}

/// <summary>
/// UPBGE builds from GitHub, a port of upstream's <c>ScraperUpbgeStable</c> and <c>ScraperUpbgeWeekly</c>
/// (<c>threads/scraping/upbge.py</c>). Ids <c>upbge-stable</c> and <c>upbge-weekly</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Releases: drafts and releases without a tag or <c>published_at</c> are skipped. A tag starting
/// <c>weekly-build-</c> is weekly. Stable takes non-weekly releases where no asset name contains <c>-alpha</c> and
/// whose tag (leading <c>v</c>s removed) parses to at least 0.30.0 (or does not parse); weekly takes weekly releases
/// where at least one asset name contains <c>-alpha</c>.</item>
/// <item>Assets: the name must match <see cref="AssetFilter"/> from its start, case-insensitively (Windows:
/// <c>upbge-…windows….zip</c> or <c>.7z</c>, both kept, as upstream). There is no architecture filter.</item>
/// <item>Version: stable from the tag; weekly from <c>upbge-M.m[-alpha]-</c> in the asset name, or else
/// <c>0.0.N-weekly</c> from the tag number.</item>
/// <item>Commit time is the release's <c>published_at</c>; the hash is the tag's commit (12 characters) when the
/// first page of tags has it; the size is the asset's <c>size</c>.</item>
/// </list>
/// Difference from upstream: when a release also has a <c>windows7</c> package, only the other Windows packages are
/// used (the launcher supports Windows 10 and later, and both would otherwise share one version).
/// </remarks>
public sealed partial class UpbgeBuildSource : IBuildSource
{
    /// <summary>Stable releases before this are skipped (upstream's <c>UPBGE_MINIMUM_VERSION</c>).</summary>
    public static readonly SemVer MinimumVersion = new(0, 30, 0);

    private const string WeeklyTagPrefix = "weekly-build-";

    private readonly UpbgeReleaseFeed _feed;

    /// <param name="kind">Stable or weekly releases.</param>
    /// <param name="feed">The release feed; pass one feed to both sources to share fetches. A private one when null.</param>
    public UpbgeBuildSource(UpbgeKind kind, UpbgeReleaseFeed? feed = null)
    {
        Kind = kind;
        _feed = feed ?? new UpbgeReleaseFeed();
        Channels = [kind == UpbgeKind.Stable ? Channel.UpbgeStable : Channel.UpbgeWeekly];
    }

    /// <summary>A stable and a weekly source sharing one <see cref="UpbgeReleaseFeed"/>.</summary>
    public static (UpbgeBuildSource Stable, UpbgeBuildSource Weekly) CreatePair()
    {
        var feed = new UpbgeReleaseFeed();
        return (new UpbgeBuildSource(UpbgeKind.Stable, feed), new UpbgeBuildSource(UpbgeKind.Weekly, feed));
    }

    public UpbgeKind Kind { get; }

    public string Id => Kind == UpbgeKind.Stable ? BuildSourceIds.UpbgeStable : BuildSourceIds.UpbgeWeekly;

    /// <summary>The branch of every build this source yields (<c>upbge-stable</c> or <c>upbge-weekly</c>, the same text as <see cref="Id"/>).</summary>
    public string Branch => Id;

    public IReadOnlyList<Channel> Channels { get; }

    public async IAsyncEnumerable<AvailableBuild> ScrapeAsync(ScrapeContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var snapshot = await _feed.GetAsync(context, Id, cancellationToken).ConfigureAwait(false);
        foreach (var build in SelectBuilds(snapshot, Kind, context.Os, context.Log))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return build;
        }
    }

    /// <summary>The builds of <paramref name="kind"/> in <paramref name="snapshot"/> for <paramref name="os"/>, in release order. Pure.</summary>
    public static IReadOnlyList<AvailableBuild> SelectBuilds(UpbgeReleaseSnapshot snapshot, UpbgeKind kind, HostOs os, Logging.Log? log = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var branch = kind == UpbgeKind.Stable ? BuildSourceIds.UpbgeStable : BuildSourceIds.UpbgeWeekly;
        var filter = AssetFilter(os);
        var builds = new List<AvailableBuild>();
        foreach (var release in snapshot.Releases)
        {
            var tag = release.TagName ?? "";
            var isWeekly = tag.StartsWith(WeeklyTagPrefix, StringComparison.Ordinal);
            if (!ShouldProcess(release, tag, isWeekly, kind, log))
            {
                continue;
            }

            if (!IsoTimestamp.TryParse(release.PublishedAt, out var commitTime))
            {
                log?.Warning($"Failed to parse UPBGE release date {release.PublishedAt}");
                continue;
            }

            var hash = snapshot.TagCommits.TryGetValue(tag, out var h) ? h : null;
            var assets = (release.Assets ?? [])
                .Where(a => !string.IsNullOrEmpty(a.BrowserDownloadUrl) && MatchesAtStart(filter, a.Name ?? ""))
                .ToList();
            if (assets.Any(a => !IsWindows7(a)))
            {
                assets.RemoveAll(IsWindows7);
            }

            foreach (var asset in assets)
            {
                if (ParseVersion(tag, asset.Name ?? "", isWeekly) is not { } version)
                {
                    log?.Warning($"Failed to parse UPBGE version {tag}");
                    continue;
                }

                var build = new BuildInfo
                {
                    Link = asset.BrowserDownloadUrl!,
                    Subversion = version.ToString(),
                    BuildHash = hash,
                    CommitTime = commitTime,
                    Branch = branch,
                    CustomExecutable = ExecutableFor(os),
                };
                builds.Add(new AvailableBuild(build) { SizeBytes = asset.Size });
            }
        }

        return builds;
    }

    /// <summary>
    /// Upstream's <c>_parse_version</c>. Weekly: <c>upbge-([0-9.]+(?:-alpha)?)-</c> searched in the asset name, with
    /// <c>-alpha</c> removed, through <see cref="BlenderVersionParser"/>; when the pattern is absent,
    /// <c>0.0.N-weekly</c> from the tag. Stable: the tag without leading <c>v</c>s. Null when that fails.
    /// </summary>
    public static SemVer? ParseVersion(string tagName, string assetName, bool isWeekly)
    {
        ArgumentNullException.ThrowIfNull(tagName);
        ArgumentNullException.ThrowIfNull(assetName);
        if (!isWeekly)
        {
            return BlenderVersionParser.TryParse(tagName.TrimStart('v'), out var stable) ? stable : null;
        }

        var match = WeeklyVersionRegex().Match(assetName);
        if (match.Success)
        {
            return BlenderVersionParser.TryParse(match.Groups[1].Value.Replace("-alpha", "", StringComparison.Ordinal), out var weekly)
                ? weekly
                : null;
        }

        // Python's int() accepts surrounding whitespace, a sign and underscores between digits; the tag numbers
        // GitHub carries are plain digits, so plain digits are all this accepts.
        var number = tagName.Replace(WeeklyTagPrefix, "", StringComparison.Ordinal);
        return number.Length > 0 && number.All(char.IsAsciiDigit) && int.TryParse(number, out var n)
            ? new SemVer(0, 0, n, "weekly")
            : null;
    }

    /// <summary>Upstream's <c>upbge_package_file_name_regex</c> for <paramref name="os"/>; used as Python's <c>re.match</c> (anchored at the start).</summary>
    public static Regex AssetFilter(HostOs os) => os switch
    {
        HostOs.Windows => WindowsAssetRegex(),
        HostOs.MacOS => MacAssetRegex(),
        _ => LinuxAssetRegex(),
    };

    /// <summary>Upstream's executable for UPBGE builds: <c>blender.exe</c>, <c>blender</c>, or the app bundle's binary on macOS.</summary>
    public static string ExecutableFor(HostOs os) => os switch
    {
        HostOs.Windows => "blender.exe",
        HostOs.MacOS => "Blender.app/Contents/MacOS/Blender",
        _ => "blender",
    };

    private static bool ShouldProcess(GitHubRelease release, string tag, bool isWeekly, UpbgeKind kind, Logging.Log? log)
    {
        if (release.Draft || tag.Length == 0 || string.IsNullOrEmpty(release.PublishedAt))
        {
            return false;
        }

        var hasAlpha = (release.Assets ?? []).Any(a => (a.Name ?? "").Contains("-alpha", StringComparison.OrdinalIgnoreCase));
        if (kind == UpbgeKind.Stable)
        {
            if (isWeekly || hasAlpha)
            {
                return false;
            }

            if (BlenderVersionParser.TryParse(tag.TrimStart('v'), out var version) && version < MinimumVersion)
            {
                log?.Debug($"Skipping old UPBGE release: {tag} (< {MinimumVersion})");
                return false;
            }

            return true;
        }

        return isWeekly && hasAlpha;
    }

    private static bool MatchesAtStart(Regex filter, string name)
    {
        var match = filter.Match(name);

        // The leftmost match starts at 0 whenever a match at 0 exists, so this equals Python's re.match.
        return match.Success && match.Index == 0;
    }

    private static bool IsWindows7(GitHubReleaseAsset asset) =>
        (asset.Name ?? "").Contains("windows7", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"upbge-.+windows.+\.(zip|7z)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsAssetRegex();

    [GeneratedRegex(@"upbge-.+macos.+\.(zip|dmg)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MacAssetRegex();

    [GeneratedRegex(@"upbge-.+linux.+\.(tar\.xz|tar\.gz)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinuxAssetRegex();

    [GeneratedRegex(@"upbge-([0-9.]+(?:-alpha)?)-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeeklyVersionRegex();
}
