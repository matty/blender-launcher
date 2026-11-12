using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Scraping.PullRequests;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Builds;

/// <summary>An update found for one installed build.</summary>
/// <param name="BuildFolder">The installed build's folder.</param>
/// <param name="Installed">The installed build.</param>
/// <param name="Candidate">The download that updates it.</param>
/// <param name="IsMajorVersionUpdate">
/// Major or minor differ (upstream's <c>is_major_version_update</c>): the UI asks "Remove old / Keep both" once the
/// update is installed.
/// </param>
/// <param name="HasPortableSettings">The build has a portable config folder: the UI asks "Move to new / Remove / Cancel" first.</param>
/// <param name="ShowButton">
/// Upstream's <c>show_update_button</c>: whether the row's update button shows. Upstream computes the update even
/// when this is off (with the advanced per-channel switches on), and only hides the button.
/// </param>
public sealed record BuildUpdate(
    string BuildFolder,
    BuildInfo Installed,
    AvailableBuild Candidate,
    bool IsMajorVersionUpdate,
    bool HasPortableSettings,
    bool ShowButton);

/// <summary>
/// The rules of upstream's <c>modules/blender_update_manager.py</c>, as pure
/// functions.
/// </summary>
/// <remarks>
/// Differences from upstream:
/// <list type="bullet">
/// <item>Experimental branches (<c>Pr…</c>, <c>Npr…</c>) are recognised without regard to case, so an
/// installed <c>PR12345</c> build is eligible.</item>
/// <item>Builds whose version does not parse are skipped (upstream raises). Commit times where one is naive and the
/// other aware are compared with the naive one taken as local time (upstream raises).</item>
/// <item>Candidates must be on the installed build's line (<see cref="IsSameLine"/>): the same branch, as upstream,
/// or, for an installed pull-request build (branch <c>PR12345</c>, from the archive name), a <c>patch</c> download
/// of the same pull request. Upstream compared branches only, so installed patch builds never found an update.</item>
/// </list>
/// </remarks>
public static class BuildUpdateRules
{
    /// <summary>Upstream's <c>any(branch.startswith(p) for p in ["Pr", "Npr"])</c>, ignoring case.</summary>
    public static bool IsExperimentalBranch(string branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        return branch.StartsWith("pr", StringComparison.OrdinalIgnoreCase) || branch.StartsWith("npr", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Upstream's <c>_branch_visibility</c>: whether updates are looked for on <paramref name="branch"/>. With the
    /// advanced update button, the channel's own switch; otherwise <see cref="BuildsSettings.ShowUpdateButton"/>.
    /// Branches of no known channel (custom names) are never eligible.
    /// </summary>
    public static bool IsEligible(string branch, BuildsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(settings);
        bool Pick(bool channelSwitch) => settings.UseAdvancedUpdateButton ? channelSwitch : settings.ShowUpdateButton;
        return branch switch
        {
            "stable" or "lts" => Pick(settings.ShowStableUpdateButton),
            "daily" => Pick(settings.ShowDailyUpdateButton),
            _ when IsExperimentalBranch(branch) => Pick(settings.ShowExperimentalUpdateButton),
            "bforartists" => Pick(settings.ShowBforartistsUpdateButton),
            "upbge-stable" => Pick(settings.ShowUpbgeStableUpdateButton),
            "upbge-weekly" => Pick(settings.ShowUpbgeWeeklyUpdateButton),
            _ => false,
        };
    }

    /// <summary>
    /// Upstream's <c>_get_update_behavior</c>: the channel's behaviour with the advanced update button, otherwise
    /// (and for any other branch) <see cref="BuildsSettings.UpdateBehaviour"/>.
    /// </summary>
    public static UpdateBehaviour BehaviourFor(string branch, BuildsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.UseAdvancedUpdateButton)
        {
            return settings.UpdateBehaviour;
        }

        return branch switch
        {
            "stable" or "lts" => settings.StableUpdateBehaviour,
            "daily" => settings.DailyUpdateBehaviour,
            _ when IsExperimentalBranch(branch) => settings.ExperimentalUpdateBehaviour,
            "bforartists" => settings.BforartistsUpdateBehaviour,
            "upbge-stable" => settings.UpbgeStableUpdateBehaviour,
            "upbge-weekly" => settings.UpbgeWeeklyUpdateBehaviour,
            _ => settings.UpdateBehaviour,
        };
    }

    /// <summary>
    /// Upstream's <c>_new_version_available</c>: the download that updates <paramref name="current"/>, or null.
    /// <paramref name="installed"/> is every installed build (upstream's library list, the current build included).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A Bforartists dev build: the download with the latest commit time among dev downloads newer than the
    /// installed build and matching no installed build.</item>
    /// <item>Otherwise only downloads of the same branch, not dev builds, count. Installed dev builds are left out of
    /// the installed set (they carry the next release's version). A download is skipped when its hash is
    /// installed, or its version (prerelease dropped) is installed and it is not newer than the current build.</item>
    /// <item>A <b>better version</b> per <paramref name="behaviour"/>, measured against the highest installed version
    /// in scope: Major, any higher version; Minor, the same major with a higher minor, or the same minor and a
    /// higher patch; Patch, the same major and minor with a higher patch. The highest wins; among equals, the
    /// latest commit.</item>
    /// <item>Else a <b>newer build of the same version</b> (later commit time; for <c>daily</c> not with a lower
    /// prerelease), the latest one.</item>
    /// <item>A better version is preferred over a newer build.</item>
    /// </list>
    /// </remarks>
    public static AvailableBuild? FindCandidate(
        BuildInfo current,
        IEnumerable<AvailableBuild> available,
        IEnumerable<BuildInfo> installed,
        UpdateBehaviour behaviour)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(installed);
        var downloads = available as IReadOnlyCollection<AvailableBuild> ?? [.. available];
        var installedList = installed as IReadOnlyCollection<BuildInfo> ?? [.. installed];

        if (current.IsBfaDev)
        {
            AvailableBuild? newest = null;
            foreach (var download in downloads)
            {
                var b = download.Build;
                if (b.IsBfaDev && Later(b.CommitTime, current.CommitTime) && !installedList.Any(b.Matches)
                    && (newest is null || Later(b.CommitTime, newest.Build.CommitTime)))
                {
                    newest = download;
                }
            }

            return newest;
        }

        if (current.SemVersion is not { } currentFull)
        {
            return null;
        }

        var currentVersion = currentFull.Finalize();
        var others = installedList.Where(b => !b.IsBfaDev).ToList();
        var installedHashes = others.Select(b => b.BuildHash).Where(h => !string.IsNullOrEmpty(h)).ToHashSet(StringComparer.Ordinal);
        var installedVersions = others.Where(b => b.SemVersion is not null).Select(b => b.SemVersion!.Value.Finalize()).ToHashSet();

        AvailableBuild? bestVersionDownload = null;
        var bestVersion = new SemVer(0, 0, 0);
        AvailableBuild? bestHashDownload = null;
        IsoTimestamp? bestHashTime = null;

        foreach (var download in downloads)
        {
            var b = download.Build;
            if (!IsSameLine(current, b) || b.IsBfaDev || b.SemVersion is not { } full)
            {
                continue;
            }

            var version = full.Finalize();
            if (!string.IsNullOrEmpty(b.BuildHash) && installedHashes.Contains(b.BuildHash))
            {
                continue;
            }

            if (installedVersions.Contains(version) && !Later(b.CommitTime, current.CommitTime))
            {
                continue;
            }

            if (IsBetterVersion(version, currentVersion, installedVersions, behaviour))
            {
                var c = version.CompareTo(bestVersion);
                if (c > 0)
                {
                    bestVersion = version;
                    bestVersionDownload = download;
                }
                else if (c == 0 && (bestVersionDownload is null || Later(b.CommitTime, bestVersionDownload.Build.CommitTime)))
                {
                    bestVersionDownload = download;
                }
            }
            else if (version.CompareTo(currentVersion) == 0 && Later(b.CommitTime, current.CommitTime))
            {
                // Daily builds: a newer commit must not be a lower prerelease (beta after rc, for example).
                if (current.Branch == "daily" && full.CompareTo(currentFull) < 0)
                {
                    continue;
                }

                if (bestHashTime is not { } t || Later(b.CommitTime, t))
                {
                    bestHashTime = b.CommitTime;
                    bestHashDownload = download;
                }
            }
        }

        return bestVersionDownload ?? bestHashDownload;
    }

