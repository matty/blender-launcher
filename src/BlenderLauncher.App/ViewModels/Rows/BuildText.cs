using System.Globalization;
using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.App.ViewModels.Rows;

/// <summary>
/// The design's three text fields for a build (name, label, branch), derived from <see cref="BuildInfo"/>. The design
/// shows the git branch ("main", "blender-v5.3-release", "npr-prototype") where Core keeps the channel in
/// <see cref="BuildInfo.Branch"/>; the git branch is recovered from what the scrapers keep: the release tag for
/// releases, the release cycle for daily builds (alpha builds come from main, beta and RC from the release branch),
/// and the prerelease field, which holds the branch for experimental and the PR for patch builds.
/// </summary>
public static class BuildText
{
    /// <summary>"Blender 5.2 LTS", "Blender 5.4 Alpha", "Bforartists 4.5", "UPBGE 0.50", or the custom name.</summary>
    public static string NameOf(BuildInfo build, Channel channel)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (build.CustomName.Length > 0)
        {
            return build.CustomName;
        }

        if (build.IsBfaDev)
        {
            return "Bforartists Dev";
        }

        var product = channel switch
        {
            Channel.Bforartists => "Bforartists",
            Channel.UpbgeStable or Channel.UpbgeWeekly => "UPBGE",
            _ => "Blender",
        };
        var series = build.SemVersion is { } v
            ? string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}")
            : build.Subversion;

        var suffix = channel switch
        {
            Channel.Lts => " LTS",
            Channel.UpbgeWeekly => " Weekly",
            _ => LabelOf(build, channel) is { Length: > 0 } label ? " " + label : "",
        };
        return product + " " + series + suffix;
    }

    /// <summary>The prerelease label beside the version: "Alpha", "Beta", "RC" for daily builds; empty otherwise.</summary>
    public static string LabelOf(BuildInfo build, Channel channel)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (channel != Channel.Daily || build.SemVersion?.Prerelease is not { Length: > 0 } cycle)
        {
            return "";
        }

        var head = cycle.Split(['.', ' ', '|'], 2)[0].Trim();
        return head.ToLowerInvariant() switch
        {
            "rc" or "candidate" => "RC",
            "" => "",
            var other => char.ToUpperInvariant(other[0]) + other[1..],
        };
    }

    /// <summary>"v5.2.2" for releases, "main" or "blender-v5.3-release" for daily builds, the branch for experimental ones.</summary>
    public static string BranchOf(BuildInfo build, Channel channel)
    {
        ArgumentNullException.ThrowIfNull(build);
        var version = build.SemVersion;
        switch (channel)
        {
            case Channel.Stable or Channel.Lts or Channel.Bforartists or Channel.UpbgeStable or Channel.UpbgeWeekly:
                return version is { } v ? string.Create(CultureInfo.InvariantCulture, $"v{v.Major}.{v.Minor}.{v.Patch}") : build.Subversion;
            case Channel.Daily:
                if (version is not { } d)
                {
                    return build.Branch;
                }

                var cycle = d.Prerelease?.Split(['.', ' ', '|'], 2)[0].Trim().ToLowerInvariant();
                return cycle is null or "alpha"
                    ? "main"
                    : string.Create(CultureInfo.InvariantCulture, $"blender-v{d.Major}.{d.Minor}-release");
            case Channel.Experimental or Channel.Patch:
                if (version?.Prerelease is { Length: > 0 } pre)
                {
                    var bar = pre.IndexOf('|', StringComparison.Ordinal);
                    return (bar >= 0 ? pre[..bar] : pre).Trim();
                }

                return build.Branch;
            default:
                return build.Branch;
        }
    }
}
