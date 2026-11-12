using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Tests.Builds;

public class BuildInfoTests
{
    private static readonly IsoTimestamp Before = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero);
    private static readonly IsoTimestamp After = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Upstream's positional BuildInfo(link, subversion, build_hash, commit_time, branch).
    private static BuildInfo B(string subversion, string? hash, IsoTimestamp time, string branch = "", string link = "") =>
        new() { Link = link, Subversion = subversion, BuildHash = hash, CommitTime = time, Branch = branch };

    private sealed class Lts(params (int Major, int Minor)[] series) : ILtsLookup
    {
        public bool IsLts(int major, int minor) => series.Contains((major, minor));
    }

    // ---- Ported from tests/backend/test_build_info_collector.py ----

    [Fact]
    public void Upstream_comparisons_and_equality()
    {
        Assert.True(B("1.0.0", "", Before).CompareTo(B("1.0.1", "", Before)) < 0);
        Assert.True(B("1.0.0", "", Before).CompareTo(B("1.0.0", "", After)) < 0);
        Assert.True(B("1.0.0", "hash", Before).Matches(B("1.0.0", "hash", Before)));
        Assert.True(B("1.0.0", "hash", Before).Matches(B("1.0.0", null, Before)));
        Assert.True(B("1.0.0", null, Before).Matches(B("1.0.0", null, Before)));
        Assert.True(B("1.0.0-daily", null, Before).Matches(B("1.0.0", null, Before)));
    }

    [Theory]
    [InlineData(4, 4, 0, null, "4.4.0")]
    [InlineData(4, 4, 0, "alpha", "4.4.0")]
    [InlineData(2, 79, 75, null, "2.79")]
    [InlineData(2, 79, 75, "a", "2.79a")]
    [InlineData(2, 79, 0, "b", "2.79b")]
    [InlineData(2, 83, 0, "alpha", "2.83alpha")]
    public void Upstream_display_version_edge_cases(int major, int minor, int patch, string? pre, string expected) =>
        Assert.Equal(expected, BuildInfo.DisplayVersionOf(new SemVer(major, minor, patch, pre)));

    [Theory]
    [InlineData("lts", 0, 0, 1, null, "", false, "LTS")]
    [InlineData("experimental", 4, 4, 0, "npr-prototypers", "4.4.0-npr-prototype", false, "Npr Prototypers")]
    [InlineData("experimental", 4, 4, 0, null, "4.4.0-npr-prototype", false, "Npr-Prototype")]
    [InlineData("daily", 2, 80, 0, "rc2", "2.80.0-rc2", false, "Rc2")]
    [InlineData("daily", 2, 80, 0, null, "2.80.0-rc2", false, "Rc2")]
    [InlineData("stable", 2, 80, 0, "rc2", "2.80.0-rc2", false, "Release Candidate 2")]
    [InlineData("stable", 2, 80, 0, "intel", "2.80.0-rc2", true, "Stable - intel")] // upstream sets sys.platform = "darwin"
    public void Upstream_display_label_variants(
        string branch, int major, int minor, int patch, string? pre, string subversion, bool macOs, string expected) =>
        Assert.Equal(expected, BuildInfo.DisplayLabelOf(branch, new SemVer(major, minor, patch, pre), subversion, macOs));

    // ---- Own cases ----

    [Theory]
    [InlineData("upbge-stable", "UPBGE Stable")]
    [InlineData("upbge", "UPBGE")]
    [InlineData("bforartists", "Bforartists")]
    public void Display_label_from_branch(string branch, string expected) =>
        Assert.Equal(expected, B("4.4.0", null, Before, branch).DisplayLabel);

    [Fact]
    public void Display_label_prefers_custom_name_then_bforartists_dev()
    {
        Assert.Equal("Mine", (B("4.4.0", null, Before, "daily") with { CustomName = "Mine" }).DisplayLabel);
        Assert.Equal("Bforartists Dev", B("4.5.0-dev", null, Before, "bforartists").DisplayLabel);
        Assert.Equal("macos-arm64", B("5.2.0-macos-arm64", null, Before, "stable").SemVersion!.Value.Prerelease);
        Assert.Equal("Stable - arm64", BuildInfo.DisplayLabelOf("stable", SemVer.Parse("5.2.0-macos-arm64"), "5.2.0-macos-arm64", macOs: true));
        Assert.Equal("Stable", BuildInfo.DisplayLabelOf("stable", SemVer.Parse("5.2.0-macos-arm64"), "5.2.0-macos-arm64", macOs: false));
    }

    [Fact]
    public void Display_version()
    {
        Assert.Equal("Dev", B("0.0.0-dev", null, Before, "bforartists").DisplayVersion);
        Assert.Equal("4.5.0", B("4.5.0-dev", null, Before, "bforartists").DisplayVersion);
        Assert.Equal("2.79b", B("2.79b", null, Before, "stable").DisplayVersion);
        Assert.Equal("4.3.0", B("4.3.0-alpha+daily.abc", null, Before, "daily").DisplayVersion);
        Assert.Equal("not a version", B("not a version", null, Before, "custom").DisplayVersion);
    }

    [Theory]
    [InlineData("4.3.0-alpha", "daily", "ddc9f92777cd", "4.3.0-alpha+daily.ddc9f92777cd")]
    [InlineData("4.1.0", "stable", null, "4.1.0-stable")]
    [InlineData("4.1.0", "stable", "", "4.1.0-stable")]
    [InlineData("4.4.0", "", null, "4.4.0")]
    [InlineData("4.3.0-alpha+daily.abc", "daily", "abc", "4.3.0-alpha+daily.abc+daily.abc")] // replace keeps the build
    public void Full_semversion_keeps_upstreams_text(string subversion, string branch, string? hash, string expected) =>
        Assert.Equal(expected, B(subversion, hash, Before, branch).FullSemVersion.ToString());

    [Fact]
    public void Lts_promotion_matches_major_and_minor_exactly()
    {
        var lts = new Lts((4, 2), (3, 6));

        Assert.Equal("lts", B("4.2.3", null, Before, "stable").PromoteLts(lts).Branch);
        Assert.Equal("lts", B("3.6.14", null, Before, "stable").PromoteLts(lts).Branch);

        // Upstream used subversion.startswith("4.2"), which also matched 4.20.
        Assert.Equal("stable", B("4.20.0", null, Before, "stable").PromoteLts(lts).Branch);
        Assert.Equal("stable", B("4.3.0", null, Before, "stable").PromoteLts(lts).Branch);
        Assert.Equal("daily", B("4.2.3", null, Before, "daily").PromoteLts(lts).Branch);

        var unchanged = B("4.3.0", null, Before, "stable");
        Assert.Same(unchanged, unchanged.PromoteLts(lts));
    }

    [Fact]
    public void Lts_promotion_follows_the_table_given_at_use()
    {
        var build = B("5.2.0", null, Before, "stable");

        Assert.Equal("stable", build.PromoteLts(new Lts()).Branch);
        Assert.Equal("lts", build.PromoteLts(new Lts((5, 2))).Branch);
    }

    [Fact]
    public void Bforartists_dev_builds_match_only_each_other_by_commit_time()
    {
        var dev = B("4.5.0-dev", "a", Before, "bforartists");

        Assert.True(dev.IsBfaDev);
        Assert.True(dev.Matches(B("4.6.0-dev", "b", Before, "bforartists")));
        Assert.False(dev.Matches(B("4.5.0-dev", "a", After, "bforartists")));
        Assert.False(dev.Matches(B("4.5.0", "a", Before, "bforartists")));
        Assert.False(B("4.5.0-dev", null, Before, "daily").IsBfaDev);
    }

    [Fact]
    public void Matching_rules_in_upstreams_order()
    {
        Assert.False(B("1.0.0", "a", Before, "daily").Matches(B("1.0.0", "b", Before, "daily")));    // hashes differ
        Assert.True(B("1.0.0", "a", Before, "daily").Matches(B("2.0.0", "a", Before, "daily")));     // hash wins over version
        Assert.False(B("1.0.0", "a", Before, "daily").Matches(B("1.0.0", "a", Before, "stable")));  // branch differs
        Assert.False(B("1.0.0", null, Before, "lts").Matches(B("1.0.0", null, Before, "stable")));
        Assert.True(B("4.5.2", null, Before, "bforartists").Matches(B("4.5.2-window", null, Before, "bforartists")));
        Assert.True(B("junk", null, Before, "custom").Matches(B("junk", null, After, "custom")));    // unparsable: text
        Assert.False(B("junk", null, Before, "custom").Matches(B("junk2", null, Before, "custom")));
        Assert.False(B("1.0.0", null, Before).Matches(null));
    }

    [Fact]
    public void Match_comparer_hash_is_consistent_with_matches()
    {
        BuildInfo[] builds =
        [
            B("1.0.0", "hash", Before, "daily"), B("1.0.0", null, After, "daily"), B("1.0.0-alpha", null, Before, "daily"),
            B("2.0.0", "hash", Before, "daily"), B("4.5.0-dev", null, Before, "bforartists"),
            B("4.6.0-dev", "x", Before, "bforartists"), B("4.5.0", null, Before, "bforartists"), B("junk", null, Before, "custom"),
        ];

        foreach (var a in builds)
        {
            foreach (var b in builds)
            {
                if (BuildInfo.MatchComparer.Equals(a, b))
                {
                    Assert.Equal(BuildInfo.MatchComparer.GetHashCode(a), BuildInfo.MatchComparer.GetHashCode(b));
                }
            }
        }
    }

    [Fact]
    public void Ordering_by_finalised_version_then_commit_time_then_full_semversion()
    {
        var naive = IsoTimestamp.Naive(new DateTime(2024, 1, 1));
        var list = new List<BuildInfo>
        {
            B("4.3.0", null, After, "stable"),
            B("4.3.0-alpha", "b", Before, "daily"),
            B("2.79b", null, Before, "stable"),
            B("junk", null, Before, "custom"),
            B("4.4.0", null, Before, "stable"),
        };
        list.Sort();

        Assert.Equal(["junk", "2.79b", "4.3.0-alpha", "4.3.0", "4.4.0"], list.Select(b => b.Subversion));

        // Naive against aware commit times fall back to full_semversion: "4.3.0-daily" < "4.3.0-stable".
        Assert.True(B("4.3.0", null, naive, "daily").CompareTo(B("4.3.0", null, Before, "stable")) < 0);
        Assert.True(B("4.3.0", null, Before, "stable").CompareTo(B("4.3.0", null, naive, "daily")) > 0);
    }

    [Fact]
    public void With_recomputes_the_parsed_version_and_validity()
    {
        var a = B("4.3.0", null, Before, "stable");
        var b = a with { Subversion = "4.4.1-beta" };

        Assert.Equal(new SemVer(4, 4, 1, "beta"), b.SemVersion);
        Assert.Equal(new SemVer(4, 3, 0), a.SemVersion);
        Assert.True(a.IsValid);
        Assert.False((a with { Subversion = "junk" }).IsValid);
        Assert.Null((a with { Subversion = "junk" }).SemVersion);
        Assert.False((a with { Branch = "" }).IsValid);
        Assert.False((a with { Subversion = "" }).IsValid);
        Assert.Equal(a, a with { });
        Assert.NotEqual(a, a with { IsFavorite = true });
    }
}