    /// <summary>
    /// Whether <paramref name="download"/> can update <paramref name="current"/>: the same branch, or an installed
    /// pull-request build (<c>PR12345</c>, any case) and a <c>patch</c> download whose pull-request number (read from
    /// its prerelease, <see cref="PullRequestLabels.TryGetNumber"/>) is the same.
    /// </summary>
    public static bool IsSameLine(BuildInfo current, BuildInfo download)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(download);
        if (string.Equals(download.Branch, current.Branch, StringComparison.Ordinal))
        {
            return true;
        }

        return download.Branch == "patch"
            && current.Branch != "patch"
            && PullRequestLabels.TryParseFromBranch(current.Branch, out var installed)
            && PullRequestLabels.TryGetNumber(download, out var offered)
            && installed == offered;
    }

    /// <summary>
    /// Upstream's <c>_is_better_version</c>. <paramref name="installedVersions"/> hold finalised versions.
    /// </summary>
    public static bool IsBetterVersion(SemVer download, SemVer current, IReadOnlyCollection<SemVer> installedVersions, UpdateBehaviour behaviour)
    {
        ArgumentNullException.ThrowIfNull(installedVersions);
        var zero = new SemVer(0, 0, 0);
        if (behaviour == UpdateBehaviour.Major)
        {
            var highest = installedVersions.DefaultIfEmpty(zero).Max();
            return download.CompareTo(highest) > 0;
        }

        if (download.Major != current.Major)
        {
            return false;
        }

        if (behaviour == UpdateBehaviour.Minor)
        {
            var highest = installedVersions.Where(v => v.Major == current.Major).DefaultIfEmpty(zero).Max();
            return download.Minor > highest.Minor || (download.Minor == highest.Minor && download.Patch > highest.Patch);
        }

        if (download.Minor != current.Minor)
        {
            return false;
        }

        if (behaviour == UpdateBehaviour.Patch)
        {
            var highest = installedVersions.Where(v => v.Major == current.Major && v.Minor == current.Minor).DefaultIfEmpty(zero).Max();
            return download.Patch > highest.Patch;
        }

        return false;
    }

    /// <summary>
    /// Upstream's <c>is_major_version_update</c>: major or minor differ (prerelease ignored). Never for Bforartists
    /// dev builds, whose downloaded versions are dates.
    /// </summary>
    public static bool IsMajorVersionUpdate(BuildInfo current, BuildInfo update)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(update);
        if (current.IsBfaDev || update.IsBfaDev || current.SemVersion is not { } a || update.SemVersion is not { } b)
        {
            return false;
        }

        return a.Major != b.Major || a.Minor != b.Minor;
    }

    /// <summary>True when <paramref name="a"/> is after <paramref name="b"/>; a naive time is taken as local time against an aware one.</summary>
    internal static bool Later(IsoTimestamp a, IsoTimestamp b) =>
        a.TryCompareTo(b, out var c) ? c > 0 : a.ToDateTimeOffset(TimeZoneInfo.Local) > b.ToDateTimeOffset(TimeZoneInfo.Local);
}

