using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Tests.Versioning;

/// <summary>A port of upstream's <c>tests/backend/test_version_matcher.py</c>, then own cases.</summary>
public class VersionSearchQueryTests
{
    private static readonly VersionSearchCandidate[] Builds =
    [
        B("1.2.3", "stable", 2020, 5, 4),
        B("1.2.2", "stable", 2020, 4, 2),
        B("1.2.1", "daily", 2020, 3, 1),
        B("1.2.4", "stable", 2020, 6, 3),
        B("3.6.14", "lts", 2024, 7, 16),
        B("4.2.0", "stable", 2024, 7, 16),
        B("4.3.0", "daily", 2024, 7, 30),
        B("4.3.0", "daily", 2024, 7, 28),
        B("4.3.1", "daily", 2024, 7, 20),
    ];

    private static VersionSearchCandidate B(string version, string branch, int y, int m, int d) =>
        new(SemVer.Parse(version), branch, "", Utc(y, m, d));

    private static DateTimeOffset Utc(int y, int m, int d, int h = 0, int mi = 0, int s = 0) => new(y, m, d, h, mi, s, TimeSpan.Zero);

    // One test per upstream test function, each keeping all of upstream's assertions.

    [Fact]
    public void Upstream_test_matcher()
    {
        // Latest minor with any patch.
        Assert.Equal(
            [B("4.3.0", "daily", 2024, 7, 30), B("4.3.0", "daily", 2024, 7, 28), B("4.3.1", "daily", 2024, 7, 20)],
            VersionSearchQuery.Version("^", "^", "*").Match(Builds));

        // Any version with patch 14.
        Assert.Equal([B("3.6.14", "lts", 2024, 7, 16)], VersionSearchQuery.Version("*", "*", 14).Match(Builds));

        // Any version in the lts branch.
        Assert.Equal(
            [B("3.6.14", "lts", 2024, 7, 16)],
            (VersionSearchQuery.Version("*", "*", "*") with { Branch = ["lts"] }).Match(Builds));

        // Latest daily of the latest major.
        Assert.Equal(
            [B("4.3.0", "daily", 2024, 7, 30)],
            (VersionSearchQuery.Version("^", "*", "*") with { Branch = ["daily"], CommitTime = "^" }).Match(Builds));

        // Oldest major with the largest patch.
        Assert.Equal([B("1.2.4", "stable", 2020, 6, 3)], VersionSearchQuery.Version("-", "*", "^").Match(Builds));
    }

    [Fact]
    public void Upstream_test_fuzzy_search()
    {
        Assert.Equal(Builds.Where(b => b.Branch == "stable"), new VersionSearchQuery { FuzzyText = "stbe" }.Match(Builds));
        Assert.Equal(Builds.Where(b => b.Branch == "daily"), new VersionSearchQuery { FuzzyText = "daly" }.Match(Builds));
        Assert.Equal(
            [new VersionSearchCandidate(new SemVer(3, 6, 14), "lts", "", Utc(2024, 7, 16)),
             new VersionSearchCandidate(new SemVer(4, 2, 0), "stable", "", Utc(2024, 7, 16))],
            new VersionSearchQuery { FuzzyText = "07 16" }.Match(Builds));
    }

    [Fact]
    public void Upstream_test_date_range_filtering()
    {
        // After and before are inclusive, alone or together.
        var after = Utc(2020, 5, 1);
        Assert.Equal(Builds.Where(b => b.CommitTime >= after), new VersionSearchQuery { After = after }.Match(Builds));

        var before = Utc(2020, 6, 1);
        Assert.Equal(Builds.Where(b => b.CommitTime <= before), new VersionSearchQuery { Before = before }.Match(Builds));

        DateTimeOffset start = Utc(2020, 4, 1), end = Utc(2020, 6, 1);
        Assert.Equal(
            Builds.Where(b => start <= b.CommitTime && b.CommitTime <= end),
            new VersionSearchQuery { After = start, Before = end }.Match(Builds));

        // A build exactly on the boundary.
        var exact = Utc(2020, 5, 4);
        Assert.Equal([Builds[0]], new VersionSearchQuery { After = exact, Before = exact }.Match(Builds));
    }

    // ---- test_vsq_serialization ----

