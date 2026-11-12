using System.Diagnostics.CodeAnalysis;
using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.Core.Versioning;

/// <summary>
/// Upstream's <c>BasicBuildInfo</c> (<c>version_matcher.py:22-62</c>): the fields a
/// <see cref="VersionSearchQuery"/> matches on. Equality compares every field (the version ignoring build
/// metadata, the commit time as an instant), as upstream's frozen dataclass does.
/// </summary>
/// <param name="Version">The full SemVer (<see cref="BuildInfo.FullSemVersion"/>).</param>
/// <param name="Branch">Upstream's branch string.</param>
/// <param name="BuildHash">The build hash; empty when the build has none.</param>
/// <param name="CommitTime">The commit time in UTC.</param>
/// <param name="Folder">The build folder, or null.</param>
/// <param name="CustomName">The custom name, or null.</param>
public sealed record VersionSearchCandidate(
    SemVer Version,
    string Branch,
    string BuildHash,
    DateTimeOffset CommitTime,
    string? Folder = null,
    string? CustomName = null) : IComparable<VersionSearchCandidate>
{
    public int Major => Version.Major;

    public int Minor => Version.Minor;

    public int Patch => Version.Patch;

    /// <summary>Upstream's <c>fuzzy_text</c>, the haystack free-text search runs over.</summary>
    public string Haystack =>
        FuzzyMatcher.BuildHaystack(Version.ToString(), Branch, CustomName, BuildHash, CommitTime, Folder);

    /// <summary>
    /// Upstream's <c>BasicBuildInfo.from_buildinfo(build, folder)</c>: the full SemVer, an empty hash for none and
    /// the commit time converted to UTC (a naive time is read as local time in <paramref name="localZone"/>, the
    /// machine's zone when null, as Python's <c>astimezone</c> does). Throws <see cref="ArgumentException"/> when
    /// the build's subversion does not parse, where upstream would raise.
    /// </summary>
    public static VersionSearchCandidate FromBuildInfo(BuildInfo build, string? folder = null, TimeZoneInfo? localZone = null) =>
        TryFromBuildInfo(build, out var candidate, folder, localZone)
            ? candidate
            : throw new ArgumentException($"The build's version '{build.Subversion}' does not parse.", nameof(build));

    /// <summary>Non-throwing <see cref="FromBuildInfo"/>: false when the build's subversion does not parse.</summary>
    public static bool TryFromBuildInfo(
        BuildInfo build,
        [NotNullWhen(true)] out VersionSearchCandidate? candidate,
        string? folder = null,
        TimeZoneInfo? localZone = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (build.FullSemVersion is not { } version)
        {
            candidate = null;
            return false;
        }

        candidate = new VersionSearchCandidate(
            version,
            build.Branch,
            build.BuildHash ?? "",
            build.CommitTime.ToDateTimeOffset(localZone).ToUniversalTime(),
            folder,
            build.CustomName);
        return true;
    }

    /// <summary>Upstream's <c>__lt__</c>: by version, then by commit time when the versions are equal.</summary>
    public int CompareTo(VersionSearchCandidate? other)
    {
        if (other is null)
        {
            return 1;
        }

        var byVersion = Version.CompareTo(other.Version);
        return byVersion != 0 ? byVersion : CommitTime.CompareTo(other.CommitTime);
    }
}
