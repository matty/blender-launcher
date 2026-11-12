using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Tests.Versioning;

public class FuzzyMatcherTests
{
    private sealed record Build(string Version, string Branch, DateTimeOffset CommitTime);

    // The nine builds of upstream's tests/backend/test_version_matcher.py (empty hash, no custom name or folder).
    private static readonly Build[] UpstreamBuilds =
    [
        new("1.2.3", "stable", Utc(2020, 5, 4)),
        new("1.2.2", "stable", Utc(2020, 4, 2)),
        new("1.2.1", "daily", Utc(2020, 3, 1)),
        new("1.2.4", "stable", Utc(2020, 6, 3)),
        new("3.6.14", "lts", Utc(2024, 7, 16)),
        new("4.2.0", "stable", Utc(2024, 7, 16)),
        new("4.3.0", "daily", Utc(2024, 7, 30)),
        new("4.3.0", "daily", Utc(2024, 7, 28)),
        new("4.3.1", "daily", Utc(2024, 7, 20)),
    ];

    private static DateTimeOffset Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    private static string Haystack(Build b) => FuzzyMatcher.BuildHaystack(b.Version, b.Branch, null, "", b.CommitTime, null);

    private static List<Build> Search(string needle) => FuzzyMatcher.Filter(UpstreamBuilds, Haystack, needle);

    // Upstream's test_fuzzy_search is ported once, through the query, in VersionSearchQueryTests.

    [Fact]
    public void Upstream_haystack_format_is_reproduced() =>
        // Python: f"{version} {branch.casefold()} {''} {build_hash} {commit_time} {folder}" for 3.6.14.
        Assert.Equal("3.6.14 lts   2024-07-16 00:00:00+00:00 None", Haystack(UpstreamBuilds[4]));

    [Fact]
    public void Haystack_folds_branch_and_custom_name_and_prints_folder() =>
        Assert.Equal(
            "4.3.0-alpha+daily.cb886aba06d5 daily my straße build cb886aba06d5 2024-07-30 12:34:56+00:00 daily\blender-4.3.0".Replace("ß", "ss"),
            FuzzyMatcher.BuildHaystack(
                "4.3.0-alpha+daily.cb886aba06d5", "Daily", "My STRAßE Build", "cb886aba06d5",
                new DateTimeOffset(2024, 7, 30, 12, 34, 56, TimeSpan.Zero), "daily\blender-4.3.0"));

    [Fact]
    public void Empty_needle_keeps_every_candidate() => Assert.Equal(UpstreamBuilds, Search(""));

    [Fact]
    public void Filter_stops_at_the_first_distance_with_any_match()
    {
        // "abc" matches the second haystack exactly, so the first (a distance-1 match only) is not kept.
        string[] haystacks = ["xabdx", "xabcx", "zzzzz"];
        Assert.Equal(["xabcx"], FuzzyMatcher.Filter(haystacks, h => h, "abc"));
        Assert.Equal(["xabdx", "xabcx"], FuzzyMatcher.Filter(haystacks, h => h, "abq"));
    }

    [Fact]
    public void Matching_is_not_monotonic_in_distance()
    {
        // fuzzysearch matches "b" against an empty haystack at d = 1 (every index is an empty match once the
        // whole needle may be deleted) but not at d = 2, where its generic search needs a haystack character.
        Assert.True(FuzzyMatcher.IsMatch("b", "", 1));
        Assert.False(FuzzyMatcher.IsMatch("b", "", 2));
        Assert.Equal(1, FuzzyMatcher.BestDistance("b", ""));
    }

    [Fact]
    public void Best_distance_is_null_when_nothing_matches_within_three() =>
        Assert.Null(FuzzyMatcher.BestDistance("abcd", "ab"));

    [Theory]
    [InlineData("stbe", "1.2.3 stable   2020-05-04 00:00:00+00:00 None", 2)]
    [InlineData("4.2.0", "4.2.0 stable   2024-07-16 00:00:00+00:00 None", 0)]
    public void Best_distance(string needle, string haystack, int expected) =>
        Assert.Equal(expected, FuzzyMatcher.BestDistance(needle, haystack));