    public static TheoryData<string, VersionSearchQuery> SerialisationQueries => new()
    {
        { "*.*.*", VersionSearchQuery.Any() },
        { "^.^.^@^", VersionSearchQuery.Default() },
        { "^.^.*", VersionSearchQuery.Version("^", "^", "*") },
        { "*.*.14", VersionSearchQuery.Version("*", "*", 14) },
        { "*.*.*-lts", VersionSearchQuery.Version("*", "*", "*") with { Branch = ["lts"] } },
        { "^.*.*-daily@^", VersionSearchQuery.Version("^", "*", "*") with { Branch = ["daily"], CommitTime = "^" } },
        { "-.*.^", VersionSearchQuery.Version("-", "*", "^") },
        { "4.0.0", VersionSearchQuery.Version(4, 0, 0) },
        { "4.*.*", VersionSearchQuery.Version(4, "*", "*") },
        {
            "^.^.*-stable@2020-05-04 00:00:00+00:00",
            VersionSearchQuery.Version("^", "^", "*") with { Branch = ["stable"], CommitTime = Utc(2020, 5, 4) }
        },
    };

    [Theory]
    [MemberData(nameof(SerialisationQueries))]
    public void Round_trip_gives_an_equal_query_and_equal_results(string text, VersionSearchQuery query)
    {
        var before = query.Match(Builds);
        var serialised = query.ToString();
        Assert.Equal(text, serialised);

        var parsed = VersionSearchQuery.Parse(serialised);
        Assert.Equal(query, parsed);
        Assert.Equal(before, parsed.Match(Builds));
    }

    [Fact]
    public void Upstream_test_search_query_parser()
    {
        Assert.Equal(new VersionSearchQuery { Major = 1, Minor = 2, Patch = 3 }, VersionSearchQuery.Parse("1.2.3"));
        Assert.Equal(new VersionSearchQuery { Major = "^", Patch = "-" }, VersionSearchQuery.Parse("^.*.-"));
        Assert.Equal(
            new VersionSearchQuery { Major = "*", Minor = "*", Patch = "*", Branch = ["daily"] },
            VersionSearchQuery.Parse("*.*.*-daily"));
        Assert.Equal(
            VersionSearchQuery.Version("*", "*", "*") with { BuildHash = "cb886aba06d5" },
            VersionSearchQuery.Parse("*.*.*+cb886aba06d5"));

        // A commit time with T or a space.
        var timed = VersionSearchQuery.Version("*", "*", "*") with { CommitTime = Utc(2024, 7, 31, 23, 53, 51) };
        Assert.Equal(timed, VersionSearchQuery.Parse("*.*.*@2024-07-31T23:53:51+00:00"));
        Assert.Equal(timed, VersionSearchQuery.Parse("*.*.*@2024-07-31 23:53:51+00:00"));

        // Text that is not a query.
        var e = Assert.Throws<VersionSearchQueryException>(() => VersionSearchQuery.Parse("abc"));
        Assert.Equal("launching.errors.invalid_query", e.Key);
        Assert.Equal("abc", e.Arguments["query"]);
        Assert.Equal("Invalid version search query: abc", e.Message);
    }

    // ---- own cases ----

    // Examples from version_matcher.py's docstring; the ones the round-trip rows already cover are left out.
    [Theory]
    [InlineData("1.2.3-master")]
    [InlineData("4.^.^-stable@^")]
    [InlineData("4.3.^+cb886aba06d5@^")]
    [InlineData("4.3.^-stable+cb886aba06d5@2024-07-31T23:53:51+00:00")]
    public void Upstream_valid_query_examples_parse(string text) => Assert.NotNull(VersionSearchQuery.Parse(text));

    [Fact]
    public void Missing_patch_is_any()
    {
        var q = VersionSearchQuery.Parse("4.2");
        Assert.Equal(VersionSearchQuery.Version(4, 2, "*"), q);
        Assert.Equal("4.2.*", q.ToString());
    }

