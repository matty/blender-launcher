using BlenderLauncher.Core.Api;

namespace BlenderLauncher.Core.Tests.Api;

/// <summary>Against upstream's bundled <c>source/resources/api/blender_launcher_api.json</c>.</summary>
public class ApiDataTests
{
    private static ApiData Bundled() => ApiData.Parse(Fixtures.ReadText("upstream-api-blender_launcher_api.json"));

    [Fact]
    public void Bundled_file_lists_every_series_in_order()
    {
        var api = Bundled();

        Assert.Equal(new ApiFileVersion(3, 0), api.FileVersion);
        Assert.Equal(25, api.Series.Count);
        Assert.Equal("5.2", api.Series[0].ToString());
        Assert.Equal("2.79", api.Series[^1].ToString());
        Assert.Equal(["5.2", "4.5", "4.2", "3.6", "3.3", "2.93", "2.83"], api.LtsSeries.Select(s => s.ToString()));
    }

    [Theory]
    [InlineData(4, 2, true)]
    [InlineData(4, 20, false)] // upstream's startswith("4.2") would have said yes
    [InlineData(4, 3, false)]
    [InlineData(6, 0, false)]
    public void Lts_lookup_is_exact(int major, int minor, bool expected) => Assert.Equal(expected, Bundled().IsLts(major, minor));

    [Fact]
    public void Dropdown_index_follows_file_order()
    {
        var index = Bundled().DropdownIndex();

        Assert.Equal(0, index["5.2"]);
        Assert.Equal(6, index["4.2"]);
        Assert.Equal(24, index["2.79"]);
        Assert.Equal(25, index.Count);
    }

    [Fact]
    public void Odd_entries_are_skipped_or_ignored()
    {
        var api = ApiData.Parse("""{"blender_versions": {"5.3": "lts", "oops": "LTS", "5.4": null, "5.5": "LTS"}}""");

        Assert.Null(api.FileVersion);
        Assert.Equal(["5.3", "5.4", "5.5"], api.Series.Select(s => s.ToString()));
        Assert.False(api.IsLts(5, 3)); // only the exact text "LTS" counts, as upstream
        Assert.True(api.IsLts(5, 5));
        Assert.Empty(ApiData.Parse("{}").Series);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"blender_versions": ["5.2"]}""")]
    public void Malformed_files_fail_to_parse(string json)
    {
        Assert.False(ApiData.TryParse(json, out var data));
        Assert.Same(ApiData.Empty, data);
    }

    [Theory]
    [InlineData("29.10", "29.9", 1)] // float("29.10") < float("29.9") upstream
    [InlineData("29.0", "29", 0)]
    [InlineData("3.0", "29.0", -1)]
    public void File_versions_compare_as_integers(string a, string b, int expected)
    {
        Assert.True(ApiFileVersion.TryParse(a, out var va));
        Assert.True(ApiFileVersion.TryParse(b, out var vb));
        Assert.Equal(expected, Math.Sign(va.CompareTo(vb)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    [InlineData("-1")]
    [InlineData("1.")]
    public void Invalid_file_versions(string? text) => Assert.False(ApiFileVersion.TryParse(text, out _));

    [Theory]
    [InlineData("29.9", "29.10", true)]
    [InlineData("29.0", "29.0", false)]
    [InlineData(null, "29.0", true)]   // nothing stored yet
    [InlineData("junk", "29.0", true)] // upstream overwrites when the stored version cannot be read
    [InlineData("29.0", null, false)]
    public void Stored_cache_is_replaced_only_by_a_newer_one(string? stored, string? incoming, bool expected) =>
        Assert.Equal(expected, ApiFileVersion.ShouldReplace(stored, incoming));

    [Fact]
    public void Bumps_and_text()
    {
        Assert.Equal("29.10", new ApiFileVersion(29, 9).NextMinor().ToString());
        Assert.Equal("30.0", new ApiFileVersion(29, 9).NextMajor().ToString());
    }
}
