using System.Globalization;
using System.Text.RegularExpressions;

namespace BlenderLauncher.Core.Versioning;

/// <summary>
/// Turns Blender's many version spellings into a <see cref="SemVer"/>: a port of upstream's
/// <c>parse_blender_ver</c> (<c>modules/build_info.py:74-158</c>), pinned by the
/// <c>test_build_parser.py</c> table.
/// </summary>
public static partial class BlenderVersionParser
{
    /// <summary>
    /// Parses <paramref name="text"/>. With <paramref name="search"/> the fallback patterns may match anywhere
    /// in the cleaned text; without it they must match at its start. Throws <see cref="FormatException"/>
    /// ("No valid version found") when nothing matches.
    /// </summary>
    public static SemVer Parse(string text, bool search = false) =>
        TryParse(text, out var version, search) ? version : throw new FormatException("No valid version found");

    /// <summary>Non-throwing <see cref="Parse"/>.</summary>
    public static bool TryParse(string? text, out SemVer version, bool search = false)
    {
        version = default;
        if (text is null)
        {
            return false;
        }

        // 1. Strict semver.
        if (SemVer.TryParse(text, out version))
        {
            return true;
        }

        // 2. Clean up and try strict semver again.
        var s = SimpleClean(text);
        if (SemVer.TryParse(s, out version))
        {
            return true;
        }

        // 3. The fallback patterns, in order; the first that matches wins.
        Match? g = null;
        foreach (var matcher in Matchers)
        {
            var m = matcher.Match(s);

            // Python's re.match anchors at the start. A leftmost match that does not start at 0 means no
            // match starts there, because the engine tries position 0 first.
            if (m.Success && (search || m.Index == 0))
            {
                g = m;
                break;
            }
        }

        if (g is null ||
            !TryInt(g.Groups["ma"].Value, out var major) ||
            !TryInt(g.Groups["mi"].Value, out var minor))
        {
            return false;
        }

        var patch = 0;
        var pa = g.Groups["pa"];
        if (pa.Success && !TryInt(pa.Value, out patch))
        {
            return false;
        }

        // 4. The prerelease is casefolded and stripped of '-' and ' '; "lts" means none.
        string? prerelease = null;
        var pre = g.Groups["pre"];
        if (pre.Success)
        {
            prerelease = PythonText.CaseFold(pre.Value).Trim('-', ' ');
            if (prerelease.Trim().Equals("lts", StringComparison.OrdinalIgnoreCase))
            {
                prerelease = null;
            }
        }

        version = new SemVer(major, minor, patch, prerelease);
        return true;
    }

    /// <summary>
    /// Upstream's <c>simple_clean</c>: cut from the first digit after a "blender" or "v" prefix, then truncate at
    /// <c>-windows</c> and at <c>-linux</c>. <c>-macos</c> is deliberately not stripped.
    /// </summary>
    internal static string SimpleClean(string s)
    {
        var captures = InitialCleaner().Match(s);
        if (captures.Success)
        {
            // Upstream cuts at s.find(group), the group's first occurrence, which can come before the match.
            s = s[s.IndexOf(captures.Groups[1].Value, StringComparison.Ordinal)..];
        }

        var idx = s.IndexOf("-windows", StringComparison.Ordinal);
        if (idx != -1)
        {
            s = s[..idx];
        }

        idx = s.IndexOf("-linux", StringComparison.Ordinal);
        if (idx != -1)
        {
            s = s[..idx];
        }

        return s;
    }

    private static bool TryInt(string digits, out int value) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static readonly Regex[] Matchers =
    [
        DottedWithPrerelease(),
        SubPatch(),
        MajorMinorOnly(),
        ShortSuffix(),
        LooseSuffix(),
    ];

    // Patterns as upstream writes them, with \d narrowed to ASCII digits (see SemVer's remarks).
    // <major>.<minor>.<patch> <prerelease>: "2.80.0 Alpha" -> 2.80.0-alpha
    [GeneratedRegex(@"(?<ma>[0-9]+)\.(?<mi>[0-9]+)(?:\.(?<pa>[0-9]+))?[ \-](?<pre>[^\+]*)", RegexOptions.CultureInvariant)]
    private static partial Regex DottedWithPrerelease();

    // <major>.<minor> (sub <patch>): "2.80 (sub 75)"
    [GeneratedRegex(@"(?<ma>[0-9]+)\.(?<mi>[0-9]+) \(sub (?<pa>[0-9]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex SubPatch();

    // <major>.<minor>: "2.79" -> 2.79.0
    [GeneratedRegex(@"(?<ma>[0-9]+)\.(?<mi>[0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex MajorMinorOnly();

    // <major>.<minor><up to 3 chars>: "2.79rc1" -> 2.79.0-rc1
    [GeneratedRegex(@"(?<ma>[0-9]+)\.(?<mi>[0-9]+)(?<pre>[^-]{0,3})", RegexOptions.CultureInvariant)]
    private static partial Regex ShortSuffix();

    // <major>.<minor><suffix>: "2.79b" -> 2.79.0-b
    [GeneratedRegex(@"(?<ma>[0-9]+)\.(?<mi>[0-9]+)(?<pre>[^0-9][^\.\s]*)?", RegexOptions.CultureInvariant)]
    private static partial Regex LooseSuffix();

    [GeneratedRegex(@"(?:blender|v)-?([0-9].*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InitialCleaner();
}