    [Fact]
    public void Full_query_parses_every_part()
    {
        var q = VersionSearchQuery.Parse("4.3.^-stable,lts+cb886aba06d5@2024-07-31T23:53:51+01:00");
        Assert.Equal(VersionSearchPosition.Exact(4), q.Major);
        Assert.Equal(VersionSearchPosition.Exact(3), q.Minor);
        Assert.Equal(VersionSearchPosition.Newest, q.Patch);
        Assert.Equal(["stable", "lts"], q.Branch!);
        Assert.Equal("cb886aba06d5", q.BuildHash);
        Assert.Equal(VersionSearchPositionKind.Exact, q.CommitTime!.Value.Kind);
        Assert.Equal(TimeSpan.FromHours(1), q.CommitTime.Value.Value.Offset);
        // str(datetime) keeps the offset as written and separates with a space.
        Assert.Equal("4.3.^-stable,lts+cb886aba06d5@2024-07-31 23:53:51+01:00", q.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("4")]
    [InlineData("a.b.c")]
    [InlineData("4.2.1.0")]
    [InlineData("4.2-da ily")]
    [InlineData("4.2+ab-c")]
    [InlineData("4.2@2024-07-31.5")]
    [InlineData(" 4.2")]
    [InlineData("4.2 ")]
    public void Rejects_invalid_grammar(string text) =>
        Assert.Equal(
            VersionSearchQueryException.InvalidQueryKey,
            Assert.Throws<VersionSearchQueryException>(() => VersionSearchQuery.Parse(text)).Key);

    [Fact]
    public void Trailing_newline_is_accepted_as_by_pythons_dollar() =>
        Assert.Equal(VersionSearchQuery.Version(4, 2, 1), VersionSearchQuery.Parse("4.2.1\n"));

    [Fact]
    public void Commit_time_that_is_not_iso_is_an_invalid_position()
    {
        // Upstream keeps the text when fromisoformat fails; __post_init__ then rejects it.
        var e = Assert.Throws<VersionSearchQueryException>(() => VersionSearchQuery.Parse("*.*.*@2024-13-45"));
        Assert.Equal(VersionSearchQueryException.InvalidPositionKey, e.Key);
        Assert.Equal("2024-13-45", e.Arguments["value"]);
    }

    [Fact]
    public void Naive_commit_time_parses_and_matches_nothing()
    {
        var q = VersionSearchQuery.Parse("*.*.*@2024-07-16");
        Assert.True(q.CommitTime!.Value.Value.IsNaive);
        Assert.Equal("*.*.*@2024-07-16 00:00:00", q.ToString());
        Assert.Empty(q.Match(Builds));
    }

    [Fact]
    public void Exact_aware_commit_time_matches_the_same_instant()
    {
        var q = VersionSearchQuery.Parse("*.*.*@2024-07-16T01:00:00+01:00");
        Assert.Equal([Builds[4], Builds[5]], q.Match(Builds));
    }

    [Fact]
    public void Text_position_other_than_a_symbol_is_invalid()
    {
        var e = Assert.Throws<VersionSearchQueryException>(() => VersionSearchQuery.Version("x", 1, 1));
        Assert.Equal(VersionSearchQueryException.InvalidPositionKey, e.Key);
        Assert.Equal("x", e.Arguments["value"]);
        Assert.Throws<VersionSearchQueryException>(() => new VersionSearchQuery { CommitTime = "now" });
    }

    [Fact]
    public void Temporal_hash_and_branch_are_rejected()
    {
        Assert.Equal(
            VersionSearchQueryException.HashTemporalKey,
            Assert.Throws<VersionSearchQueryException>(() => new VersionSearchQuery { BuildHash = "^" }).Key);
        Assert.Equal(
            VersionSearchQueryException.BranchTemporalKey,
            Assert.Throws<VersionSearchQueryException>(() => VersionSearchQuery.Parse("*.*.*--")).Key);
    }

    [Fact]
    public void Default_and_any()
    {
        Assert.Equal("^.^.^@^", VersionSearchQuery.Default().ToString());
        // ^.^.^ narrows to 4.3.1 before the commit time is looked at.
        Assert.Equal([B("4.3.1", "daily", 2024, 7, 20)], VersionSearchQuery.Default().Match(Builds));
        Assert.Equal(Builds, VersionSearchQuery.Any().Match(Builds));
    }

    [Fact]
    public void Narrowing_is_hierarchical_per_remaining_set()
    {
        // ^.^ is the newest minor within the newest major, not the largest minor overall (6 in 3.6).
        Assert.All(VersionSearchQuery.Version("^", "^", "*").Match(Builds), b => Assert.Equal((4, 3), (b.Major, b.Minor)));
        // -.-.- is the oldest patch within the oldest minor within the oldest major.
        Assert.Equal([Builds[2]], VersionSearchQuery.Version("-", "-", "-").Match(Builds));
    }

    [Fact]
    public void Branch_alternatives_match_any_and_keep_input_order() =>
        Assert.Equal(
            Builds.Where(b => b.Branch is "lts" or "daily"),
            (VersionSearchQuery.Any() with { Branch = ["lts", "daily"] }).Match(Builds));

    [Fact]
    public void Empty_branch_list_matches_nothing_and_is_not_printed()
    {
        var q = VersionSearchQuery.Any() with { Branch = [] };
        Assert.Empty(q.Match(Builds));
        Assert.Equal("*.*.*", q.ToString());
    }

    [Fact]
    public void Build_hash_and_folder_use_equality()
    {
        VersionSearchCandidate[] builds =
        [
            new(new SemVer(4, 3, 0), "daily", "aaa", Utc(2024, 1, 1), "daily\\a"),
            new(new SemVer(4, 3, 0), "daily", "bbb", Utc(2024, 1, 2), "daily\\b"),
        ];
        Assert.Equal([builds[1]], (VersionSearchQuery.Any() with { BuildHash = "bbb" }).Match(builds));
        Assert.Equal([builds[0]], (VersionSearchQuery.Any() with { Folder = "daily\\a" }).Match(builds));
        Assert.Empty((VersionSearchQuery.Any() with { Folder = "DAILY\\A" }).Match(builds));
    }

    [Fact]
    public void Empty_result_returns_early_and_empty_input_does_not_raise()
    {
        Assert.Empty(VersionSearchQuery.Version(9, "^", "^").Match(Builds));
        Assert.Empty(VersionSearchQuery.Default().Match([]));
    }

    [Fact]
    public void Fuzzy_text_filters_before_the_version_places()
    {
        // Priority order: fuzzy first, so ^ is the newest among the stable builds, not overall.
        var q = new VersionSearchQuery { FuzzyText = "stable", Major = "^", Minor = "^", Patch = "^" };
        Assert.Equal([B("4.2.0", "stable", 2024, 7, 16)], q.Match(Builds));
    }

    [Fact]
    public void Match_with_a_projection_returns_the_items()
    {
        var items = Builds.Select((b, i) => (Index: i, Build: b)).ToArray();
        Assert.Equal([3], VersionSearchQuery.Version("-", "*", "^").Match(items, x => x.Build).Select(x => x.Index));
    }

    [Fact]
    public void Or_overrides_only_the_relevant_places()
    {
        var left = new VersionSearchQuery { After = Utc(2020, 1, 1), Major = 4, Branch = ["daily"] };
        var right = new VersionSearchQuery { Minor = "^", FuzzyText = "x", Branch = ["lts"] };
        var combined = left | right;
        Assert.Equal(
            new VersionSearchQuery { After = Utc(2020, 1, 1), Major = 4, Minor = "^", FuzzyText = "x", Branch = ["lts"] },
            combined);
        Assert.Equal(left, left.Or(VersionSearchQuery.Any()));
    }

    [Fact]
    public void Commit_time_star_differs_from_null_but_neither_is_relevant()
    {
        var star = VersionSearchQuery.Parse("*.*.*@*");
        Assert.NotEqual(VersionSearchQuery.Any(), star);
        Assert.False(star.IsRelevant(VersionSearchPlace.CommitTime));
        Assert.Equal("*.*.*", star.ToString());
    }

    [Fact]
    public void Text_star_is_not_relevant()
    {
        Assert.False(new VersionSearchQuery { FuzzyText = "*" }.IsRelevant(VersionSearchPlace.FuzzyText));
        Assert.True(new VersionSearchQuery { FuzzyText = "" }.IsRelevant(VersionSearchPlace.FuzzyText));
    }

    [Fact]
    public void Equality_compares_branch_contents_and_hash_codes_agree()
    {
        var a = VersionSearchQuery.Parse("4.2.^-stable,lts");
        var b = VersionSearchQuery.Version(4, 2, "^") with { Branch = new List<string> { "stable", "lts" } };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { Branch = ["lts", "stable"] });
    }

