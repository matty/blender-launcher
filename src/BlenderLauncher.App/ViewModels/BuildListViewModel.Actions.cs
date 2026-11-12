using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.ViewModels;

/// <summary>What the lists' buttons, clicks, keys and drops do. Everything goes through <see cref="MainWindowViewModel.Actions"/>.</summary>
public sealed partial class BuildListViewModel
{
    /// <summary>The file extensions a drop on an installed row accepts (upstream: <c>.blend</c> and <c>.blend1</c>–<c>.blend4</c>).</summary>
    private static readonly string[] BlendExtensions = [".blend", ".blend1", ".blend2", ".blend3", ".blend4"];

    /// <summary>Launch (Shift or "open previous" mode: <see cref="LaunchMode.OpenLast"/>).</summary>
    public void Launch(InstalledBuildRowViewModel row, bool openPrevious = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsRecognised)
        {
            Shell.Actions.Launch(row, openPrevious ? LaunchMode.OpenLast : null);
        }
    }

    /// <summary>The download row's "Installed" + Launch.</summary>
    public void LaunchInstalled(AvailableBuildRowViewModel row, bool openPrevious = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.InstalledRow is { } installed)
        {
            Launch(installed, openPrevious);
        }
    }

    /// <summary>The star: sets or clears the favourite flag.</summary>
    public void ToggleFavourite(InstalledBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsRecognised)
        {
            _ = Shell.Actions.SetFavouriteAsync(row, !row.IsFavourite);
        }
    }

    public void Download(AvailableBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Shell.Actions.Download(row);
    }

    public void CancelDownload(AvailableBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Shell.Actions.CancelDownload(row);
    }

    /// <summary>
    /// Double-click or Enter. Installed: launch (Shift: open previous), "Initialise" for an unrecognised folder.
    /// Download: start the download (upstream's double-click), or show the installed copy when there is one.
    /// </summary>
    public void Activate(BuildRowViewModel row, bool shift = false)
    {
        switch (row)
        {
            case InstalledBuildRowViewModel { IsUnrecognised: true } unrecognised:
                Initialise(unrecognised);
                break;
            case InstalledBuildRowViewModel installed:
                Launch(installed, shift);
                break;
            case AvailableBuildRowViewModel { InstalledRow: { } copy }:
                Shell.ShowInstalledBuild(copy);
                break;
            case AvailableBuildRowViewModel { IsIdle: true } available:
                Download(available);
                break;
        }
    }

    public void Initialise(InstalledBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        // Initialising writes a .blinfo through the custom build dialog, the same editor as "Edit build…".
        _ = Shell.Actions.EditBuildAsync(row);
    }

    /// <summary>"Remove" on a damaged row, and Delete on the selection (Shift inverts the delete-versus-trash setting).</summary>
    public Task RemoveAsync(InstalledBuildRowViewModel row, bool invertDeleteSetting = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Shell.Actions.RemoveAsync(row, invertDeleteSetting);
    }

    /// <summary>The Delete key: removes every selected installed row (one confirmation; Shift inverts delete versus trash).</summary>
    public Task RemoveSelectedAsync(bool invertDeleteSetting = false)
    {
        IReadOnlyList<InstalledBuildRowViewModel> rows = SelectedInstalledRows.Count > 0 ? SelectedInstalledRows
            : SelectedInstalledItem is { } single ? [single]
            : [];
        return rows.Count == 0 ? Task.CompletedTask : Shell.Actions.RemoveManyAsync([.. rows], invertDeleteSetting);
    }

    /// <summary>Whether a drop of <paramref name="paths"/> on <paramref name="row"/> would launch anything.</summary>
    public static bool CanOpenFiles(InstalledBuildRowViewModel row, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(paths);
        return row.IsRecognised && paths.Any(IsBlendFile);
    }

    /// <summary>Opens each dropped <c>.blend</c> file with <paramref name="row"/>'s build.</summary>
    public void OpenFiles(InstalledBuildRowViewModel row, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(paths);
        if (!row.IsRecognised)
        {
            return;
        }

        foreach (var path in paths.Where(IsBlendFile))
        {
            Shell.Actions.Launch(row, LaunchMode.WithBlendFile(path));
        }
    }

    /// <summary>"Browse downloads": the download view of the same channel (Stable from "All builds" and "Favorites").</summary>
    public void BrowseDownloads()
    {
        var key = Shell.CurrentChannel is { } channel and not Channel.Custom ? DisplayFormat.ChannelKey(channel) : "stable";
        Shell.Navigate(SidebarSection.Download, key);
    }

    /// <summary>"Check for builds" (Shift: a forced check of every visible channel) and "Try again".</summary>
    public void CheckForBuilds(bool forced = false)
    {
        if (Shell.CheckForBuildsCommand.CanExecute(forced))
        {
            Shell.CheckForBuildsCommand.Execute(forced);
        }
    }

    private static bool IsBlendFile(string path) =>
        BlendExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
