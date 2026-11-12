using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace BlenderLauncher.Core.Versioning;

/// <summary>
/// Upstream's <c>VersionSearchQuery</c> (<c>modules/version_matcher.py</c>): a query over builds,
/// written <c>&lt;major&gt;.&lt;minor&gt;[.&lt;patch&gt;][-&lt;branch&gt;[,…]][+&lt;build_hash&gt;][@&lt;commit time&gt;]</c>.
/// Each of major, minor and patch is <c>^</c> (newest), <c>-</c> (oldest), <c>*</c> (any) or a number.
/// </summary>
/// <remarks>
/// <para>
/// The properties are declared in upstream's priority order, which is also the order <see cref="Match"/> filters
/// in: <see cref="FuzzyText"/>, <see cref="Folder"/>, <see cref="BuildHash"/>, <see cref="Major"/>,
/// <see cref="Minor"/>, <see cref="Patch"/>, <see cref="Branch"/>, <see cref="CommitTime"/>, <see cref="After"/>,
/// <see cref="Before"/>. Defaults are <c>*</c> for the positions and null for the rest, as upstream.
/// </para>
/// <para>
/// Immutable and validated on every construction and <c>with</c>, as upstream's frozen dataclass and its
/// <c>__post_init__</c>: invalid parts throw <see cref="VersionSearchQueryException"/>. Equality compares every
/// property, the branch alternatives in order.
/// </para>
/// </remarks>
public sealed partial record VersionSearchQuery
{
    /// <summary>Upstream's <c>VERSION_SEARCH_SYNTAX</c>, shown with an invalid query.</summary>
    public const string Syntax = "<major_num>.<minor>.<patch>[-<branch>][+<build_hash>][@<commit time>]";

    /// <summary>Free text matched with <see cref="FuzzyMatcher"/>. Not part of the query text.</summary>
    public string? FuzzyText { get; init; }

    /// <summary>The build folder, matched exactly. Not part of the query text.</summary>
    public string? Folder { get; init; }

    /// <summary>The git hash of the build. <c>^</c> and <c>-</c> are rejected.</summary>
    public string? BuildHash
    {
        get;
        init => field = value is "^" or "-" ? throw VersionSearchQueryException.HashTemporal() : value;
    }

    public VersionSearchPosition Major { get; init; }

    public VersionSearchPosition Minor { get; init; }

    public VersionSearchPosition Patch { get; init; }

    /// <summary>
    /// Branch alternatives (upstream's tuple): a build matches when its branch equals any of them. A single
    /// alternative of <c>^</c> or <c>-</c> is rejected. The list is copied.
    /// </summary>
    public IReadOnlyList<string>? Branch
    {
        get;
        init => field = value is null ? null
            : value is [("^" or "-")] ? throw VersionSearchQueryException.BranchTemporal()
            : value.ToArray();
    }

    /// <summary>The commit time: <c>^</c>, <c>-</c>, <c>*</c> or an exact time. Null and <c>*</c> both match any.</summary>
    public VersionSearchTime? CommitTime { get; init; }

    /// <summary>Keeps builds committed at or after this time. Not part of the query text.</summary>
    public DateTimeOffset? After { get; init; }

    /// <summary>Keeps builds committed at or before this time. Not part of the query text.</summary>
    public DateTimeOffset? Before { get; init; }

    /// <summary>Upstream's <c>default()</c>: <c>^.^.^@^</c>, the newest build of the newest version.</summary>
    public static VersionSearchQuery Default() => new()
    {
        Major = VersionSearchPosition.Newest,
        Minor = VersionSearchPosition.Newest,
        Patch = VersionSearchPosition.Newest,
        CommitTime = VersionSearchTime.Newest,
    };

    /// <summary>Upstream's <c>any()</c>: every property at its default, matching every build.</summary>
    public static VersionSearchQuery Any() => new();

    /// <summary>Upstream's <c>version(major, minor, patch)</c>, for example <c>Version(4, 3, "^")</c>.</summary>
    public static VersionSearchQuery Version(VersionSearchPosition major, VersionSearchPosition minor, VersionSearchPosition patch) =>
        new() { Major = major, Minor = minor, Patch = patch };

    /// <summary>
    /// Upstream's <c>parse</c>: reads query text (the format <see cref="ToString"/> writes). A missing patch is
    /// <c>*</c>, the branch is split on commas, and a commit time other than a symbol is read with
    /// <c>datetime.fromisoformat</c> rules (<see cref="Builds.IsoTimestamp.TryParse"/>). Throws
    /// <see cref="VersionSearchQueryException"/>: <see cref="VersionSearchQueryException.InvalidQueryKey"/> when
    /// the text does not match the grammar, <see cref="VersionSearchQueryException.InvalidPositionKey"/> for a
    /// commit time that is not ISO, and the temporal keys for a hash or branch of <c>^</c> or <c>-</c>.
    /// </summary>
    public static VersionSearchQuery Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var m = QueryRegex().Match(text);
        if (!m.Success)
        {
            throw VersionSearchQueryException.InvalidQuery(text);
        }

