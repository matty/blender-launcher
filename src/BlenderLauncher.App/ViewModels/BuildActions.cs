using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.ViewModels.Detail;
using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.App.Views.Detail;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Pipeline;
using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Core.Scraping.PullRequests;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Platform.Windows.FileSystem;
using BlenderLauncher.Platform.Windows.Processes;
using BlenderLauncher.Platform.Windows.Shell;

namespace BlenderLauncher.App.ViewModels;

/// <summary>
/// The things a user can do to a build, shared by the lists, the detail panel and the context menu so each action
/// behaves the same wherever it is started. Each method checks its preconditions, calls the services, and reports
/// through the toast; the rows then update from the services' events (through <see cref="BuildCatalog"/>).
/// </summary>
/// <remarks>
/// Without services (sample data) the pure view-model actions are simulated so previews behave: launching, quitting,
/// favourite, default, the option check boxes and removal. Actions that need the file system or the network only
/// toast. Questions go through <see cref="Ui"/> (a <see cref="Services.DialogService"/> dialog on the desktop).
/// </remarks>
public sealed class BuildActions
{
    /// <summary>The library's junction to the chosen build (upstream's <c>bl_symlink</c>).</summary>
    public const string SymlinkName = "bl_symlink";

    private readonly BuildCatalog _catalog;
    private readonly ToastService _toasts;
    private AppServices? _services;

    public BuildActions(BuildCatalog catalog, ToastService toasts)
    {
        _catalog = catalog;
        _toasts = toasts;
    }

    /// <summary>The live services, or null with sample data.</summary>
    public AppServices? Services => _services;

    /// <summary>Prompts, clipboard, save picker and links; replaced in tests.</summary>
    public IBuildActionsUi Ui { get; set; } = DesktopActionsUi.Instance;

    /// <summary>Builds can be started (the platform has a process launcher, or sample data simulates it).</summary>
    public bool CanLaunch => _services?.Launches.IsAvailable ?? true;

    /// <summary>Shell integration exists: register extension, shortcuts, the junction (Windows only).</summary>
    public static bool HasShellIntegration => OperatingSystem.IsWindows();

    /// <summary>The default-delete setting: true when removal goes to the Recycle Bin (the default).</summary>
    public bool TrashByDefault =>
        (_services?.Settings.Current.General.DefaultDeleteAction ?? DeleteAction.SendToTrash) == DeleteAction.SendToTrash;

    /// <summary>Whether a removal goes to the Recycle Bin, with Shift (<paramref name="invert"/>) inverting the setting.</summary>
    public bool WillTrash(bool invert) => TrashByDefault != invert;

    internal void Attach(AppServices services) => _services = services;

    // ================================================================== launching

    /// <summary>Starts <paramref name="row"/>'s build (Shift-launch passes <see cref="LaunchMode.OpenLast"/>).</summary>
    public void Launch(InstalledBuildRowViewModel row, LaunchMode? mode = null) => Start(row, mode, script: null);

    /// <summary>Runs one of the build's Debug scripts (<see cref="LaunchArgsBuilder.DebugScripts"/>) in a console.</summary>
    public void LaunchDebug(InstalledBuildRowViewModel row, string script)
    {
        ArgumentException.ThrowIfNullOrEmpty(script);
        Start(row, null, script);
    }

