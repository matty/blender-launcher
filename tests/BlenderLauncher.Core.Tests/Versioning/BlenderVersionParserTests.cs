using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Tests.Versioning;

public class BlenderVersionParserTests
{
    /// <summary>
    /// Every row of upstream's <c>tests/backend/test_build_parser.py::test_parser</c>. Upstream compares with
    /// python-semver's ==, which ignores build metadata; the expected text here pins the build too.
    /// </summary>
    public static TheoryData<string, bool, string> UpstreamRows => new()
    {
        { "Blender1.0", true, "1.0.0" },
        { "blender-4.3.0-alpha-linux", true, "4.3.0-alpha" },
        { "3.6.14", false, "3.6.14" },
        { "4.3.0-alpha+daily.ddc9f92777cd", true, "4.3.0-alpha+daily.ddc9f92777cd" },
        { "blender-3.3.21-stable+v33.e016c21db151-linux.x86_64-release.tar.xz", true, "3.3.21-stable+v33.e016c21db151" },
        { "blender-4.1.0-linux-x64.tar.xz", false, "4.1.0" },
        { "2.80 (sub 75)", false, "2.80.0-(sub 75)" },
        { "2.79rc1", false, "2.79.0-rc1" },
        { "3.6", false, "3.6.0" },
        { "v4.4.4", true, "4.4.4" },
        { "BLENDER1.0", true, "1.0.0" },
        { "4.4.0 Alpha", false, "4.4.0-alpha" },
    };

    [Theory]
    [MemberData(nameof(UpstreamRows))]
    public void Upstream_parser_table(string text, bool search, string expected)
    {
        // "2.80.0-(sub 75)" is not valid semver text, only a constructed value, as in upstream's table.
        var expectedVersion = SemVer.TryParse(expected, out var strict) ? strict : new SemVer(2, 80, 0, "(sub 75)");

        var parsed = BlenderVersionParser.Parse(text, search);
        Assert.Equal(expectedVersion, parsed);
        Assert.Equal(expected, parsed.ToString());

        // Things that do not need to be searched also work when searched.
        if (!search)
        {
            Assert.Equal(expected, BlenderVersionParser.Parse(text, search: true).ToString());
        }
    }

    [Theory]
    [InlineData("2.79b", false, "2.79.0-b")]
    [InlineData("4.20", false, "4.20.0")]
    [InlineData("2.48.0-a", false, "2.48.0-a")]
    [InlineData("4.2.1 LTS", false, "4.2.1")]               // "lts" is dropped
    [InlineData("4.2-lts", false, "4.2.0")]
    [InlineData("Blender 4.2 LTS", true, "4.2.0")]           // the prefix is only skipped when searching
    [InlineData("blender-5.2.0-macos-arm64", false, "5.2.0-macos-arm64")] // -macos is not stripped
    [InlineData("Blender2.53beta", true, "2.53.0-bet")]          // the short-suffix pattern takes at most three characters
    [InlineData("2.80.0 Release Candidate", false, "2.80.0-release candidate")]
    public void Own_cases(string text, bool search, string expected) =>
        Assert.Equal(expected, BlenderVersionParser.Parse(text, search).ToString());

    [Theory]
    [InlineData("Blender 4.2 LTS")]
    [InlineData("")]
    [InlineData("4")]
    public void Unmatched_text_throws_without_search(string text)
    {
        var e = Assert.Throws<FormatException>(() => BlenderVersionParser.Parse(text));
        Assert.Equal("No valid version found", e.Message);
        Assert.False(BlenderVersionParser.TryParse(text, out _));
    }

    [Fact]
    public void Null_does_not_parse() => Assert.False(BlenderVersionParser.TryParse(null, out _));

    [Theory]
    [InlineData("blender-4.3.0-alpha-linux", "4.3.0-alpha")]
    [InlineData("v4.4.4", "4.4.4")]
    [InlineData("Blender 4.2", "Blender 4.2")]
    [InlineData("1v1", "1v1")] // upstream cuts at the group's first occurrence, which here is before the match
    public void Simple_clean(string text, string expected) =>
        Assert.Equal(expected, BlenderVersionParser.SimpleClean(text));
}
