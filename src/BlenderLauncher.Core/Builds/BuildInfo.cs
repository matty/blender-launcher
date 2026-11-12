using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Builds;

/// <summary>
/// One Blender build, installed or downloadable: a port of upstream's <c>BuildInfo</c>
/// (<c>modules/build_info.py:164-392</c>). Immutable; use <c>with</c> to change a field.
/// </summary>
/// <remarks>
/// Record equality (<c>==</c>) compares every field. Upstream's looser <c>__eq__</c>, used to match a download
/// to an installed build, is <see cref="Matches"/> and <see cref="MatchComparer"/>.
/// LTS promotion is not done in the constructor as upstream does with a list frozen at import; the caller applies
/// <see cref="PromoteLts"/> with the current <see cref="ILtsLookup"/> when it creates or re-reads a build.
/// </remarks>
public sealed record BuildInfo : IComparable<BuildInfo>
{
    /// <summary>Upstream's <c>BuildInfo.file_version</c>, written to every <c>.blinfo</c>.</summary>
    public const string FileVersion = "1.5";

    private static readonly SemVer OldVersionCutoff = new(2, 83, 0);

    /// <summary>A download URL, or the build folder's path for an installed build.</summary>
    public required string Link { get; init; }

    /// <summary>The version text as the source gave it, for example <c>4.3.0-alpha</c> or <c>2.79.0-b</c>.</summary>
    public required string Subversion
    {
        get;
        init
        {
            field = value;
            SemVersion = BlenderVersionParser.TryParse(value, out var v) ? v : null;
        }
    }

    /// <summary>The commit hash, or null (stable builds have none). An empty hash counts as none.</summary>
    public string? BuildHash { get; init; }

    /// <summary>The commit or release time; naive or aware exactly as read.</summary>
    public required IsoTimestamp CommitTime { get; init; }

    /// <summary>Upstream's branch string: <c>stable</c>, <c>lts</c>, <c>daily</c>, <c>PR12345</c>, a custom name and so on.</summary>
    public required string Branch { get; init; }

    /// <summary>A user-given name; empty when none.</summary>
    public string CustomName { get; init; } = "";

    public bool IsFavorite { get; init; }

    /// <summary>Executable path relative to the build folder. Null or empty when auto-detected.</summary>
    public string? CustomExecutable { get; init; }

    public bool IsFrozen { get; init; }

    /// <summary>
    /// <see cref="Subversion"/> parsed by <see cref="BlenderVersionParser"/> (upstream's <c>semversion</c>), or
    /// null when it does not parse, where upstream would raise.
    /// </summary>
    public SemVer? SemVersion { get; private init; }

    /// <summary>Upstream's <c>is_valid</c>: a parsable subversion and a non-empty branch.</summary>
    public bool IsValid => Subversion.Length > 0 && SemVersion is not null && Branch.Length > 0;

    /// <summary>A Bforartists dev build: branch <c>bforartists</c> and a subversion ending in <c>-dev</c>.</summary>
    public bool IsBfaDev => Branch == "bforartists" && Subversion.EndsWith("-dev", StringComparison.Ordinal);

    /// <summary>
    /// Upstream's <c>full_semversion</c>: the version with
    /// <c>"{pre}+{branch}.{hash}"</c> (or <c>"{branch}.{hash}"</c> without a prerelease) as its prerelease, for
    /// example <c>4.3.0-alpha+daily.ddc9f92777cd</c> or <c>4.1.0-stable</c>. Used for folder names and download
    /// de-duplication, so its text must not change. Null when the subversion does not parse.
    /// </summary>
    public SemVer? FullSemVersion => SemVersion is { } v ? FullSemVersionOf(v, Branch, BuildHash) : null;

    /// <summary>
    /// Upstream's <c>display_version</c>: <c>"Dev"</c> for a Bforartists dev build with major 0; <c>M.m</c> plus
    /// the prerelease for versions below 2.83.0; otherwise <c>M.m.p</c>. The subversion as is when it does not parse.
    /// </summary>
    public string DisplayVersion => SemVersion is not { } v ? Subversion
        : IsBfaDev && v.Major == 0 ? "Dev"
        : DisplayVersionOf(v);

    /// <summary>
    /// Upstream's <c>display_label</c>: the custom name, <c>"Bforartists Dev"</c>, or a label from the branch and
    /// prerelease (see <see cref="DisplayLabelOf"/>).
    /// </summary>
    public string DisplayLabel => CustomName.Length > 0 ? CustomName
        : IsBfaDev ? "Bforartists Dev"
        : SemVersion is { } v ? DisplayLabelOf(Branch, v, Subversion, OperatingSystem.IsMacOS())
        : PythonText.Title(Branch);

    /// <summary>
    /// Upstream's LTS promotion: a <c>stable</c> build whose series is LTS in <paramref name="lts"/> becomes
    /// <c>lts</c>. The series is matched on parsed major and minor, so 4.2 does not cover 4.20 (upstream matched
    /// the subversion text with <c>startswith("4.2")</c>, which did). Returns this instance when nothing changes.
    /// </summary>
    public BuildInfo PromoteLts(ILtsLookup lts) =>
        Branch == "stable" && SemVersion is { } v && lts.IsLts(v.Major, v.Minor) ? this with { Branch = "lts" } : this;