    private void Start(InstalledBuildRowViewModel row, LaunchMode? mode, string? script)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Build is not { } build || row.IsStarting)
        {
            return;
        }

        row.IsStarting = true;
        _toasts.Show(Loc.Tr("ui.toast.launching", ("name", row.Name)));
        if (_services is not { } services)
        {
            SimulateLaunch(row);
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                services.Launches.Launch(build, mode, script);
            }
            catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                services.Log.Error($"Could not launch {row.Path}", e);
                UiDispatcher.Post(() =>
                {
                    row.IsStarting = false;
                    _toasts.Show(Loc.Tr("ui.toast.launch_failed", ("name", row.Name), ("error", e.Message)));
                });
            }
        });
    }

    /// <summary>Asks every running instance of <paramref name="row"/> to close its windows ("Quit all").</summary>
    public void QuitAll(InstalledBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_services is { } services)
        {
            _ = Task.Run(() => services.Launches.QuitAll(row.Path));
        }
        else
        {
            row.RunningCount = 0;
            _catalog.RefreshSampleTotals();
        }

        _toasts.Show(Loc.Tr("ui.toast.closed", ("name", row.Name)));
    }

    // ================================================================== downloads

    /// <summary>Downloads and installs <paramref name="row"/>'s build (joins the job when one is already running).</summary>
    public void Download(AvailableBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.IsIdle || _services is not { } services)
        {
            return;
        }

        try
        {
            services.Install.Install(row.Source);
        }
        catch (InvalidOperationException e)
        {
            services.Log.Error($"Could not start the download of {row.Key}", e);
            _toasts.Show(Loc.Tr("ui.toast.download_failed", ("name", row.Name), ("error", e.Message)));
        }
    }

    /// <summary>Cancels <paramref name="row"/>'s install job.</summary>
    public void CancelDownload(AvailableBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Job?.Cancel() == true)
        {
            _toasts.Show(Loc.Tr("ui.toast.download_cancelled"));
        }
    }

    // ================================================================== options

    /// <summary>Sets or clears the favourite flag (written to the build's <c>.blinfo</c>).</summary>
    public async Task SetFavouriteAsync(InstalledBuildRowViewModel row, bool favourite)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_services is not { } services)
        {
            row.IsFavourite = favourite;
            return;
        }

        await Guard(services, row, () => services.Library.SetFavouriteAsync(row.Path, favourite)).ConfigureAwait(true);
    }

    /// <summary>Makes <paramref name="row"/> the library's default build.</summary>
    public void SetDefault(InstalledBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        SetDefaultPath(row.Path);
    }

    /// <summary>Leaves the library without a default build (unticking "Use as default" on the default one).</summary>
    public void ClearDefault(InstalledBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsDefault)
        {
            SetDefaultPath(null);
        }
    }

    private void SetDefaultPath(string? path)
    {
        if (_services is { } services)
        {
            if (services.Library.LibraryRoot is not null)
            {
                services.Library.SetDefaultBuild(path);
            }

            return;
        }

        foreach (var each in _catalog.Installed)
        {
            each.IsDefault = path is not null && string.Equals(each.Path, path, StringComparison.OrdinalIgnoreCase);
        }

        _catalog.RefreshSampleTotals();
    }

    /// <summary>"Portable config": makes the build keep its preferences in its own folder, or stops it doing so.</summary>
    public async Task SetPortableAsync(InstalledBuildRowViewModel row, bool portable)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsPortable == portable)
        {
            return;
        }

        if (_services is not { } services || row.Build is not { } build)
        {
            row.IsPortable = portable;
            return;
        }

        await Guard(services, row, async () =>
        {
            row.IsPortable = await Task.Run(() => BuildConfigFolders.TogglePortableAsync(build)).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    /// <summary>"Replace on new build": the sidecar flag read by replace-on-new-build after each check.</summary>
    public void SetReplaceOnNewBuild(InstalledBuildRowViewModel row, bool replace)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.ReplaceOnNewBuild = replace;
        if (_services is { Library.LibraryRoot: not null } services)
        {
            services.Library.UpdateState(row.Path, s => s with { ReplaceOnNewBuild = replace });
        }
    }

    /// <summary>"Freeze updates": a frozen build is never offered an update.</summary>
    public async Task SetFrozenAsync(InstalledBuildRowViewModel row, bool frozen)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_services is not { } services)
        {
            row.IsFrozen = frozen;
        }
        else
        {
            await Guard(services, row, () => services.Library.SetFrozenAsync(row.Path, frozen)).ConfigureAwait(true);
        }

        _toasts.Show(Loc.Tr(frozen ? "ui.menu.toast.frozen" : "ui.menu.toast.unfrozen", ("name", row.Name)));
    }

    // ================================================================== folders, links, clipboard

    /// <summary>Opens <paramref name="folder"/> in Explorer.</summary>
    public void OpenFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        if (!Directory.Exists(folder))
        {
            _toasts.Show(Loc.Tr("ui.menu.toast.folder_missing", ("path", folder)));
            return;
        }

        Ui.OpenWithShell(folder);
    }

    /// <summary>
    /// "Show config folder": the portable folder when there is one, unless <paramref name="preferUserFolder"/> (Shift);
    /// else the user config folder of the build's version, offering the general folder when that does not exist.
    /// </summary>
    public async Task ShowConfigFolderAsync(InstalledBuildRowViewModel row, bool preferUserFolder)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Build is not { } build)
        {
            return;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var series = _services?.ApiData.Current.Series ?? [];
        var target = BuildConfigFolders.ResolveConfigFolder(build, appData, series, preferUserFolder);
        if (target.Kind == ConfigFolderKind.GeneralFallback)
        {
            var choice = await Ui.AskAsync(Loc.Tr("msg.warning"), Loc.Tr("msg.err.no_config_version"),
                [new PromptButton(Loc.Tr("act.general_folder"), IsPrimary: true), new PromptButton(Loc.Tr("act.cancel"), IsCancel: true)]).ConfigureAwait(true);
            if (choice != 0)
            {
                return;
            }
        }

        OpenFolder(target.Path);
    }

    /// <summary>Copies the build's full commit hash.</summary>
    public async Task CopyHashAsync(BuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrEmpty(row.Hash))
        {
            _toasts.Show(Loc.Tr("msg.err.no_hash"));
            return;
        }

        if (await Ui.SetClipboardTextAsync(row.Hash).ConfigureAwait(true))
        {
            _toasts.Show(Loc.Tr("ui.toast.copied"));
        }
    }

    /// <summary>"Show release notes" / "Show patch details" / "Show PR details".</summary>
    public void ShowReleaseNotes(BuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Build is { } build && ReleaseNotesLinks.For(build, row.Channel).Url is { } url)
        {
            Ui.OpenWithShell(url);
        }
    }

    // ================================================================== shell integration

    /// <summary>"Register extension": runs the build's own <c>blender -r</c>, hidden, which registers it for <c>.blend</c> files.</summary>
    public void RegisterExtension(InstalledBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_services is null || !HasShellIntegration)
        {
            _toasts.Show(Loc.Tr("ui.menu.toast.sample_only"));
            return;
        }

        var exe = ExecutableOf(row.Path);
        var name = row.Name;
        _ = Task.Run(async () =>
        {
            try
            {
                // Blender's own registration, hidden (upstream: CREATE_NO_WINDOW). Its exit code tells whether it worked.
                using var process = HiddenConsoleLauncher.Start(exe, CommandLine.Build(exe, ["-r"]), row.Path, hideConsole: true);
                var code = await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                _services?.Log.Info($"{exe} -r exited with {code}");
                _toasts.Show(code == 0
                    ? Loc.Tr("ui.menu.toast.registered", ("name", name))
                    : Loc.Tr("ui.menu.toast.failed", ("error", $"{Path.GetFileName(exe)} -r exited with code {code}")));
            }
            catch (Exception e) when (e is Win32Exception or IOException or TimeoutException)
            {
                _services?.Log.Error($"Could not register {exe}", e);
                _toasts.Show(Loc.Tr("ui.menu.toast.failed", ("error", e.Message)));
            }
        });
    }

    /// <summary>"Create shortcut": a <c>.lnk</c> to the build's executable where the user chooses (default: the desktop).</summary>
    public async Task CreateShortcutAsync(InstalledBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!HasShellIntegration)
        {
            return;
        }

        var suggested = SafeFileName(ShortcutNameOf(row)) + ".lnk";
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = await Ui.PickSaveFileAsync(Loc.Tr("msg.popup.dest"), suggested, "lnk", desktop).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            path += ".lnk";
        }

        var exe = ExecutableOf(row.Path);
        try
        {
            await Task.Run(() => ShortcutService.Write(path, new ShortcutDefinition(exe, "", row.Path, exe, 0, row.Name))).ConfigureAwait(true);
            _toasts.Show(Loc.Tr("ui.menu.toast.shortcut_created", ("path", path)));
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException)
        {
            _services?.Log.Error($"Could not create the shortcut {path}", e);
            _toasts.Show(Loc.Tr("ui.menu.toast.failed", ("error", e.Message)));
        }
    }

    /// <summary>The library's <c>bl_symlink</c> path, or null without a library.</summary>
    public string? SymlinkPath => (_services?.Library.LibraryRoot ?? _catalog.LibraryRoot) is { } root ? Path.Combine(root, SymlinkName) : null;

    /// <summary>True when <c>bl_symlink</c> already points at <paramref name="row"/> (the entry is then disabled).</summary>
    public bool SymlinkPointsAt(InstalledBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (SymlinkPath is not { } link)
        {
            return false;
        }

        try
        {
            var target = new DirectoryInfo(link).LinkTarget;
            return target is not null && string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Replace(@"\??\", "", StringComparison.Ordinal))),
                Path.TrimEndingDirectorySeparator(row.Path), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>"Create symlink": points the library's <c>bl_symlink</c> junction at the build, replacing an old junction.</summary>
    public void CreateSymlink(InstalledBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!HasShellIntegration || _services is null || SymlinkPath is not { } link)
        {
            _toasts.Show(Loc.Tr("ui.menu.toast.sample_only"));
            return;
        }

        try
        {
            CreateJunction(link, row.Path);
            _toasts.Show(Loc.Tr("ui.menu.toast.symlink_created", ("name", row.Name)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            _services.Log.Error($"Could not point {link} at {row.Path}", e);
            _toasts.Show(Loc.Tr("ui.menu.toast.failed", ("error", e.Message)));
        }
    }

    /// <summary>Points the junction <paramref name="link"/> at <paramref name="target"/>; an existing junction is replaced, anything else is refused.</summary>
    internal static void CreateJunction(string link, string target)
    {
        var info = new DirectoryInfo(link);
        if (info.Exists || File.Exists(link))
        {
            if (info.LinkTarget is null)
            {
                throw new IOException($"{link} exists and is not a link; it was left alone.");
            }

            Directory.Delete(link);
        }

        Junction.Create(link, target);
    }

    /// <summary>"Install template": copies the library's <c>template</c> folder into the build (refused while it runs).</summary>
    public async Task InstallTemplateAsync(InstalledBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsRunning)
        {
            _toasts.Show(Loc.Tr("ui.menu.toast.quit_first"));
            return;
        }

        if (_services is not { Library.LibraryRoot: { } root } services)
        {
            _toasts.Show(Loc.Tr("ui.menu.toast.sample_only"));
            return;
        }

        try
        {
            var templates = new LibraryLayout(root).TemplateFolder;
            var target = await Task.Run(() => TemplateInstaller.Install(templates, row.Path)).ConfigureAwait(true);
            _toasts.Show(target is null
                ? Loc.Tr("ui.menu.toast.template_none", ("name", row.Name))
                : Loc.Tr("ui.menu.toast.template_installed", ("name", row.Name)));
        }
        catch (PipelineException e)
        {
            services.Log.Error($"Could not install the template into {row.Path}", e);
            _toasts.Show(Loc.Tr("ui.menu.toast.failed", ("error", e.Message)));
        }
    }

    // ================================================================== names

    /// <summary>"Fetch PR name": names the build after its pull request's title.</summary>
    public async Task FetchPrNameAsync(InstalledBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Build is not { } build || ReleaseNotesLinks.PullRequestOf(build) is not { } number)
        {
            return;
        }

        if (_services is not { } services)
        {
            _toasts.Show(Loc.Tr("ui.menu.toast.sample_only"));
            return;
        }

        var titles = services.Scrape.PullRequestTitles
            ?? new PullRequestTitleService(() => services.Http.Client, services.Directories.Cache, services.Log);
        try
        {
            var title = await Task.Run(() => titles.GetTitleAsync(number)).ConfigureAwait(true);
            if (title is null)
            {
                _toasts.Show(Loc.Tr("ui.menu.toast.pr_not_found", ("number", number)));
                return;
            }

            var name = PullRequestLabels.Format(number, title, services.Settings.Current.Builds.PrependPrNumber);
            await services.Library.SetCustomNameAsync(row.Path, name).ConfigureAwait(true);
            _toasts.Show(Loc.Tr("ui.menu.toast.renamed", ("name", name)));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException or UnauthorizedAccessException)
        {
            services.Log.Error($"Could not fetch the title of PR {number}", e);
            _toasts.Show(Loc.Tr("ui.menu.toast.failed", ("error", e.Message)));
        }
    }

    /// <summary>"Rename": the build's custom name (upstream "Rename Branch"); an empty name is refused.</summary>
    public async Task RenameAsync(InstalledBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Build is not { } build)
        {
            return;
        }

        var name = await Ui.AskTextAsync(Loc.Tr("ui.menu.rename_title"), Loc.Tr("ui.menu.rename_message"),
            build.CustomName.Length > 0 ? build.CustomName : row.Name, Loc.Tr("ui.menu.rename_confirm")).ConfigureAwait(true);
        if (name is null)
        {
            return;
        }

        name = name.Trim();
        if (name.Length == 0)
        {
            _toasts.Show(Loc.Tr("msg.err.rename_branch"));
            return;
        }

        if (name.Length > 256)
        {
            name = name[..256];
        }

        if (_services is not { } services)
        {
            _toasts.Show(Loc.Tr("ui.menu.toast.sample_only"));
            return;
        }

        await Guard(services, row, () => services.Library.SetCustomNameAsync(row.Path, name)).ConfigureAwait(true);
        _toasts.Show(Loc.Tr("ui.menu.toast.renamed", ("name", name)));
    }

    /// <summary>
    /// "Edit build…" and "Initialise": the custom build dialog, then the <c>.blinfo</c> is written through the
    /// library: <see cref="LibraryService.InitialiseAsync"/> for an unrecognised or damaged folder,
    /// <see cref="LibraryService.EditAsync"/> (which re-reads the file first) for a build.
    /// </summary>
    public async Task EditBuildAsync(InstalledBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_services is not { } services)
        {
            _toasts.Show(Loc.Tr("ui.menu.toast.sample_only"));
            return;
        }

        var existing = row.IsRecognised ? row.Build : null;
        var reader = services.BuildInfoReader;
        var probe = services.Platform.VersionProbe;
        var viewModel = new CustomBuildViewModel(row.Path, existing, async (exe, ct) =>
        {
            var output = await Task.Run(() => probe.RunAsync(exe, reader.ProbeTimeout, ct), ct).ConfigureAwait(false);
            return reader.ParseVersionOutput(output);
        });
        if (await Ui.EditBuildAsync(viewModel).ConfigureAwait(true) is not { } fields)
        {
            return;
        }

        try
        {
            if (existing is null)
            {
                await services.Library.InitialiseAsync(row.Path, fields).ConfigureAwait(true);
            }
            else
            {
                await services.Library.EditAsync(row.Path, b => fields with { IsFrozen = b.IsFrozen }).ConfigureAwait(true);
            }

            var name = fields.CustomName.Length > 0 ? fields.CustomName : fields.DisplayVersion;
            _toasts.Show(Loc.Tr("ui.custom_build.saved", ("name", name)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            services.Log.Error($"Could not write the build information of {row.Path}", e);
            _toasts.Show(Loc.Tr("ui.menu.toast.failed", ("error", e.Message)));
        }
    }

    // ================================================================== update

    /// <summary>
    /// "Update to newer build" (upstream <c>library_widget.py:559-642</c>): asks about portable settings first
    /// ("Move to new / Remove / Cancel"), installs the newer build, then removes the old one, asking "Remove / Keep
    /// both" when major or minor changed. The default build and the favourite flag move to the new build.
    /// </summary>
    public async Task UpdateAsync(InstalledBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Update is not { } update)
        {
            return;
        }

        var candidate = update.Candidate;
        var newName = candidate.Build.DisplayVersion;
        if (_services is not { } services)
        {
            _toasts.Show(Loc.Tr("ui.menu.toast.sample_only"));
            return;
        }

        var moveSettings = false;
        if (BuildUpdateService.HasPortableSettings(update.Installed))
        {
            var choice = await Ui.AskAsync(Loc.Tr("ui.menu.update_title"), Loc.Tr("msg.popup.update_portable_settings"),
            [
                new PromptButton(Loc.Tr("act.move_to_new"), IsPrimary: true),
                new PromptButton(Loc.Tr("act.remove_settings")),
                new PromptButton(Loc.Tr("act.cancel"), IsCancel: true),
            ]).ConfigureAwait(true);
            if (choice is not (0 or 1))
            {
                return;
            }

            moveSettings = choice == 0;
        }

        if (services.Install.Find(candidate.Build) is { IsFinished: false })
        {
            await Ui.AskAsync(Loc.Tr("msg.info"), Loc.Tr("msg.popup.update_already_in_progress", ("version", newName)),
                [new PromptButton(Loc.Tr("act.ok"), IsPrimary: true, IsCancel: true)]).ConfigureAwait(true);
            return;
        }

        _toasts.Show(Loc.Tr("ui.menu.toast.updating", ("name", row.Name), ("version", newName)));
        LibraryEntry? entry;
        try
        {
            entry = await Task.Run(() => services.InstallAsync(candidate)).ConfigureAwait(true);
        }
        catch (Exception e) when (e is InvalidOperationException or OperationCanceledException)
        {
            services.Log.Error($"Could not update {row.Path}", e);
            entry = null;
        }

        if (entry?.Build is not { } installed)
        {
            _toasts.Show(Loc.Tr("ui.menu.toast.update_failed", ("name", row.Name)));
            return;
        }

        if (moveSettings)
        {
            try
            {
                var copied = await BuildUpdateService.MovePortableSettingsAsync(update.Installed, installed).ConfigureAwait(true);
                if (copied != PortableCopyResult.Copied)
                {
                    _toasts.Show(Loc.Tr(copied == PortableCopyResult.NoSettings ? "msg.err.port.no_settings" : "msg.err.port.no_build_dir"));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                services.Log.Error($"Could not move the portable settings of {row.Path}", e);
                var go = await Ui.AskAsync(Loc.Tr("msg.error"), Loc.Tr("msg.popup.portable_failure", ("error", e.Message)),
                    [new PromptButton(Loc.Tr("act.cont"), IsPrimary: true), new PromptButton(Loc.Tr("act.cancel"), IsCancel: true)]).ConfigureAwait(true);
                if (go != 0)
                {
                    return;
                }
            }
        }

        if (row.IsDefault)
        {
            services.Library.SetDefaultBuild(entry.Path);
        }

        if (row.IsFavourite)
        {
            await Guard(services, row, () => services.Library.SetFavouriteAsync(entry.Path, true)).ConfigureAwait(true);
        }

        if (update.IsMajorVersionUpdate)
        {
            var keep = await Ui.AskAsync(Loc.Tr("ui.menu.update_title"),
                Loc.Tr("msg.popup.major_version_update", ("current", update.Installed.DisplayVersion), ("update", installed.DisplayVersion)),
                [new PromptButton(Loc.Tr("act.remove"), IsDanger: true), new PromptButton(Loc.Tr("act.keep_both_versions"), IsPrimary: true, IsCancel: true)])
                .ConfigureAwait(true);
            if (keep != 0)
            {
                _toasts.Show(Loc.Tr("ui.menu.toast.updated", ("name", installed.DisplayVersion)));
                return;
            }
        }

        if (row.IsRunning)
        {
            _toasts.Show(Loc.Tr("ui.toast.quit_before_remove"));
            return;
        }

        // Upstream removes the old build to the bin after an update, whatever the delete setting.
        await RemoveCoreAsync(services, row, toTrash: services.Platform.RecycleBin.IsAvailable).ConfigureAwait(true);
        _toasts.Show(Loc.Tr("ui.menu.toast.updated", ("name", installed.DisplayVersion)));
    }

    // ================================================================== removal

    /// <summary>
    /// Removes a build after confirmation: to the Recycle Bin or permanently per the default-delete setting, with
    /// Shift (<paramref name="invertDeleteSetting"/>) inverting it; refused while it runs.
    /// </summary>
    public Task RemoveAsync(InstalledBuildRowViewModel row, bool invertDeleteSetting = false)
    {
        ArgumentNullException.ThrowIfNull(row);
        return RemoveManyAsync([row], invertDeleteSetting);
    }

    /// <summary>Removes several builds after one confirmation (the multi-selection menu). Running builds are skipped.</summary>
    public async Task RemoveManyAsync(IReadOnlyList<InstalledBuildRowViewModel> rows, bool invertDeleteSetting = false)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var removable = rows.Where(r => !r.IsRunning).ToList();
        if (removable.Count < rows.Count)
        {
            _toasts.Show(Loc.Tr("ui.toast.quit_before_remove"));
        }

        if (removable.Count == 0)
        {
            return;
        }

        var trash = WillTrash(invertDeleteSetting) && (_services?.Platform.RecycleBin.IsAvailable ?? true);
        var title = Loc.Tr(trash ? "act.a.trash" : "act.a.delete");
        var message = Loc.Plural(trash ? "msg.popup.ask_send_to_trash" : "msg.popup.ask_remove_from_drive", removable.Count).Trim();
        var answer = await Ui.AskAsync(title, message,
        [
            new PromptButton(Loc.Tr("act.yes"), IsDanger: true),
            new PromptButton(Loc.Tr("act.no"), IsPrimary: true, IsCancel: true),
        ]).ConfigureAwait(true);
        if (answer != 0)
        {
            return;
        }

        if (_services is not { } services)
        {
            foreach (var row in removable)
            {
                _catalog.Installed.Remove(row);
            }

            _catalog.RefreshSampleTotals();
            ToastRemoved(removable);
            return;
        }

        var removed = new List<InstalledBuildRowViewModel>();
        foreach (var row in removable)
        {
            if (await RemoveCoreAsync(services, row, trash).ConfigureAwait(true))
            {
                removed.Add(row);
            }
        }

        ToastRemoved(removed);
    }

    private void ToastRemoved(IReadOnlyList<InstalledBuildRowViewModel> removed)
    {
        if (removed.Count == 1)
        {
            _toasts.Show(Loc.Tr("ui.toast.removed", ("name", removed[0].Name), ("size", removed[0].SizeText)));
        }
        else if (removed.Count > 1)
        {
            _toasts.Show(Loc.Plural("ui.menu.toast.removed_many", removed.Count));
        }
    }

    // One build: the Remover, asking before a permanent delete when the bin cannot take it, then the library.
    private async Task<bool> RemoveCoreAsync(AppServices services, InstalledBuildRowViewModel row, bool toTrash)
    {
        try
        {
            var result = await Task.Run(() => services.Remover.RemoveAsync(row.Path, toTrash)).ConfigureAwait(true);
            if (result.Outcome == RecycleOutcome.NotRecycled)
            {
                var answer = await Ui.AskAsync(Loc.Tr("act.a.delete"),
                    Loc.Tr("ui.menu.not_recycled", ("name", row.Name), ("reason", result.Reason ?? "")),
                    [new PromptButton(Loc.Tr("act.delete"), IsDanger: true), new PromptButton(Loc.Tr("act.cancel"), IsPrimary: true, IsCancel: true)])
                    .ConfigureAwait(true);
                if (answer != 0)
                {
                    return false;
                }

                await Task.Run(() => services.Remover.RemoveAsync(row.Path, toRecycleBin: false)).ConfigureAwait(true);
            }

            services.Library.Removed(row.Path);
            return true;
        }
        catch (PipelineException e)
        {
            services.Log.Error($"Could not remove {row.Path}", e);
            _toasts.Show(Loc.Tr("ui.menu.toast.remove_failed", ("name", row.Name), ("error", e.Message)));
            return false;
        }
    }

    // ================================================================== helpers

    /// <summary>The executable a shortcut or registration uses: <c>bforartists.exe</c> when present, else <c>blender.exe</c>.</summary>
    internal static string ExecutableOf(string buildFolder)
    {
        var bfa = Path.Combine(buildFolder, "bforartists.exe");
        return File.Exists(bfa) ? bfa : Path.Combine(buildFolder, "blender.exe");
    }

    /// <summary>Upstream's shortcut name, <c>Blender {version} {branch}</c>, or the custom name.</summary>
    internal static string ShortcutNameOf(InstalledBuildRowViewModel row) =>
        row.Build is { CustomName.Length: > 0 } b ? b.CustomName : $"Blender {row.Version} {row.ChannelName}";

    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
    }

    // Runs a library change, toasting and logging the failures the library reports.
    private async Task Guard(AppServices services, InstalledBuildRowViewModel row, Func<Task> change)
    {
        try
        {
            await change().ConfigureAwait(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            services.Log.Error($"Could not change {row.Path}", e);
            _toasts.Show(Loc.Tr("msg.err.generic", ("err", e.Message)).Trim());
        }
    }

    // The design's behaviour without services: "Starting" for about a second, then one more running instance.
    private void SimulateLaunch(InstalledBuildRowViewModel row) =>
        DispatcherTimer.RunOnce(() =>
        {
            row.IsStarting = false;
            row.RunningCount++;
            row.LastUsed = DateTimeOffset.Now;
            _catalog.RefreshSampleTotals();
        }, TimeSpan.FromMilliseconds(1100));
}