        return new VersionSearchQuery
        {
            BuildHash = m.Groups[5].Success ? m.Groups[5].Value : null,
            Major = ParsePosition(m.Groups[1].Value, text),
            Minor = ParsePosition(m.Groups[2].Value, text),
            Patch = m.Groups[3].Success ? ParsePosition(m.Groups[3].Value, text) : VersionSearchPosition.Any,
            Branch = m.Groups[4].Success ? m.Groups[4].Value.Split(',') : (IReadOnlyList<string>?)null,
            CommitTime = m.Groups[6].Success ? (VersionSearchTime?)VersionSearchTime.Parse(m.Groups[6].Value) : null,
        };
    }

    /// <summary>Non-throwing <see cref="Parse"/>.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out VersionSearchQuery? query)
    {
        query = null;
        if (text is null)
        {
            return false;
        }

        try
        {
            query = Parse(text);
            return true;
        }
        catch (VersionSearchQueryException)
        {
            return false;
        }
    }

    /// <summary>
    /// The search bar's rule (upstream <c>widgets/search_bar/search_bar.py:94-106</c>): the text is stripped; if it
    /// is empty, <paramref name="baseQuery"/> is returned. Otherwise the first word (up to the first space) is
    /// tried as a query, and the rest, if any, becomes its fuzzy text; that query's relevant places override
    /// <paramref name="baseQuery"/> (<see cref="Or"/>). If the first word is not a query, the whole stripped
    /// text becomes the fuzzy text of <paramref name="baseQuery"/>.
    /// </summary>
    /// <param name="text">The search box text.</param>
    /// <param name="baseQuery">The query from the other controls (after and before dates); <see cref="Any"/> when null.</param>
    public static VersionSearchQuery ParseSearchText(string text, VersionSearchQuery? baseQuery = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var query = baseQuery ?? Any();
        text = PythonStrip(text);
        if (text.Length == 0)
        {
            return query;
        }

        var space = text.IndexOf(' ', StringComparison.Ordinal);
        var first = space < 0 ? text : text[..space];
        try
        {
            var q = Parse(first);
            if (space >= 0)
            {
                q = q with { FuzzyText = text[(space + 1)..] };
            }

            return query.Or(q);
        }
        catch (VersionSearchQueryException)
        {
            return query with { FuzzyText = text };
        }
    }

    /// <summary>
    /// Upstream's <c>__or__</c>: this query with every relevant place of <paramref name="other"/> (see
    /// <see cref="IsRelevant"/>) copied over it.
    /// </summary>
    public VersionSearchQuery Or(VersionSearchQuery other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return this with
        {
            FuzzyText = other.IsRelevant(VersionSearchPlace.FuzzyText) ? other.FuzzyText : FuzzyText,
            Folder = other.IsRelevant(VersionSearchPlace.Folder) ? other.Folder : Folder,
            BuildHash = other.IsRelevant(VersionSearchPlace.BuildHash) ? other.BuildHash : BuildHash,
            Major = other.IsRelevant(VersionSearchPlace.Major) ? other.Major : Major,
            Minor = other.IsRelevant(VersionSearchPlace.Minor) ? other.Minor : Minor,
            Patch = other.IsRelevant(VersionSearchPlace.Patch) ? other.Patch : Patch,
            Branch = other.IsRelevant(VersionSearchPlace.Branch) ? other.Branch : Branch,
            CommitTime = other.IsRelevant(VersionSearchPlace.CommitTime) ? other.CommitTime : CommitTime,
            After = other.IsRelevant(VersionSearchPlace.After) ? other.After : After,
            Before = other.IsRelevant(VersionSearchPlace.Before) ? other.Before : Before,
        };
    }

    /// <summary>Upstream's <c>__or__</c> operator; see <see cref="Or"/>.</summary>
    public static VersionSearchQuery operator |(VersionSearchQuery left, VersionSearchQuery right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.Or(right);
    }

    /// <summary>
    /// Whether a place filters (upstream's <c>relevant_places</c>): its value is neither null nor <c>*</c>. A
    /// text place set to <c>"*"</c> is not relevant; an empty text, or an empty branch list, is.
    /// </summary>
    public bool IsRelevant(VersionSearchPlace place) => place switch
    {
        VersionSearchPlace.FuzzyText => FuzzyText is not (null or "*"),
        VersionSearchPlace.Folder => Folder is not (null or "*"),
        VersionSearchPlace.BuildHash => BuildHash is not (null or "*"),
        VersionSearchPlace.Major => Major.Kind != VersionSearchPositionKind.Any,
        VersionSearchPlace.Minor => Minor.Kind != VersionSearchPositionKind.Any,
        VersionSearchPlace.Patch => Patch.Kind != VersionSearchPositionKind.Any,
        VersionSearchPlace.Branch => Branch is not null,
        VersionSearchPlace.CommitTime => CommitTime is { Kind: not VersionSearchPositionKind.Any },
        VersionSearchPlace.After => After is not null,
        VersionSearchPlace.Before => Before is not null,
        _ => throw new ArgumentOutOfRangeException(nameof(place)),
    };

    /// <summary>
    /// Upstream's <c>__str__</c>, which <see cref="Parse"/> reads back: <c>major.minor.patch</c>, then
    /// <c>-branch,…</c> when there is a non-empty branch list, <c>+hash</c> when there is a non-empty hash, and
    /// <c>@time</c> unless the commit time is null or <c>*</c>. A time prints as Python's <c>str(datetime)</c>,
    /// for example <c>2020-05-04 00:00:00+00:00</c>. Fuzzy text, folder, after and before are not written.
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Major.ToString()).Append('.').Append(Minor.ToString()).Append('.').Append(Patch.ToString());
        if (Branch is { Count: > 0 })
        {
            sb.Append('-').AppendJoin(',', Branch);
        }

        if (!string.IsNullOrEmpty(BuildHash))
        {
            sb.Append('+').Append(BuildHash);
        }

        if (CommitTime is { Kind: not VersionSearchPositionKind.Any } time)
        {
            sb.Append('@').Append(time.ToString());
        }

        return sb.ToString();
    }

    public bool Equals(VersionSearchQuery? other) =>
        other is not null
        && FuzzyText == other.FuzzyText
        && Folder == other.Folder
        && BuildHash == other.BuildHash
        && Major == other.Major
        && Minor == other.Minor
        && Patch == other.Patch
        && (Branch is null ? other.Branch is null : other.Branch is not null && Branch.SequenceEqual(other.Branch))
        && Nullable.Equals(CommitTime, other.CommitTime)
        && Nullable.Equals(After, other.After)
        && Nullable.Equals(Before, other.Before);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(FuzzyText);
        hash.Add(Folder);
        hash.Add(BuildHash);
        hash.Add(Major);
        hash.Add(Minor);
        hash.Add(Patch);
        hash.Add(Branch?.Count ?? -1);
        foreach (var b in Branch ?? [])
        {
            hash.Add(b);
        }

        hash.Add(CommitTime);
        hash.Add(After);
        hash.Add(Before);
        return hash.ToHashCode();
    }

    // A number (Python's isnumeric() then int(); \d also takes non-ASCII decimal digits) or a symbol.
    private static VersionSearchPosition ParsePosition(string text, string query)
    {
        if (text is "^" or "*" or "-")
        {
            return VersionSearchPosition.FromSymbol(text);
        }

        long value = 0;
        foreach (var c in text)
        {
            value = (value * 10) + (long)char.GetNumericValue(c);
            if (value > int.MaxValue)
            {
                // Python's int is unbounded; a number this large cannot be a Blender version anyway.
                throw VersionSearchQueryException.InvalidQuery(query);
            }
        }

        return VersionSearchPosition.Exact((int)value);
    }

    // Python's str.strip(): str.isspace() also counts U+001C-U+001F, which char.IsWhiteSpace does not.
    private static string PythonStrip(string text) =>
        text.Trim().Trim('\u001C', '\u001D', '\u001E', '\u001F').Trim();

    /// <summary>
    /// Upstream's <c>VERSION_SEARCH_REGEX</c> (<c>version_matcher.py:83-91</c>), compiled with <c>re.X</c>. Verbose
    /// mode drops the newlines and indentation between the parts and keeps whitespace inside character classes,
    /// so the escaped space in the commit-time class stays a literal space; this is the same pattern on one line.
    /// <c>$</c> also matches before a final newline, in Python and .NET alike, and <c>\d</c>, <c>\w</c> and
    /// <c>\s</c> are Unicode-aware in both.
    /// </summary>
    [GeneratedRegex(
        @"^([\^\-\*]|\d+)\.([\^\-\*]|\d+)(?:\.([\^\-\*]|\d+))?(?:\-([^\@\s\+]+))?(?:\+([\d\w]+))?(?:\@([\^\-\*]|[\dT\+\:Z\ \^\-]+))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex QueryRegex();
}

/// <summary>The places of a <see cref="VersionSearchQuery"/>, in upstream's priority (filtering) order.</summary>
public enum VersionSearchPlace
{
    FuzzyText,
    Folder,
    BuildHash,
    Major,
    Minor,
    Patch,
    Branch,
    CommitTime,
    After,
    Before,
}
