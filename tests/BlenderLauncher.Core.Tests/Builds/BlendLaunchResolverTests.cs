using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Tests.Builds;

public class BlendLaunchResolverTests
{
    private static readonly VersionSearchCandidate[] Builds =
    [
        B("4.2.3", "lts", 2024, 10, 1, @"stable\blender-4.2.3-lts"),
        B("4.2.1", "lts", 2024, 8, 20, @"stable\blender-4.2.1-lts"),
        B("4.3.0", "daily", 2024, 7, 30, @"daily\blender-4.3.0"),
        B("3.6.14", "lts", 2024, 7, 16, @"stable\blender-3.6.14-lts"),
        B("4.4.0", "daily", 2024, 11, 1, @"daily\blender-4.4.0"),
    ];

    private static VersionSearchCandidate B(string version, string branch, int y, int m, int d, string folder) =>
        new(SemVer.Parse(version), branch, "", new DateTimeOffset(y, m, d, 0, 0, 0, TimeSpan.Zero), folder);

    private static BlendFileHeader Header(int major, int minor) => new(major, minor, 1, BlendCompression.Zstd);

    [Fact]
    public void Saved_query_key_is_the_python_version_string() => Assert.Equal("4.2.0", BlendLaunchResolver.SavedQueryKey(4, 2));

    [Fact]
    public void Header_without_saved_query_uses_major_minor_newest()
    {
        var r = BlendLaunchResolver.Resolve(Builds, Header(4, 2));
        Assert.Equal(BlendLaunchSource.FileVersion, r.Source);
        Assert.Equal("4.2.^", r.Query.ToString());
        Assert.Equal([Builds[0]], r.Matches);
    }

    [Fact]
    public void Saved_query_for_the_file_version_wins()
    {
        var saved = new Dictionary<string, VersionSearchQuery> { ["4.2.0"] = VersionSearchQuery.Parse("4.2.-") };
        var r = BlendLaunchResolver.Resolve(Builds, Header(4, 2), saved);
        Assert.Equal(BlendLaunchSource.SavedQuery, r.Source);
        Assert.Equal([Builds[1]], r.Matches);
    }

    [Fact]
    public void Saved_query_for_another_version_is_ignored()
    {
        var saved = new Dictionary<string, VersionSearchQuery> { ["3.6.0"] = VersionSearchQuery.Any() };
        Assert.Equal(BlendLaunchSource.FileVersion, BlendLaunchResolver.Resolve(Builds, Header(4, 2), saved).Source);
    }

    [Fact]
    public void Explicit_query_wins_over_the_file()
    {
        var saved = new Dictionary<string, VersionSearchQuery> { ["4.2.0"] = VersionSearchQuery.Any() };
        var r = BlendLaunchResolver.Resolve(Builds, Header(4, 2), saved, VersionSearchQuery.Parse("3.^"));
        Assert.Equal(BlendLaunchSource.ExplicitQuery, r.Source);
        Assert.Equal([Builds[3]], r.Matches);
    }

    [Fact]
    public void Explicit_query_without_matches_does_not_fall_back()
    {
        var r = BlendLaunchResolver.Resolve(Builds, null, explicitQuery: VersionSearchQuery.Parse("9.9"), defaultBuildFolder: Builds[2].Folder);
        Assert.Equal(BlendLaunchSource.ExplicitQuery, r.Source);
        Assert.Empty(r.Matches);
    }

    [Fact]
    public void Unreadable_header_uses_the_default_build()
    {
        var r = BlendLaunchResolver.Resolve(Builds, null, defaultBuildFolder: @"daily\blender-4.3.0");
        Assert.Equal(BlendLaunchSource.DefaultBuild, r.Source);
        Assert.Equal([Builds[2]], r.Matches);
        Assert.Equal(@"daily\blender-4.3.0", r.Query.Folder);
    }

    [Fact]
    public void Unreadable_header_without_default_build_uses_the_default_query()
    {
        var r = BlendLaunchResolver.Resolve(Builds, null);
        Assert.Equal(BlendLaunchSource.DefaultQuery, r.Source);
        Assert.Equal(VersionSearchQuery.Default(), r.Query);
        Assert.Equal([Builds[4]], r.Matches);
    }

    [Fact]
    public void No_match_for_the_file_version_uses_the_default_build()
    {
        var r = BlendLaunchResolver.Resolve(Builds, Header(2, 79), defaultBuildFolder: @"stable\blender-3.6.14-lts");
        Assert.Equal(BlendLaunchSource.DefaultBuild, r.Source);
        Assert.Equal([Builds[3]], r.Matches);
    }

    [Fact]
    public void No_match_and_no_default_build_uses_the_default_query()
    {
        var r = BlendLaunchResolver.Resolve(Builds, Header(2, 79));
        Assert.Equal(BlendLaunchSource.DefaultQuery, r.Source);
        Assert.Equal([Builds[4]], r.Matches);
    }

    [Fact]
    public void Saved_query_without_matches_also_falls_back()
    {
        var saved = new Dictionary<string, VersionSearchQuery> { ["4.2.0"] = VersionSearchQuery.Parse("4.2.*-daily") };
        var r = BlendLaunchResolver.Resolve(Builds, Header(4, 2), saved, defaultBuildFolder: @"daily\blender-4.3.0");
        Assert.Equal(BlendLaunchSource.DefaultBuild, r.Source);
    }

    [Fact]
    public void Default_build_that_is_not_installed_uses_the_default_query()
    {
        var r = BlendLaunchResolver.Resolve(Builds, null, defaultBuildFolder: @"daily\gone");
        Assert.Equal(BlendLaunchSource.DefaultQuery, r.Source);
        Assert.Equal([Builds[4]], r.Matches);
    }

    [Fact]
    public void Generic_overload_returns_the_items()
    {
        var items = Builds.Select((b, i) => (Index: i, Build: b)).ToArray();
        var r = BlendLaunchResolver.Resolve(items, x => x.Build, Header(4, 3));
        Assert.Equal([2], r.Matches.Select(x => x.Index));
    }

    [Fact]
    public void Nothing_installed_gives_no_matches()
    {
        var r = BlendLaunchResolver.Resolve([], Header(4, 2), defaultBuildFolder: "x");
        Assert.Equal(BlendLaunchSource.DefaultQuery, r.Source);
        Assert.Empty(r.Matches);
    }
}