    /// <summary>
    /// Upstream's <c>__eq__</c> (de-duplication), in order: Bforartists dev builds match only each
    /// other, by commit time; two builds with hashes match on hash and branch; otherwise on major, minor and patch
    /// (prerelease ignored) and branch; and if either subversion does not parse, on subversion and branch.
    /// Not transitive (a build without a hash can match two builds with different hashes).
    /// </summary>
    public bool Matches(BuildInfo? other)
    {
        if (other is null)
        {
            return false;
        }

        if (IsBfaDev || other.IsBfaDev)
        {
            return IsBfaDev && other.IsBfaDev && CommitTime == other.CommitTime;
        }

        if (!string.IsNullOrEmpty(BuildHash) && !string.IsNullOrEmpty(other.BuildHash))
        {
            return BuildHash == other.BuildHash && Branch == other.Branch;
        }

        if (SemVersion is { } a && other.SemVersion is { } b)
        {
            return a.Major == b.Major && a.Minor == b.Minor && a.Patch == b.Patch && Branch == other.Branch;
        }

        return Subversion == other.Subversion && Branch == other.Branch;
    }

    /// <summary>
    /// <see cref="Matches"/> as an <see cref="IEqualityComparer{T}"/>. Its hash code is consistent with it
    /// (branch, plus commit time for Bforartists dev builds), so it works for lookups; because the rule is not
    /// transitive, a set built with it keeps whichever of several overlapping builds came first.
    /// </summary>
    public static IEqualityComparer<BuildInfo> MatchComparer { get; } = new UpstreamMatchComparer();

    /// <summary>
    /// Upstream's <c>__lt__</c> as a total order: by finalised version; if equal, by commit time; if the commit
    /// times cannot be compared (naive against aware), by <see cref="FullSemVersion"/>. Builds whose subversion
    /// does not parse (upstream would raise) sort below all others, by subversion text.
    /// </summary>
    public int CompareTo(BuildInfo? other)
    {
        if (other is null)
        {
            return 1;
        }

        if (SemVersion is not { } sv || other.SemVersion is not { } osv)
        {
            return SemVersion is not null ? 1
                : other.SemVersion is not null ? -1
                : Math.Sign(string.CompareOrdinal(Subversion, other.Subversion));
        }

        var c = sv.Finalize().CompareTo(osv.Finalize());
        if (c != 0)
        {
            return c;
        }

        return CommitTime.TryCompareTo(other.CommitTime, out var byTime)
            ? byTime
            : FullSemVersion!.Value.CompareTo(other.FullSemVersion!.Value);
    }

    /// <summary>Upstream's <c>get_semver(subversion, branch, build_hash)</c> applied to a parsed version.</summary>
    public static SemVer FullSemVersionOf(SemVer version, string branch, string? buildHash)
    {
        var prerelease = string.IsNullOrEmpty(version.Prerelease) ? "" : version.Prerelease + "+";
        prerelease += string.Join('.', new[] { branch, buildHash }.Where(s => !string.IsNullOrEmpty(s)));
        return version.WithPrerelease(prerelease);
    }

    /// <summary>Upstream's <c>_display_version</c>.</summary>
    public static string DisplayVersionOf(SemVer version) =>
        version < OldVersionCutoff
            ? $"{version.Major}.{version.Minor}{version.Prerelease}"
            : version.Finalize().ToString();

    /// <summary>
    /// Upstream's <c>_display_label</c>: <c>lts</c> gives <c>"LTS"</c>; <c>patch</c>, <c>experimental</c> and
    /// <c>daily</c> give the title-cased prerelease (dashes as spaces), or the title-cased text after the
    /// subversion's first dash; <c>upbge-x</c> gives <c>"UPBGE X"</c>; a prerelease starting <c>rc</c> gives
    /// <c>"Release Candidate …"</c>; on macOS a stable prerelease gives <c>"Stable - …"</c>; otherwise the
    /// title-cased branch.
    /// </summary>
    public static string DisplayLabelOf(string branch, SemVer version, string subversion, bool macOs)
    {
        if (branch == "lts")
        {
            return "LTS";
        }

        if (branch is "patch" or "experimental" or "daily")
        {
            if (version.Prerelease is { } b)
            {
                return PythonText.Title(b.Replace('-', ' '));
            }

            var dash = subversion.IndexOf('-', StringComparison.Ordinal);
            return PythonText.Title(dash < 0 ? subversion : subversion[(dash + 1)..]);
        }

        if (branch.StartsWith("upbge", StringComparison.Ordinal))
        {
            var parts = branch.Split('-');
            return parts.Length == 2 ? $"UPBGE {PythonText.Title(parts[1])}" : "UPBGE";
        }

        if (version.Prerelease is { } pre)
        {
            if (pre.StartsWith("rc", StringComparison.Ordinal))
            {
                return $"Release Candidate {pre[2..]}";
            }

            if (macOs && branch == "stable")
            {
                if (pre.StartsWith("macos-", StringComparison.Ordinal))
                {
                    pre = pre["macos-".Length..];
                }

                return $"{PythonText.Title(branch)} - {pre}";
            }
        }

        return PythonText.Title(branch);
    }

    private sealed class UpstreamMatchComparer : IEqualityComparer<BuildInfo>
    {
        public bool Equals(BuildInfo? x, BuildInfo? y) => x is null ? y is null : x.Matches(y);

        public int GetHashCode(BuildInfo obj) =>
            obj.IsBfaDev ? HashCode.Combine(obj.Branch, true, obj.CommitTime) : HashCode.Combine(obj.Branch, false);
    }
}
