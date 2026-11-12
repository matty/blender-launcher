using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.Core.Tests.Builds;

/// <summary>
/// An installed pull-request build (branch <c>PR12345</c>, from its archive name) is
/// updated by a scraped <c>patch</c> build of the same pull request (prerelease <c>PR12345</c>), and by no other.
/// </summary>
public sealed class PullRequestUpdateTests
{
    private static readonly IsoTimestamp T1 = IsoTimestamp.Parse("2026-01-01T00:00:00+00:00");
    private static readonly IsoTimestamp T2 = IsoTimestamp.Parse("2026-02-01T00:00:00+00:00");

    private static BuildInfo B(string subversion, string branch, string hash, IsoTimestamp time) => new()
    {
        Link = $"https://example.com/{branch}-{subversion}-{hash}.zip",
        Subversion = subversion,
        BuildHash = hash,
        CommitTime = time,
        Branch = branch,
    };

    [Fact]
    public void An_installed_PR_build_is_updated_by_a_newer_patch_build_of_the_same_PR()
    {
        var installed = B("4.4.0-PR12345", "PR12345", "aaaa", T1);
        var offered = new AvailableBuild(B("4.4.0-PR12345", "patch", "bbbb", T2));
        var other = new AvailableBuild(B("4.4.0-PR99999", "patch", "cccc", T2));

        var pick = BuildUpdateRules.FindCandidate(installed, [other, offered], [installed], UpdateBehaviour.Major);

        Assert.Same(offered, pick);
    }

    [Fact]
    public void Case_and_the_any_architecture_suffix_do_not_matter()
    {
        var installed = B("4.4.0-pr12345", "pr12345", "aaaa", T1);
        var offered = B("4.4.0-PR12345 | x86_64", "patch", "bbbb", T2);

        Assert.True(BuildUpdateRules.IsSameLine(installed, offered));
    }

    [Theory]
    [InlineData("PR99999", "patch")] // another pull request
    [InlineData("PR12345", "experimental")] // not a patch download
    [InlineData("alpha", "patch")] // a patch download without a pull-request number
    public void Other_downloads_are_not_on_the_line(string prerelease, string branch)
    {
        var installed = B("4.4.0-PR12345", "PR12345", "aaaa", T1);
        Assert.False(BuildUpdateRules.IsSameLine(installed, B("4.4.0-" + prerelease, branch, "bbbb", T2)));
    }

    [Fact]
    public void The_same_commit_or_an_older_one_is_no_update()
    {
        var installed = B("4.4.0-PR12345", "PR12345", "aaaa", T2);
        Assert.Null(BuildUpdateRules.FindCandidate(installed, [new AvailableBuild(B("4.4.0-PR12345", "patch", "aaaa", T2))], [installed], UpdateBehaviour.Major));
        Assert.Null(BuildUpdateRules.FindCandidate(installed, [new AvailableBuild(B("4.4.0-PR12345", "patch", "bbbb", T1))], [installed], UpdateBehaviour.Major));
    }
}