    [Fact]
    public void Candidate_from_build_info_uses_full_version_empty_hash_and_utc()
    {
        var build = new BuildInfo
        {
            Link = @"C:\lib\daily\blender-4.3.0",
            Subversion = "4.3.0-alpha",
            BuildHash = null,
            CommitTime = IsoTimestamp.Aware(new DateTime(2024, 7, 30, 12, 0, 0), TimeSpan.FromHours(2)),
            Branch = "daily",
            CustomName = "Mine",
        };
        var c = VersionSearchCandidate.FromBuildInfo(build, @"daily\blender-4.3.0");
        Assert.Equal("4.3.0-alpha+daily", c.Version.ToString());
        Assert.Equal("", c.BuildHash);
        Assert.Equal(TimeSpan.Zero, c.CommitTime.Offset);
        Assert.Equal(new DateTime(2024, 7, 30, 10, 0, 0), c.CommitTime.DateTime);
        Assert.Equal("4.3.0-alpha+daily daily mine  2024-07-30 10:00:00+00:00 daily\\blender-4.3.0", c.Haystack);
    }

    [Fact]
    public void Candidate_from_build_info_reads_a_naive_time_as_local()
    {
        var build = new BuildInfo
        {
            Link = "x",
            Subversion = "4.2.0",
            CommitTime = IsoTimestamp.Naive(new DateTime(2024, 7, 16, 12, 0, 0)),
            Branch = "stable",
        };
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus3", TimeSpan.FromHours(3), "plus3", "plus3");
        var c = VersionSearchCandidate.FromBuildInfo(build, localZone: zone);
        Assert.Equal(Utc(2024, 7, 16, 9), c.CommitTime);
        Assert.False(VersionSearchCandidate.TryFromBuildInfo(build with { Subversion = "nonsense" }, out _));
    }

