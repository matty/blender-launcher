using System.Globalization;

namespace BlenderLauncher.Core.Api;

/// <summary>
/// An <c>api_file_version</c> such as <c>"29.10"</c>, compared as two integers so that 29.10 is newer than 29.9.
/// Upstream compares <c>float(...)</c> values, which gets that wrong.
/// </summary>
public readonly record struct ApiFileVersion(int Major, int Minor) : IComparable<ApiFileVersion>
{
    /// <summary>
    /// Parses <c>"major"</c> or <c>"major.minor"</c> (non-negative integers, surrounding white space allowed).
    /// Returns false for anything else.
    /// </summary>
    public static bool TryParse(string? text, out ApiFileVersion version)
    {
        version = default;
        if (text is null)
        {
            return false;
        }

        var parts = text.Trim().Split('.');
        if (parts.Length is < 1 or > 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            return false;
        }

        var minor = 0;
        if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor))
        {
            return false;
        }

        version = new ApiFileVersion(major, minor);
        return true;
    }

    /// <summary>The version a stable-cache save writes after this one: the minor bumped.</summary>
    public ApiFileVersion NextMinor() => this with { Minor = Minor + 1 };

    /// <summary>The version a cache build writes after this one: the major bumped, minor reset.</summary>
    public ApiFileVersion NextMajor() => new(Major + 1, 0);

    public int CompareTo(ApiFileVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    public static bool operator <(ApiFileVersion left, ApiFileVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ApiFileVersion left, ApiFileVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ApiFileVersion left, ApiFileVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ApiFileVersion left, ApiFileVersion right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// Upstream's rule for replacing the stored stable cache: replace when the stored version is missing or
    /// unreadable, or the incoming one is strictly newer.
    /// </summary>
    public static bool ShouldReplace(string? stored, string? incoming)
    {
        if (!TryParse(stored, out var current))
        {
            return true;
        }

        // Upstream treats a missing incoming version as 0.
        var next = TryParse(incoming, out var parsed) ? parsed : default;
        return next > current;
    }

    /// <summary><c>"major.minor"</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");
}
