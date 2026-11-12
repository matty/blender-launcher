using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.ViewModels.Detail;
using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.Core.Builds;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlenderLauncher.App.ViewModels;

/// <summary>Which entries a <see cref="BuildContextMenuViewModel"/> holds.</summary>
public enum BuildMenuScope
{
    /// <summary>The row context menu: every action, in upstream's order.</summary>
    ContextMenu,

    /// <summary>
    /// The detail panel's More section: the actions the design has no place for. Launch, favourite, default, portable
    /// and remove are left out because the panel shows them already.
    /// </summary>
    MoreSection,
}

/// <summary>
/// The row context menu and the detail panel's More section: the build actions for one row, or the
/// delete-only menu for a multi-selection, as <see cref="Entries"/>. Built per right-click (or per selection, for the
/// panel); every entry calls <see cref="MainWindowViewModel.Actions"/> so the menu, the panel and the lists behave
/// alike.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Installed rows: upstream's order. Entries whose platform service is absent are not created (Launch and Debug
/// without a process launcher; Register, Shortcut and Symlink off Windows; release notes when no page is known; Fetch
/// PR name for builds that are not pull requests; Copy hash without a hash; Update without an update).</item>
/// <item>Unrecognised or damaged folders: Show build folder and the delete entry only.</item>
/// <item>Downloads: Download or Cancel, Show in library when installed, Copy hash, Show release notes. An installed
/// download shows the installed row's full menu instead, as upstream.</item>
/// <item>More than one installed row: the delete entry only, for all of them.</item>
/// <item>Delete versus trash follows the default-delete setting with <see cref="IsShiftHeld"/> inverting it, and
/// Show config folder opens the user folder instead of the portable one while Shift is held; both headers follow
/// <see cref="IsShiftHeld"/> while the menu is open.</item>
/// </list>
/// </remarks>
public sealed partial class BuildContextMenuViewModel : ViewModelBase
{
    private readonly List<InstalledBuildRowViewModel> _targets;
    private BuildMenuEntry? _remove;
    private BuildMenuEntry? _config;
    private bool _configHasPortable;

    public BuildContextMenuViewModel(MainWindowViewModel shell, BuildRowViewModel row)
        : this(shell, [row])
    {
    }