/// <summary>
/// Which installed builds have an update, recomputed from the available list after each
/// check and whenever the library changes. Frozen builds and builds in <c>custom</c> are skipped, as upstream
/// (<c>window.py:645-650</c>, <c>library_widget.py:528-557</c>). Thread-safe; <see cref="UpdatesChanged"/> is raised
/// on the calling thread.
/// </summary>
public sealed class BuildUpdateService
{
    private readonly Func<BuildsSettings> _settings;
    private readonly LibraryService _library;
    private readonly Log? _log;
    private readonly Lock _gate = new();
    private IReadOnlyDictionary<string, BuildUpdate> _current = new Dictionary<string, BuildUpdate>(LibraryLayout.PathComparer);
    private IReadOnlyList<AvailableBuild> _lastAvailable = [];

    public BuildUpdateService(Func<BuildsSettings> settings, LibraryService library, Log? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(library);
        _settings = settings;
        _library = library;
        _log = log;
    }

    /// <summary>The updates found by the last <see cref="Refresh(IReadOnlyList{AvailableBuild})"/>, by build folder.</summary>
    public IReadOnlyDictionary<string, BuildUpdate> Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Raised after a refresh that changed <see cref="Current"/>.</summary>
    public event EventHandler<IReadOnlyDictionary<string, BuildUpdate>>? UpdatesChanged;

