using System.Buffers;
using System.Text;

namespace BlenderLauncher.Core.Versioning;

/// <summary>
/// Upstream's free-text build search (<c>version_matcher.py:337-358</c>, <c>fuzzy_search_by_str</c>): a build
/// matches when a substring of its haystack (<see cref="BuildHaystack"/>) is within a small edit distance of the
/// search text, both case-folded. Upstream calls
/// <c>fuzzysearch.find_near_matches(needle, haystack, max_l_dist=d, max_deletions=1, max_substitutions=1,
/// max_insertions=1)</c> for d = 0, 1, 2, 3 and stops at the first d at which any build matches.
/// </summary>
/// <remarks>
/// fuzzysearch 0.8.1 picks a different algorithm per d, and the ones used for d = 1, 2 and 3 are greedy
/// candidate searches rather than an exact edit-distance DP, so they occasionally miss (or find) a match that
/// an exact search would not. <see cref="IsMatch"/> ports each algorithm as written so the results agree with
/// upstream case for case; see <c>FuzzyMatcher.Search.cs</c>.
/// </remarks>
public static partial class FuzzyMatcher
{
    /// <summary>The largest distance upstream tries.</summary>
    public const int MaxDistance = 3;

    // Folded strings longer than this are folded into a pooled array instead of the stack.
    private const int StackFoldLimit = 256;

    /// <summary>
    /// Upstream's <c>BasicBuildInfo.fuzzy_text</c>:
    /// <c>f"{version} {branch.casefold()} {custom_name.casefold() or ''} {build_hash} {commit_time} {folder}"</c>.
    /// </summary>
    /// <param name="version">The SemVer text as Python's <c>str(Version)</c> prints it.</param>
    /// <param name="branch">The branch name; case-folded here.</param>
    /// <param name="customName">The custom name, or null for none (prints as nothing); case-folded here.</param>
    /// <param name="buildHash">The build hash, empty when the build has none (upstream turns None into "").</param>
    /// <param name="commitTime">The commit time, already converted to UTC by the caller as upstream does.</param>
    /// <param name="folder">The build folder, or null, which prints as <c>None</c>.</param>
    public static string BuildHaystack(
        string version, string branch, string? customName, string buildHash, DateTimeOffset commitTime, string? folder) =>
        string.Join(' ',
            version,
            CaseFold(branch),
            customName is null ? "" : CaseFold(customName),
            buildHash,
            PythonDateTimeFormat.Format(commitTime),
            folder ?? "None");

    /// <summary>
    /// Whether <c>find_near_matches(casefold(needle), casefold(haystack), max_l_dist=distance,
    /// max_deletions=1, max_substitutions=1, max_insertions=1)</c> returns any match.
    /// </summary>
    /// <remarks>
    /// An empty needle matches everything. fuzzysearch raises <c>ValueError</c> for it instead; upstream never
    /// sends one, because the search bar strips the text and skips the fuzzy step when it is empty.
    /// Distances above 3 behave as 3, as fuzzysearch caps <c>max_l_dist</c> at the sum of the three limits.
    /// </remarks>
    public static bool IsMatch(string needle, string haystack, int distance)
    {
        ArgumentNullException.ThrowIfNull(needle);
        ArgumentNullException.ThrowIfNull(haystack);
        ArgumentOutOfRangeException.ThrowIfNegative(distance);

        char[]? needleRented = null;
        char[]? haystackRented = null;
        try
        {
            var foldedNeedle = Fold(needle, needle.Length <= StackFoldLimit / 3 ? stackalloc char[StackFoldLimit] : default, ref needleRented);
            var foldedHaystack = Fold(haystack, haystack.Length <= StackFoldLimit / 3 ? stackalloc char[StackFoldLimit] : default, ref haystackRented);
            return MatchesFolded(foldedNeedle, foldedHaystack, distance);
        }
        finally
        {
            if (needleRented is not null)
            {
                ArrayPool<char>.Shared.Return(needleRented);
            }

            if (haystackRented is not null)
            {
                ArrayPool<char>.Shared.Return(haystackRented);
            }
        }
    }