    [Fact]
    public void Candidates_order_by_version_then_commit_time()
    {
        var sorted = Builds.Order().ToArray();
        Assert.Equal(Builds[2], sorted[0]);
        Assert.Equal(B("4.3.0", "daily", 2024, 7, 28), sorted[^3]);
        Assert.Equal(B("4.3.0", "daily", 2024, 7, 30), sorted[^2]);
    }

    // ---- search bar (search_bar.py:94-106) ----

    [Fact]
    public void Search_text_is_a_query_word_then_fuzzy_text()
    {
        // Empty text returns the base query itself.
        var baseQuery = new VersionSearchQuery { After = Utc(2020, 1, 1) };
        Assert.Same(baseQuery, VersionSearchQuery.ParseSearchText("   ", baseQuery));
        Assert.Equal(VersionSearchQuery.Any(), VersionSearchQuery.ParseSearchText(""));

        // The first word is a query and the rest is fuzzy; a single query word has no fuzzy text.
        Assert.Equal(
            VersionSearchQuery.Version(4, "^", "*") with { FuzzyText = "my build" },
            VersionSearchQuery.ParseSearchText("  4.^ my build "));
        Assert.Equal(VersionSearchQuery.Parse("4.2.1-daily"), VersionSearchQuery.ParseSearchText("4.2.1-daily"));

        // Text that is not a query is all fuzzy, and the base dates are kept either way.
        var beforeQuery = new VersionSearchQuery { Before = Utc(2025, 1, 1) };
        Assert.Equal(beforeQuery with { FuzzyText = "daily 4.2" }, VersionSearchQuery.ParseSearchText(" daily 4.2", beforeQuery));
        Assert.Equal(new VersionSearchQuery { After = Utc(2020, 1, 1), Major = 4, Minor = 2 }, VersionSearchQuery.ParseSearchText("4.2", baseQuery));

        // As Python's split(" ", 1): the remainder keeps a second space.
        Assert.Equal(" x", VersionSearchQuery.ParseSearchText("4.2  x").FuzzyText);

        // Text that matches the grammar but is invalid is fuzzy.
        Assert.Equal("*.*.*@2024-13-45", VersionSearchQuery.ParseSearchText("*.*.*@2024-13-45").FuzzyText);
    }
}