    public BuildContextMenuViewModel(MainWindowViewModel shell, IReadOnlyList<BuildRowViewModel> rows, BuildMenuScope scope = BuildMenuScope.ContextMenu)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            throw new ArgumentException("A menu needs at least one row.", nameof(rows));
        }

        Shell = shell;
        Row = rows[0];
        Scope = scope;
        _targets = [.. rows.OfType<InstalledBuildRowViewModel>()];

        if (rows.Count > 1)
        {
            Entries = _targets.Count > 0 ? [RemoveEntry()] : [];
        }
        else if (Row is InstalledBuildRowViewModel installed)
        {
            Entries = ForInstalled(installed);
        }
        else if (Row is AvailableBuildRowViewModel { InstalledRow: { } copy } && scope == BuildMenuScope.ContextMenu)
        {
            _targets = [copy];
            Entries = ForInstalled(copy);
        }
        else if (Row is AvailableBuildRowViewModel available)
        {
            Entries = ForDownload(available);
        }
        else
        {
            Entries = [];
        }
    }

    public MainWindowViewModel Shell { get; }

    /// <summary>The row the menu was opened on (the first of a multi-selection).</summary>
    public BuildRowViewModel Row { get; }

    public BuildMenuScope Scope { get; }

    /// <summary>The menu, in order; separators are never first, last or doubled.</summary>
    public IReadOnlyList<BuildMenuEntry> Entries { get; }

    /// <summary>Upstream does not open a menu without entries.</summary>
    public bool HasEntries => Entries.Count > 0;

    /// <summary>
    /// Shift (or Ctrl, as upstream) is held while the menu is open: inverts delete and trash, and Show config folder
    /// opens the user folder. The view updates it from key events; the actions also read the keys at click time.
    /// </summary>
    [ObservableProperty]
    public partial bool IsShiftHeld { get; set; }

    /// <summary>The More section's flat list: the Debug group becomes a label followed by its entries, nested.</summary>
    public IReadOnlyList<BuildMenuEntry> FlatEntries =>
        [.. Entries.Where(e => !e.IsSeparator).SelectMany(e => e.HasChildren ? [e, .. e.Children.Select(c => c.AsNested())] : new[] { e })];

    /// <summary>The entry with <paramref name="id"/>, searching groups too; null when absent.</summary>
    public BuildMenuEntry? Find(string id) =>
        Entries.SelectMany(e => e.HasChildren ? [e, .. e.Children] : new[] { e }).FirstOrDefault(e => e.Id == id);

    private BuildActions Actions => Shell.Actions;

    partial void OnIsShiftHeldChanged(bool value) => UpdateShiftHeaders();

    // ------------------------------------------------------------------ installed

    private List<BuildMenuEntry> ForInstalled(InstalledBuildRowViewModel row)
    {
        var menu = Scope == BuildMenuScope.ContextMenu;
        var list = new List<BuildMenuEntry>();
        if (!row.IsRecognised || row.Build is not { } build)
        {
            list.Add(Action(BuildMenuIds.BuildFolder, "act.a.folder_build", () => Actions.OpenFolder(row.Path)));
            if (menu)
            {
                list.Add(BuildMenuEntry.Separator());
                list.Add(RemoveEntry());
            }

            return Tidy(list);
        }

        if (Actions.CanLaunch)
        {
            if (menu)
            {
                list.Add(Action(BuildMenuIds.Launch, "act.launch", () => Actions.Launch(row, ModifierKeys.IsShiftDown ? LaunchMode.OpenLast : null)));
            }

            var openLast = Action(BuildMenuIds.OpenLast, "act.a.prev", () => Actions.Launch(row, LaunchMode.OpenLast));
            openLast.ToolTip = Loc.Tr("ui.menu.open_last_hint");
            list.Add(openLast);

            if (BuildActions.HasShellIntegration)
            {
                string[] keys = ["act.a.d.log", "act.a.d.factory", "act.a.d.gpu", "act.a.d.glitch"];
                string[] ids = [BuildMenuIds.DebugLog, BuildMenuIds.DebugFactory, BuildMenuIds.DebugGpu, BuildMenuIds.DebugGpuWorkaround];
                var children = new List<BuildMenuEntry>();
                for (var i = 0; i < ids.Length; i++)
                {
                    var script = LaunchArgsBuilder.DebugScripts[i];
                    children.Add(Action(ids[i], keys[i], () => Actions.LaunchDebug(row, script)));
                }

                list.Add(BuildMenuEntry.Group(BuildMenuIds.Debug, Loc.Tr("act.a.d.d"), children));
            }
        }

        if (menu)
        {
            list.Add(Action(BuildMenuIds.Favourite, row.IsFavourite ? "act.a.fav.rem" : "act.a.fav.add",
                () => _ = Actions.SetFavouriteAsync(row, !row.IsFavourite)));
            var useDefault = Action(BuildMenuIds.Default, "ui.detail.option.use_as_default",
                () =>
                {
                    if (row.IsDefault)
                    {
                        Actions.ClearDefault(row);
                    }
                    else
                    {
                        Actions.SetDefault(row);
                    }
                });
            useDefault.IsChecked = row.IsDefault;
            list.Add(useDefault);
        }

        if (menu && row.Update is { } update)
        {
            var entry = Action(BuildMenuIds.Update, "act.a.update", () => _ = Actions.UpdateAsync(row));
            entry.Header = Loc.Tr("ui.menu.update_to", ("version", update.Candidate.Build.DisplayVersion));
            entry.ToolTip = Loc.Tr("act.a.update_tooltip");
            list.Add(entry);
        }

        list.Add(BuildMenuEntry.Separator());
        if (BuildActions.HasShellIntegration)
        {
            var register = Action(BuildMenuIds.Register, "act.a.register", () => Actions.RegisterExtension(row));
            register.ToolTip = Loc.Tr("act.a.register_tooltip");
            list.Add(register);
            list.Add(Action(BuildMenuIds.Shortcut, "ui.menu.shortcut", () => _ = Actions.CreateShortcutAsync(row)));
            var symlink = Action(BuildMenuIds.Symlink, "act.a.symlink", () => Actions.CreateSymlink(row));
            symlink.IsEnabled = !Actions.SymlinkPointsAt(row);
            symlink.ToolTip = Loc.Tr("ui.menu.symlink_hint");
            list.Add(symlink);
        }

        var template = Action(BuildMenuIds.Template, "act.a.template", () => _ = Actions.InstallTemplateAsync(row));
        template.IsEnabled = !row.IsRunning;
        list.Add(template);
        if (menu)
        {
            list.Add(Action(BuildMenuIds.Portable, row.IsPortable ? "act.a.port.rem" : "act.a.port.add",
                () => _ = Actions.SetPortableAsync(row, !row.IsPortable)));
        }

        if (!string.IsNullOrEmpty(row.Hash))
        {
            list.Add(Action(BuildMenuIds.CopyHash, "act.a.hash", () => _ = Actions.CopyHashAsync(row)));
        }

        list.Add(Action(BuildMenuIds.Freeze, row.IsFrozen ? "act.a.freeze.rem" : "act.a.freeze.add",
            () => _ = Actions.SetFrozenAsync(row, !row.IsFrozen)));

        list.Add(BuildMenuEntry.Separator());
        AddReleaseNotes(list, row, build);
        if (ReleaseNotesLinks.PullRequestOf(build) is not null)
        {
            list.Add(Action(BuildMenuIds.FetchPrName, "act.a.fetch_pr_name", () => _ = Actions.FetchPrNameAsync(row)));
        }

        list.Add(Action(BuildMenuIds.BuildFolder, "act.a.folder_build", () => Actions.OpenFolder(row.Path)));
        _configHasPortable = row.IsPortable;
        _config = Action(BuildMenuIds.ConfigFolder, "act.a.config",
            () => _ = Actions.ShowConfigFolderAsync(row, preferUserFolder: IsShiftHeld || ModifierKeys.IsShiftDown));
        _config.ToolTip = Loc.Tr("ui.menu.config_hint");
        list.Add(_config);
        list.Add(Action(BuildMenuIds.Rename, "ui.menu.rename", () => _ = Actions.RenameAsync(row)));
        list.Add(Action(BuildMenuIds.Edit, "act.a.edit", () => _ = Actions.EditBuildAsync(row)));

        if (menu)
        {
            list.Add(BuildMenuEntry.Separator());
            list.Add(RemoveEntry());
        }

        UpdateShiftHeaders();
        return Tidy(list);
    }

    // ------------------------------------------------------------------ downloads

    private List<BuildMenuEntry> ForDownload(AvailableBuildRowViewModel row)
    {
        var list = new List<BuildMenuEntry>();
        if (Scope == BuildMenuScope.ContextMenu)
        {
            if (row.IsIdle)
            {
                list.Add(Action(BuildMenuIds.Download, "act.download", () => Actions.Download(row)));
            }
            else if (row.IsBusy)
            {
                var cancel = Action(BuildMenuIds.Cancel, "act.cancel", () => Actions.CancelDownload(row));
                cancel.IsEnabled = row.CanCancel;
                list.Add(cancel);
            }
            else if (row.InstalledRow is { } copy)
            {
                list.Add(Action(BuildMenuIds.ShowInLibrary, "ui.detail.show_in_library", () => Shell.ShowInstalledBuild(copy)));
            }

            list.Add(BuildMenuEntry.Separator());
        }

        if (!string.IsNullOrEmpty(row.Hash))
        {
            list.Add(Action(BuildMenuIds.CopyHash, "act.a.hash", () => _ = Actions.CopyHashAsync(row)));
        }

        if (row.Build is { } build)
        {
            AddReleaseNotes(list, row, build);
        }

        return Tidy(list);
    }

    // ------------------------------------------------------------------ shared

    private void AddReleaseNotes(List<BuildMenuEntry> list, BuildRowViewModel row, BuildInfo build)
    {
        var key = ReleaseNotesLinks.For(build, row.Channel).Kind switch
        {
            ReleaseNotesKind.ReleaseNotes => "act.a.release_notes",
            ReleaseNotesKind.PatchDetails => "act.a.release_notes_patch",
            ReleaseNotesKind.PullRequest => "act.a.release_notes_pr",
            _ => null,
        };
        if (key is not null)
        {
            list.Add(Action(BuildMenuIds.ReleaseNotes, key, () => Actions.ShowReleaseNotes(row)));
        }
    }

    private BuildMenuEntry RemoveEntry()
    {
        _remove = BuildMenuEntry.Action(BuildMenuIds.Remove, "", new RelayCommand(() =>
        {
            var invert = IsShiftHeld || ModifierKeys.IsShiftOrControlDown;
            _ = _targets.Count == 1 ? Actions.RemoveAsync(_targets[0], invert) : Actions.RemoveManyAsync(_targets, invert);
        }), isDanger: true);
        _remove.IsEnabled = _targets.Any(t => !t.IsRunning);
        UpdateShiftHeaders();
        return _remove;
    }

    private void UpdateShiftHeaders()
    {
        if (_remove is { } remove)
        {
            remove.Header = Loc.Tr(Actions.WillTrash(IsShiftHeld) ? "act.a.trash" : "act.a.delete");
        }

        if (_config is { } config)
        {
            config.Header = Loc.Tr(_configHasPortable && !IsShiftHeld ? "act.a.config_portable" : "act.a.config");
        }
    }

    private static BuildMenuEntry Action(string id, string key, Action run) =>
        BuildMenuEntry.Action(id, Loc.Tr(key), new RelayCommand(run));

    // No separator first, last or twice in a row.
    private static List<BuildMenuEntry> Tidy(List<BuildMenuEntry> list)
    {
        var result = new List<BuildMenuEntry>(list.Count);
        foreach (var entry in list)
        {
            if (entry.IsSeparator && (result.Count == 0 || result[^1].IsSeparator))
            {
                continue;
            }

            result.Add(entry);
        }

        while (result.Count > 0 && result[^1].IsSeparator)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }
}
