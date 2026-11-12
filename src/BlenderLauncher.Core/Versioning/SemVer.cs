using System.Globalization;
using System.Text.RegularExpressions;

namespace BlenderLauncher.Core.Versioning;

/// <summary>
/// A semantic version with python-semver 3's semantics, as upstream uses its <c>Version</c> type:
/// strict parsing, ordering by major, minor, patch then prerelease, equality and ordering that ignore build
/// metadata, and <see cref="ToString"/> identical to python-semver's <c>str()</c>.
/// </summary>
/// <remarks>
/// Like python-semver's constructor, the constructor does not validate the prerelease or build text, so
/// <c>new SemVer(2, 80, 0, "(sub 75)")</c> is allowed (upstream's Blender parser relies on this).
/// Deviations: numeric parts are limited to <see cref="int"/> (Python's are unbounded; a larger number fails to
/// parse), and only ASCII digits count as digits (Python's <c>\d</c> also takes other Unicode digits).
/// <c>default(SemVer)</c> is 0.0.0.
/// </remarks>
public readonly partial struct SemVer : IEquatable<SemVer>, IComparable<SemVer>, IComparable
{
    /// <summary>Creates a version. Throws <see cref="ArgumentOutOfRangeException"/> for a negative part.</summary>
    public SemVer(int major, int minor = 0, int patch = 0, string? prerelease = null, string? build = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Build = build;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The prerelease text without its leading <c>-</c>, or null. May be empty, which prints as absent.</summary>
    public string? Prerelease { get; }

    /// <summary>The build metadata without its leading <c>+</c>, or null. Ignored by equality and ordering.</summary>
    public string? Build { get; }

    /// <summary>
    /// python-semver's <c>Version.parse</c>. With <paramref name="optionalMinorAndPatch"/>, <c>"5.2"</c> and
    /// <c>"5"</c> are accepted and the missing parts are 0. Throws <see cref="FormatException"/> when invalid.
    /// </summary>
    public static SemVer Parse(string text, bool optionalMinorAndPatch = false) =>
        TryParse(text, out var version, optionalMinorAndPatch)
            ? version
            : throw new FormatException($"{text} is not valid SemVer string");

    /// <summary>Non-throwing <see cref="Parse"/>.</summary>
    public static bool TryParse(string? text, out SemVer version, bool optionalMinorAndPatch = false)
    {
        version = default;
        if (text is null)
        {
            return false;
        }

        var m = (optionalMinorAndPatch ? OptionalRegex() : StrictRegex()).Match(text);
        if (!m.Success ||
            !TryInt(m.Groups["major"], out var major) ||
            !TryInt(m.Groups["minor"], out var minor) ||
            !TryInt(m.Groups["patch"], out var patch))
        {
            return false;
        }

        var pre = m.Groups["prerelease"];
        var build = m.Groups["build"];
        version = new SemVer(major, minor, patch, pre.Success ? pre.Value : null, build.Success ? build.Value : null);
        return true;
    }

    /// <summary>python-semver's <c>Version.is_valid</c> (strict form).</summary>
    public static bool IsValid(string? text) => TryParse(text, out _);

    /// <summary>python-semver's <c>finalize_version</c>: the same major, minor and patch with no prerelease or build.</summary>
    public SemVer Finalize() => new(Major, Minor, Patch);

    /// <summary>python-semver's <c>replace(prerelease=...)</c>. The build is kept, as in python-semver.</summary>
    public SemVer WithPrerelease(string? prerelease) => new(Major, Minor, Patch, prerelease, Build);

    /// <summary>python-semver's <c>replace(build=...)</c>.</summary>
    public SemVer WithBuild(string? build) => new(Major, Minor, Patch, Prerelease, build);

    /// <summary>python-semver's <c>replace(major=..., minor=..., patch=...)</c>; parts left null are kept.</summary>
    public SemVer With(int? major = null, int? minor = null, int? patch = null) =>
        new(major ?? Major, minor ?? Minor, patch ?? Patch, Prerelease, Build);

    /// <summary>
    /// python-semver's <c>compare</c>: major, minor and patch numerically, then the prerelease (a version without
    /// one is greater). Build metadata is ignored.
    /// </summary>
    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c == 0)
        {
            c = Minor.CompareTo(other.Minor);
        }

        if (c == 0)
        {
            c = Patch.CompareTo(other.Patch);
        }

        if (c != 0)
        {
            return Math.Sign(c);
        }

        var rc = ComparePrerelease(Prerelease, other.Prerelease);
        if (rc == 0)
        {
            return 0;
        }

        // An empty or absent prerelease is a release, which sorts above every prerelease.
        if (string.IsNullOrEmpty(Prerelease))
        {
            return 1;
        }

        return string.IsNullOrEmpty(other.Prerelease) ? -1 : rc;
    }

    int IComparable.CompareTo(object? obj) => obj switch
    {
        null => 1,
        SemVer v => CompareTo(v),
        _ => throw new ArgumentException("Object is not a SemVer.", nameof(obj)),
    };

    /// <summary>python-semver's <c>__eq__</c>: equal when <see cref="CompareTo(SemVer)"/> is 0, so build metadata is ignored.</summary>
    public bool Equals(SemVer other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemVer v && Equals(v);

    /// <summary>Consistent with <see cref="Equals(SemVer)"/>: an empty and an absent prerelease hash alike.</summary>
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease ?? "");

    /// <summary>python-semver's <c>str()</c>: <c>M.m.p</c>, then <c>-pre</c> and <c>+build</c> when non-empty.</summary>
    public override string ToString()
    {
        var s = string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
        if (!string.IsNullOrEmpty(Prerelease))
        {
            s += "-" + Prerelease;
        }

        if (!string.IsNullOrEmpty(Build))
        {
            s += "+" + Build;
        }

        return s;
    }

    public static bool operator ==(SemVer left, SemVer right) => left.Equals(right);

    public static bool operator !=(SemVer left, SemVer right) => !left.Equals(right);

    public static bool operator <(SemVer left, SemVer right) => left.CompareTo(right) < 0;

    public static bool operator >(SemVer left, SemVer right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemVer left, SemVer right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemVer left, SemVer right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// python-semver's <c>_nat_cmp</c>: dot-separated identifiers, all-digit ones compared as numbers and below
    /// text ones, text compared by code point. When the shared identifiers are equal, the longer prerelease
    /// <em>string</em> (not identifier count) is greater, as in python-semver.
    /// </summary>
    private static int ComparePrerelease(string? a, string? b)
    {
        a ??= "";
        b ??= "";
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var c = CompareIdentifier(pa[i], pb[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return Math.Sign(a.Length.CompareTo(b.Length));
    }

    private static int CompareIdentifier(string a, string b)
    {
        var aNum = IsAllDigits(a);
        var bNum = IsAllDigits(b);
        if (aNum && bNum)
        {
            return CompareDigitStrings(a, b);
        }

        if (aNum)
        {
            return -1;
        }

        if (bNum)
        {
            return 1;
        }

        return Math.Sign(string.CompareOrdinal(a, b));
    }

    private static bool IsAllDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    // Compares unbounded non-negative integers written in ASCII digits, as Python's int() would.
    private static int CompareDigitStrings(string a, string b)
    {
        a = a.TrimStart('0');
        b = b.TrimStart('0');
        return a.Length != b.Length ? Math.Sign(a.Length - b.Length) : Math.Sign(string.CompareOrdinal(a, b));
    }

    private static bool TryInt(Group g, out int value)
    {
        value = 0;
        return !g.Success || g.Length == 0 ||
               int.TryParse(g.ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    // python-semver 3's _REGEX_TEMPLATE, with ASCII digits. $ matches before a final newline, as in Python.
    [GeneratedRegex(@"^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)" +
                    @"(?:-(?<prerelease>(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*))?" +
                    @"(?:\+(?<build>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex StrictRegex();

    [GeneratedRegex(@"^(?<major>0|[1-9][0-9]*)(?:\.(?<minor>0|[1-9][0-9]*)(?:\.(?<patch>0|[1-9][0-9]*))?)?" +
                    @"(?:-(?<prerelease>(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*))?" +
                    @"(?:\+(?<build>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex OptionalRegex();
}