    /// <summary>The update for <paramref name="buildFolder"/>, or null.</summary>
    public BuildUpdate? Get(string buildFolder) =>
        Current.TryGetValue(LibraryLayout.NormaliseRoot(buildFolder), out var update) ? update : null;

    /// <summary>Recomputes every installed build's update from <paramref name="available"/> (the scrape snapshot's builds).</summary>
    public IReadOnlyDictionary<string, BuildUpdate> Refresh(IReadOnlyList<AvailableBuild> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        var snapshot = _library.Current;
        var installed = snapshot.Builds.ToList();
        var result = new Dictionary<string, BuildUpdate>(LibraryLayout.PathComparer);
        foreach (var entry in snapshot.Entries)
        {
            if (FindUpdate(entry, available, installed) is { } update)
            {
                result[entry.Path] = update;
            }
        }

        bool changed;
        lock (_gate)
        {
            _lastAvailable = available;
            changed = !SameUpdates(_current, result);
            _current = result;
        }

        if (changed)
        {
            _log?.Info($"Updates available for {result.Count} installed build(s)");
            UpdatesChanged?.Invoke(this, result);
        }

        return result;
    }

    /// <summary>Recomputes with the available list of the last refresh (after a library change).</summary>
    public IReadOnlyDictionary<string, BuildUpdate> Refresh()
    {
        IReadOnlyList<AvailableBuild> available;
        lock (_gate)
        {
            available = _lastAvailable;
        }

        return Refresh(available);
    }

    /// <summary>
    /// Upstream's <c>check_for_updates</c> for one library entry: null for anything that is not a readable build, a
    /// build in <c>custom</c>, a frozen build, or a branch whose update button is off; otherwise the candidate from
    /// <see cref="BuildUpdateRules.FindCandidate"/> with the branch's behaviour.
    /// </summary>
    public BuildUpdate? FindUpdate(LibraryEntry entry, IReadOnlyList<AvailableBuild> available, IReadOnlyList<BuildInfo>? installed = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Build is not { } build || entry.ParentFolder == LibraryLayout.CustomFolderName || build.IsFrozen)
        {
            return null;
        }

        var settings = _settings();
        if (!BuildUpdateRules.IsEligible(build.Branch, settings))
        {
            return null;
        }

        var candidate = BuildUpdateRules.FindCandidate(build, available, installed ?? [.. _library.Current.Builds], BuildUpdateRules.BehaviourFor(build.Branch, settings));
        return candidate is null
            ? null
            : new BuildUpdate(
                entry.Path,
                build,
                candidate,
                BuildUpdateRules.IsMajorVersionUpdate(build, candidate.Build),
                HasPortableSettings(build),
                settings.ShowUpdateButton);
    }

    /// <summary>
    /// The download that would replace <paramref name="build"/> under replace-on-new-build: the same
    /// candidate rules and the branch's behaviour, without the update-button switches, which only govern the
    /// button. Null for frozen builds.
    /// </summary>
    public AvailableBuild? FindReplacement(BuildInfo build, IReadOnlyList<AvailableBuild> available)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (build.IsFrozen)
        {
            return null;
        }

        return BuildUpdateRules.FindCandidate(build, available, [.. _library.Current.Builds], BuildUpdateRules.BehaviourFor(build.Branch, _settings()));
    }

    /// <summary>True when the build has an enabled portable config folder (upstream's <c>make_portable_path().is_dir()</c>).</summary>
    public static bool HasPortableSettings(BuildInfo build) =>
        BuildConfigFolders.GetPortableState(build) == PortableState.Portable;

    /// <summary>"Move to new": copies the old build's portable settings into the new build (<see cref="BuildConfigFolders.CopyPortableSettingsAsync"/>).</summary>
    public static Task<PortableCopyResult> MovePortableSettingsAsync(BuildInfo oldBuild, BuildInfo newBuild, CancellationToken ct = default) =>
        BuildConfigFolders.CopyPortableSettingsAsync(oldBuild, newBuild, ct);

    private static bool SameUpdates(IReadOnlyDictionary<string, BuildUpdate> a, IReadOnlyDictionary<string, BuildUpdate> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var other) && other == p.Value);
}