    [Theory]
    [MemberData(nameof(PythonCases))]
    public void Agrees_with_python_fuzzysearch(string needle, string haystack, bool d0, bool d1, bool d2, bool d3)
    {
        bool[] expected = [d0, d1, d2, d3];
        bool[] actual = [.. Enumerable.Range(0, 4).Select(d => FuzzyMatcher.IsMatch(needle, haystack, d))];
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Distances_above_three_behave_as_three() =>
        Assert.Equal(FuzzyMatcher.IsMatch("baabbb ba", "aababbbba aaba", 3), FuzzyMatcher.IsMatch("baabbb ba", "aababbbba aaba", 7));

    [Theory]
    [InlineData("STRAẞE", "strasse")]
    [InlineData("ﬁle", "file")]
    [InlineData("ΣΊΣΥΦΟΣ ς", "σίσυφοσ σ")]
    [InlineData("µ", "μ")]
    [InlineData("𐐀", "𐐨")] // Deseret capital long I folds to its small letter.
    public void Case_fold_matches_python(string input, string expected) =>
        Assert.Equal(expected, FuzzyMatcher.CaseFold(input));

    [Theory]
    [InlineData(2024, 7, 16, 0, 0, 0, 0, 0, "2024-07-16 00:00:00+00:00")]
    [InlineData(2024, 7, 16, 1, 2, 3, 450, -330, "2024-07-16 01:02:03.000450-05:30")]
    [InlineData(5, 1, 2, 0, 0, 0, 0, 60, "0005-01-02 00:00:00+01:00")]
    public void Python_datetime_str(int y, int mo, int d, int h, int mi, int s, int micro, int offsetMinutes, string expected) =>
        Assert.Equal(expected, PythonDateTimeFormat.Format(
            new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.FromMinutes(offsetMinutes)).AddMicroseconds(micro)));

    [Fact]
    public void Python_datetime_str_drops_sub_microsecond_ticks() =>
        Assert.Equal("2024-07-16 00:00:00+00:00", PythonDateTimeFormat.Format(Utc(2024, 7, 16).AddTicks(9)));

    // Each row: needle, haystack, then whether
    // find_near_matches(needle.casefold(), haystack.casefold(), max_l_dist=d, max_deletions=1,
    // max_substitutions=1, max_insertions=1) returned any match for d = 0, 1, 2, 3. Produced by fuzzysearch 0.8.1
    // (pure-Python paths) on Python 3.10: random strings over small alphabets, mutated slices of build haystacks,
    // and hand-picked edge cases. The same generator's 35,000 cases (140,000 checks) agreed in full before these
    // were frozen. A representative subset of the frozen rows is kept.
    public static TheoryData<string, string, bool, bool, bool, bool> PythonCases => new()
    {
        { "b", "", false, true, false, false },
        { "baabbb ba", "aababbbba aaba", false, false, false, true },
        { "bbbababaa", "", false, false, false, false },
        { "datb02bsld", "4.2.0 stable  ab12cd34ef56 2024-07-16 00:00:00+00:00 None", false, false, false, false },
        { "cbcbcaa", "aacbbaaabbbc", false, false, true, true },
        { "baabaaa", "aabaaabbbaabaaaabbababaaabbb", true, true, true, true },
        { "14 lts  202", "3.6.14 lts  2024-07-16 00:00:00+00:00 None", true, true, true, true },
        { "-07-16 00:0:00+00:00", "4.2.0 stable  ab12cd34ef56 2024-07-16 00:00:00+00:00 None", false, true, true, true },
        { "abcd", "abc", false, true, true, true },
        { "abcd", "ab", false, false, false, false },
        { "xbc", "abc", false, true, true, true },
        { "abc", "xabcx", true, true, true, true },
        { "ab", "ba", false, true, true, true },
        { "stbe", "stable", false, false, true, true },
        { "STABLE", "Stable", true, true, true, true },
        { "\u00DF", "ss", true, true, true, true },
        { "\u0130", "i\u0307", true, true, true, true },
        { "\uD83D\uDE00b", "a\uD83D\uDE00b", true, true, true, true },
        { "\uD83D\uDE00", "x\uD83D\uDE00", true, true, true, true },
        { "a\uD83D\uDE00c", "abc", false, true, true, true },
        { "abcdefghijkl", "abcdxfghiyjkl", false, false, true, true },
        { "version", "verison", false, false, true, true },
        { ".0-alhab+dailyc", "4.3.0-alpha+daily.cb886aba06d5 daily my build cb886aba06d5 2024-07-30 12:34:56+00:00 C:\\Builds\\daily\\blender-4.3.0", false, false, false, true },
        { "c:/builds", "4.3.0-alpha+daily.cb886aba06d5 daily my build cb886aba06d5 2024-07-30 12:34:56+00:00 C:\\Builds\\daily\\blender-4.3.0", false, true, true, true },
    };
}