    /// <summary>The smallest d in 0..<paramref name="maxDistance"/> at which <see cref="IsMatch"/> is true, or null.</summary>
    /// <remarks>
    /// fuzzysearch's per-d algorithms are not monotonic: a pair can match at d and not at d + 1 (for example
    /// "b" against an empty haystack matches at 1 but not at 2). Selection is unaffected, because upstream keeps
    /// the builds that match at the smallest d any build matches at, and those are exactly the builds whose
    /// best distance is that d.
    /// </remarks>
    public static int? BestDistance(string needle, string haystack, int maxDistance = MaxDistance)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDistance);
        for (var d = 0; d <= maxDistance; d++)
        {
            if (IsMatch(needle, haystack, d))
            {
                return d;
            }
        }

        return null;
    }

    /// <summary>
    /// Upstream's <c>fuzzy_search_by_str</c>: for d = 0, 1, 2, 3, keep the candidates whose haystack matches at d,
    /// stopping at the first d that keeps any. Order is preserved. An empty needle keeps every candidate.
    /// </summary>
    public static List<T> Filter<T>(IReadOnlyList<T> candidates, Func<T, string> haystackOf, string needle)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(haystackOf);
        ArgumentNullException.ThrowIfNull(needle);

        var result = new List<T>();
        if (needle.Length == 0)
        {
            result.AddRange(candidates);
            return result;
        }

        var foldedNeedle = CaseFold(needle);

        // Fold every haystack once into one pooled buffer, since each may be searched up to four times.
        var total = 0;
        var haystacks = new string[candidates.Count];
        for (var i = 0; i < haystacks.Length; i++)
        {
            haystacks[i] = haystackOf(candidates[i]);
            total += haystacks[i].Length * MaxFoldExpansion;
        }

        var buffer = ArrayPool<char>.Shared.Rent(Math.Max(total, 1));
        var ranges = ArrayPool<(int Start, int Length)>.Shared.Rent(Math.Max(haystacks.Length, 1));
        try
        {
            var used = 0;
            for (var i = 0; i < haystacks.Length; i++)
            {
                var length = FoldInto(haystacks[i], buffer.AsSpan(used));
                ranges[i] = (used, length);
                used += length;
            }

            for (var d = 0; d <= MaxDistance && result.Count == 0; d++)
            {
                for (var i = 0; i < haystacks.Length; i++)
                {
                    if (MatchesFolded(foldedNeedle, buffer.AsSpan(ranges[i].Start, ranges[i].Length), d))
                    {
                        result.Add(candidates[i]);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
            ArrayPool<(int, int)>.Shared.Return(ranges);
        }

        return result;
    }

    /// <summary>
    /// Python's <c>str.casefold()</c>, close enough for the text in a haystack.
    /// </summary>
    /// <remarks>
    /// <c>ToLowerInvariant</c> applies Unicode's simple lower-case mapping; <c>casefold</c> applies full case
    /// folding, which differs in a few places. The ones handled here (from Python 3.14, Unicode 16) are the
    /// Latin, Greek, Armenian and Cyrillic ones a build or folder name could plausibly contain: ß and ẞ to "ss",
    /// ſ to s, µ to μ, İ to "i̇", final ς to σ, the Greek symbol forms (ϐ ϑ ϕ ϖ ϰ ϱ ϵ), ŉ, ǰ, ẖ-ẚ, ẛ, the
    /// combining and precomposed iota forms ͅ ΐ ΰ, the Latin and Armenian ligatures, and the old Cyrillic forms
    /// ᲀ-ᲈ. Not handled: polytonic Greek with iota subscript (U+1F50-U+1FFC), Cherokee (casefold maps it to
    /// upper case) and letters added in Unicode 16. Those fold as <c>ToLowerInvariant</c> leaves them.
    /// </remarks>
    public static string CaseFold(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        char[]? rented = null;
        try
        {
            return new string(Fold(value, value.Length <= StackFoldLimit / 3 ? stackalloc char[StackFoldLimit] : default, ref rented));
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    // The longest full folding handled (ﬃ, ΐ) is three UTF-16 units per input unit.
    private const int MaxFoldExpansion = 3;

    private static Span<char> Fold(string value, Span<char> stackBuffer, ref char[]? rented)
    {
        var destination = stackBuffer.Length >= value.Length * MaxFoldExpansion
            ? stackBuffer
            : rented = ArrayPool<char>.Shared.Rent(Math.Max(value.Length * MaxFoldExpansion, 1));
        return destination[..FoldInto(value, destination)];
    }

    private static int FoldInto(ReadOnlySpan<char> source, Span<char> destination)
    {
        var written = 0;
        while (!source.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(source, out var rune, out var consumed) != OperationStatus.Done)
            {
                // A lone surrogate: Python keeps it as is.
                destination[written++] = source[0];
                source = source[1..];
                continue;
            }

            source = source[consumed..];
            var special = FullFolding(rune.Value);
            if (special is not null)
            {
                special.CopyTo(destination[written..]);
                written += special.Length;
            }
            else
            {
                written += Rune.ToLowerInvariant(rune).EncodeToUtf16(destination[written..]);
            }
        }

        return written;
    }

    // Where casefold differs from ToLowerInvariant; generated by comparing the two over every code point.
    private static string? FullFolding(int codePoint) => codePoint switch
    {
        0x00B5 => "μ",
        0x00DF => "ss",
        0x0130 => "i̇",
        0x0149 => "ʼn",
        0x017F => "s",
        0x01F0 => "ǰ",
        0x0345 => "ι",
        0x0390 => "ΐ",
        0x03B0 => "ΰ",
        0x03C2 => "σ",
        0x03D0 => "β",
        0x03D1 => "θ",
        0x03D5 => "φ",
        0x03D6 => "π",
        0x03F0 => "κ",
        0x03F1 => "ρ",
        0x03F5 => "ε",
        0x0587 => "եւ",
        0x1C80 => "в",
        0x1C81 => "д",
        0x1C82 => "о",
        0x1C83 => "с",
        0x1C84 => "т",
        0x1C85 => "т",
        0x1C86 => "ъ",
        0x1C87 => "ѣ",
        0x1C88 => "ꙋ",
        0x1E96 => "ẖ",
        0x1E97 => "ẗ",
        0x1E98 => "ẘ",
        0x1E99 => "ẙ",
        0x1E9A => "aʾ",
        0x1E9B => "ṡ",
        0x1E9E => "ss",
        0xFB00 => "ff",
        0xFB01 => "fi",
        0xFB02 => "fl",
        0xFB03 => "ffi",
        0xFB04 => "ffl",
        0xFB05 => "st",
        0xFB06 => "st",
        0xFB13 => "մն",
        0xFB14 => "մե",
        0xFB15 => "մի",
        0xFB16 => "վն",
        0xFB17 => "մխ",
        _ => null,
    };
}
