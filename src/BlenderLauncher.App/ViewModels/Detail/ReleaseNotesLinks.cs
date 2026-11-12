using System.Globalization;
using System.Text.RegularExpressions;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Scraping.PullRequests;

namespace BlenderLauncher.App.ViewModels.Detail;

/// <summary>What "Show release notes" offers for a build (upstream <c>library_widget.py</c> menu entry 16).</summary>
public enum ReleaseNotesKind
{
    /// <summary>No entry: no page is known for the build.</summary>
    None,

    /// <summary>"Show Release Notes": stable, LTS, daily, Bforartists, UPBGE.</summary>
    ReleaseNotes,

    /// <summary>"Show Patch Details": an old Phabricator differential (<c>D12345</c>).</summary>
    PatchDetails,

    /// <summary>"Show PR Details" (and "Fetch PR name"): a pull request (<c>PR12345</c>).</summary>
    PullRequest,
}

/// <summary>The page behind "Show release notes", "Show patch details" and "Show PR details".</summary>
public static partial class ReleaseNotesLinks
{
    /// <summary>The kind of page and its address for <paramref name="build"/> of <paramref name="channel"/>.</summary>
    public static (ReleaseNotesKind Kind, string? Url) For(BuildInfo build, Channel channel)
    {
        ArgumentNullException.ThrowIfNull(build);
        var version = build.SemVersion;
        switch (channel)
        {
            case Channel.Stable or Channel.Lts or Channel.Daily when version is { } v:
                return (ReleaseNotesKind.ReleaseNotes,
                    string.Create(CultureInfo.InvariantCulture, $"https://developer.blender.org/docs/release_notes/{v.Major}.{v.Minor}/"));
            case Channel.Bforartists:
                return (ReleaseNotesKind.ReleaseNotes, "https://github.com/Bforartists/Bforartists/releases");
            case Channel.UpbgeStable or Channel.UpbgeWeekly:
                return (ReleaseNotesKind.ReleaseNotes, "https://github.com/UPBGE/upbge/releases");
        }

        var text = build.Branch + " " + build.Subversion;
        if (PullRequestLabels.TryGetNumber(build, out var pr))
        {
            return (ReleaseNotesKind.PullRequest,
                string.Create(CultureInfo.InvariantCulture, $"https://projects.blender.org/blender/blender/pulls/{pr}"));
        }

        if (Differential().Match(text) is { Success: true } d)
        {
            return (ReleaseNotesKind.PatchDetails, "https://archive.blender.org/developer/" + d.Value);
        }

        return (ReleaseNotesKind.None, null);
    }

    /// <summary>The pull-request number of a build, for "Fetch PR name".</summary>
    public static int? PullRequestOf(BuildInfo build) =>
        PullRequestLabels.TryGetNumber(build, out var number) ? number : null;

    [GeneratedRegex(@"D\d{5}", RegexOptions.CultureInvariant)]
    private static partial Regex Differential();
}
