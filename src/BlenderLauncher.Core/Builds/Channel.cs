using System.Text.RegularExpressions;

namespace BlenderLauncher.Core.Builds;

/// <summary>
/// Where a build comes from. Upstream has no such type; it keys everything off branch strings and library
/// subfolders, which <see cref="Channels"/> maps to and from.
/// </summary>
public enum Channel
{
    /// <summary>Stable releases from download.blender.org (branch <c>stable</c>, folder <c>stable</c>).</summary>
    Stable,

    /// <summary>Stable releases of an LTS series (branch <c>lts</c>, shares the <c>stable</c> folder).</summary>
    Lts,

    /// <summary>Daily builds from builder.blender.org (branch and folder <c>daily</c>).</summary>
    Daily,

    /// <summary>Experimental branch builds (branch <c>experimental</c> or, once installed, the branch name; folder <c>experimental</c>).</summary>
    Experimental,

    /// <summary>Pull-request builds (branch <c>patch</c> or, once installed, <c>PR12345</c>; folder <c>experimental</c>).</summary>
    Patch,

    /// <summary>Bforartists, stable and dev (branch and folder <c>bforartists</c>).</summary>
    Bforartists,

    /// <summary>UPBGE releases (branch and folder <c>upbge-stable</c>).</summary>
    UpbgeStable,

    /// <summary>UPBGE weekly builds (branch and folder <c>upbge-weekly</c>).</summary>
    UpbgeWeekly,

    /// <summary>User-added builds (folder <c>custom</c>; the branch is the folder or archive name).</summary>
    Custom,
}

/// <summary>Mapping between <see cref="Channel"/>, upstream's branch strings and the library's subfolders.</summary>
public static partial class Channels
{
    /// <summary>Every library subfolder upstream creates, in its order.</summary>
    public static IReadOnlyList<string> LibraryFolders { get; } =
        ["stable", "daily", "experimental", "bforartists", "upbge-stable", "upbge-weekly", "custom", "template", ".temp"];

    /// <summary>
    /// The branch string a scraper gives builds of <paramref name="channel"/>; null for <see cref="Channel.Custom"/>,
    /// whose branch is the build's own name.
    /// </summary>
    public static string? BranchOf(Channel channel) => channel switch
    {
        Channel.Stable => "stable",
        Channel.Lts => "lts",
        Channel.Daily => "daily",
        Channel.Experimental => "experimental",
        Channel.Patch => "patch",
        Channel.Bforartists => "bforartists",
        Channel.UpbgeStable => "upbge-stable",
        Channel.UpbgeWeekly => "upbge-weekly",
        Channel.Custom => null,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
    };

    /// <summary>The library subfolder builds of <paramref name="channel"/> are installed into.</summary>
    public static string LibraryFolderOf(Channel channel) => channel switch
    {
        Channel.Stable or Channel.Lts => "stable",
        Channel.Daily => "daily",
        Channel.Experimental or Channel.Patch => "experimental",
        Channel.Bforartists => "bforartists",
        Channel.UpbgeStable => "upbge-stable",
        Channel.UpbgeWeekly => "upbge-weekly",
        Channel.Custom => "custom",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
    };

    /// <summary>
    /// Upstream's download destination (<c>download_widget.py:226-245</c>): the subfolder a
    /// downloaded build of <paramref name="branch"/> is extracted into. Any branch not listed goes to
    /// <c>experimental</c>.
    /// </summary>
    public static string DownloadFolderForBranch(string branch) => branch switch
    {
        "stable" or "lts" => "stable",
        "daily" => "daily",
        "bforartists" => "bforartists",
        "upbge-stable" => "upbge-stable",
        "upbge-weekly" => "upbge-weekly",
        _ => "experimental",
    };

    /// <summary>
    /// The channel of a build with <paramref name="branch"/>. When known, <paramref name="libraryFolder"/> (the
    /// subfolder an installed build sits in) settles cases the branch alone cannot: anything in <c>custom</c> is
    /// <see cref="Channel.Custom"/>. Otherwise the canonical branch strings map directly, a branch like
    /// <c>PR12345</c> (any case) is <see cref="Channel.Patch"/>, and any other branch is
    /// <see cref="Channel.Experimental"/>, where upstream installs it.
    /// </summary>
    public static Channel FromBranch(string branch, string? libraryFolder = null)
    {
        if (string.Equals(libraryFolder, "custom", StringComparison.Ordinal))
        {
            return Channel.Custom;
        }

        return branch switch
        {
            "stable" => Channel.Stable,
            "lts" => Channel.Lts,
            "daily" => Channel.Daily,
            "experimental" => Channel.Experimental,
            "patch" => Channel.Patch,
            "bforartists" => Channel.Bforartists,
            "upbge-stable" => Channel.UpbgeStable,
            "upbge-weekly" => Channel.UpbgeWeekly,
            _ when PullRequestBranch().IsMatch(branch) => Channel.Patch,
            _ => Channel.Experimental,
        };
    }

    /// <summary>
    /// The channel a library subfolder holds, or null when it holds no builds (<c>template</c>, <c>.temp</c>,
    /// anything unknown). <c>stable</c> gives <see cref="Channel.Stable"/>; whether a build there is LTS comes
    /// from its branch. <c>experimental</c> gives <see cref="Channel.Experimental"/>; use
    /// <see cref="FromBranch"/> to tell patch builds apart.
    /// </summary>
    public static Channel? FromLibraryFolder(string folder) => folder switch
    {
        "stable" => Channel.Stable,
        "daily" => Channel.Daily,
        "experimental" => Channel.Experimental,
        "bforartists" => Channel.Bforartists,
        "upbge-stable" => Channel.UpbgeStable,
        "upbge-weekly" => Channel.UpbgeWeekly,
        "custom" => Channel.Custom,
        _ => null,
    };

    /// <summary>True for the UPBGE channels (upstream tests <c>branch.startswith("upbge")</c>).</summary>
    public static bool IsUpbge(Channel channel) => channel is Channel.UpbgeStable or Channel.UpbgeWeekly;

    [GeneratedRegex("^pr[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PullRequestBranch();
}
