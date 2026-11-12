using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Tests.Versioning;

/// <summary>python-semver 3 behaviour, as upstream relies on it.</summary>
public class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null, null)]
    [InlineData("1.0.0-alpha.1+build.5", 1, 0, 0, "alpha.1", "build.5")]
    [InlineData("1.0.0-0.3.7", 1, 0, 0, "0.3.7", null)]
    [InlineData("1.0.0+20130313144700", 1, 0, 0, null, "20130313144700")]
    [InlineData("4.3.0-alpha+daily.ddc9f92777cd", 4, 3, 0, "alpha", "daily.ddc9f92777cd")]
    [InlineData("1.0.0--x-", 1, 0, 0, "-x-", null)]
    [InlineData("10.20.30-0a", 10, 20, 30, "0a", null)]
    public void Strict_parse_accepts_valid_versions(string text, int major, int minor, int patch, string? pre, string? build)
    {
        var v = SemVer.Parse(text);

        Assert.Equal((major, minor, patch, pre, build), (v.Major, v.Minor, v.Patch, v.Prerelease, v.Build));
        Assert.Equal(text, v.ToString());
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-a..b")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2.3-alpha_1")]
    [InlineData("")]
    [InlineData("99999999999.0.0")] // Python accepts this; the port's parts are int (documented deviation)
    public void Strict_parse_rejects_invalid_versions(string text)
    {
        Assert.False(SemVer.TryParse(text, out _));
        Assert.False(SemVer.IsValid(text));
        Assert.Throws<FormatException>(() => SemVer.Parse(text));
    }

    [Theory]
    [InlineData("5.2", "5.2.0")]
    [InlineData("5", "5.0.0")]
    [InlineData("5.2-rc1", "5.2.0-rc1")]
    [InlineData("5.2.1+b", "5.2.1+b")]
    public void Optional_minor_and_patch(string text, string expected) =>
        Assert.Equal(expected, SemVer.Parse(text, optionalMinorAndPatch: true).ToString());

    [Theory]
    [InlineData("01.2")]
    [InlineData("5.")]
    public void Optional_form_still_rejects_bad_text(string text) =>
        Assert.False(SemVer.TryParse(text, out _, optionalMinorAndPatch: true));

    [Fact]
    public void Precedence_follows_the_semver_example_chain()
    {
        string[] ordered =
        [
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11",
            "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0", "10.0.0",
        ];

        for (var i = 0; i < ordered.Length - 1; i++)
        {
            var lower = SemVer.Parse(ordered[i]);
            var higher = SemVer.Parse(ordered[i + 1]);
            Assert.True(lower < higher, $"{lower} < {higher}");
            Assert.True(higher > lower);
            Assert.Equal(-1, lower.CompareTo(higher));
            Assert.Equal(1, higher.CompareTo(lower));
        }

        var shuffled = ordered.Reverse().Select(s => SemVer.Parse(s)).ToList();
        shuffled.Sort();
        Assert.Equal(ordered, shuffled.Select(v => v.ToString()));
    }

    [Theory]
    [InlineData("1.0.0-B", "1.0.0-a", -1)]          // text identifiers by code point
    [InlineData("1.0.0-rc10", "1.0.0-rc9", -1)]     // "rc10" is text, not a number
    [InlineData("1.0.0-1", "1.0.0-a", -1)]          // numeric identifiers sort below text
    [InlineData("1.0.0-2", "1.0.0-10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.0", -1)]
    [InlineData("4.3.0-alpha+daily.abc", "4.3.0", -1)]
    [InlineData("4.3.0-alpha+x", "4.3.0-alpha+y", 0)]
    public void Compare_matches_python_semver(string a, string b, int expected)
    {
        Assert.Equal(expected, SemVer.Parse(a).CompareTo(SemVer.Parse(b)));
        Assert.Equal(-expected, SemVer.Parse(b).CompareTo(SemVer.Parse(a)));
    }

    [Fact]
    public void Prerelease_tie_break_is_by_string_length_as_python_semver()
    {
        // python-semver's _nat_cmp compares int("01") == int("1"), then len("rc.01") > len("rc.1").
        Assert.True(new SemVer(1, 0, 0, "rc.01") > new SemVer(1, 0, 0, "rc.1"));
    }

    [Fact]
    public void Equality_ignores_build_metadata_and_hash_agrees()
    {
        var a = SemVer.Parse("4.3.0-alpha+daily.ddc9f92777cd");
        var b = new SemVer(4, 3, 0, "alpha");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(SemVer.Parse("4.3.0-alpha"), SemVer.Parse("4.3.0-beta"));
        Assert.Single(new HashSet<SemVer> { a, b });
    }

    [Fact]
    public void Empty_and_absent_prerelease_are_the_same_release()
    {
        var empty = new SemVer(1, 2, 3, "", "");
        var none = new SemVer(1, 2, 3);

        Assert.Equal("1.2.3", empty.ToString());
        Assert.Equal(none, empty);
        Assert.Equal(none.GetHashCode(), empty.GetHashCode());
        Assert.True(new SemVer(1, 2, 3, "a") < empty);
    }

    [Fact]
    public void Constructor_does_not_validate_prerelease_but_rejects_negatives()
    {
        Assert.Equal("2.80.0-(sub 75)", new SemVer(2, 80, 0, "(sub 75)").ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemVer(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemVer(1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemVer(1, 0, -1));
        Assert.Equal("0.0.0", default(SemVer).ToString());
    }

    [Fact]
    public void Replace_helpers_match_python_semver()
    {
        var v = SemVer.Parse("4.3.0-alpha+daily.abc");

        Assert.Equal("4.3.0", v.Finalize().ToString());
        Assert.Equal("4.3.0+daily.abc", v.WithPrerelease(null).ToString()); // replace keeps the build
        Assert.Equal("4.3.0-beta+daily.abc", v.WithPrerelease("beta").ToString());
        Assert.Equal("4.3.0-alpha", v.WithBuild(null).ToString());
        Assert.Equal("5.3.1-alpha+daily.abc", v.With(major: 5, patch: 1).ToString());
    }

    [Fact]
    public void Upstream_update_check_compares_versions_without_prerelease()
    {
        // blender_update_manager: semversion.replace(prerelease=None) for both sides.
        var installed = SemVer.Parse("4.2.3-stable").WithPrerelease(null);
        var download = SemVer.Parse("4.2.4").WithPrerelease(null);

        Assert.True(download > installed);
        Assert.Equal(installed, SemVer.Parse("4.2.3"));
    }
}
