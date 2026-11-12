using Avalonia.Headless.XUnit;
using BlenderLauncher.App.Lifetime;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.App.ViewModels.FirstRun;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Core.Updates;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.App.Tests.Settings;

/// <summary>
/// The rules that could break without anything looking wrong: the new-user channel defaults versus
/// an upstream import, what the wizard saves, the custom build dialog's Accept rule and result, the update dialog's
/// once-per-version offer and notification routing.
/// </summary>
public sealed class FirstRunAndDialogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bl-p5b-" + Guid.NewGuid().ToString("N")[..8]);

    public FirstRunAndDialogTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void New_users_get_the_extra_channels_off_but_imported_and_returning_users_keep_theirs()
    {
        var fresh = FirstRunDefaults.Apply(AppSettings.Default).Builds;
        Assert.False(fresh.ShowBforartists || fresh.ScrapeBforartists || fresh.ShowUpbge || fresh.ScrapeUpbge || fresh.ShowUpbgeWeekly);
        Assert.True(fresh.ShowStable && fresh.ShowDaily && fresh.ShowExperimentalAndPatch);

        var imported = AppSettings.Default with { Internal = new InternalSettings { UpstreamImportDone = true } };
        Assert.True(FirstRunDefaults.Apply(imported).Builds.ShowBforartists);
        var returning = AppSettings.Default with { Internal = new InternalSettings { FirstRunSeen = true } };
        Assert.True(FirstRunDefaults.Apply(returning).Builds.ShowUpbge);
    }

    [AvaloniaFact]
    public async Task Wizard_import_keeps_upstream_defaults_and_finish_saves_the_choices()
    {
        var library = Path.Combine(_root, "Library");
        var ini = Path.Combine(_root, "Blender Launcher.ini");
        // No show_bfa_builds key: upstream's default (on) applies, not the new-user default (off).
        await File.WriteAllTextAsync(ini, $"library_folder={library.Replace('\\', '/')}\nshow_upbge_builds=false\n");
        var store = SettingsStore.InMemory();
        using var vm = new FirstRunViewModel(store, new FirstRunEnvironment
        {
            SuggestedLibraryFolder = Path.Combine(_root, "Suggested"),
            UpstreamIniPath = ini,
            Credentials = new InMemoryCredentialStore(),
        });

        Assert.True(vm.ImportUpstream);
        Assert.Equal(library, vm.LibraryPath);
        Assert.True(vm.Draft.Current.Builds.ShowBforartists);
        Assert.False(vm.Draft.Current.Builds.ShowUpbge);

        vm.ImportUpstream = false;
        Assert.False(vm.Draft.Current.Builds.ShowBforartists);
        Assert.Equal(Path.Combine(_root, "Suggested"), vm.LibraryPath);

        vm.ImportUpstream = true;
        vm.AddToDesktop = false;
        vm.RegisterFileAssociation = true;
        await vm.CheckLibraryNowAsync();
        Assert.True(vm.CanGoNext || vm.Page != FirstRunPage.Library);
        await vm.FinishAsync();

        var saved = store.Current;
        Assert.True(saved.Internal.FirstRunSeen);
        Assert.True(saved.Internal.UpstreamImportDone);
        Assert.Equal(LibraryLayout.NormaliseRoot(library), saved.General.LibraryFolder);
        Assert.True(Directory.Exists(library));
        Assert.False(Directory.Exists(Path.Combine(library, "stable")));
        Assert.True(saved.Builds.ShowBforartists);
        Assert.True(saved.Internal is { PendingStartMenuShortcut: true, PendingDesktopShortcut: false, PendingFileAssociation: true });
    }

    [AvaloniaFact]
    public async Task Wizard_asks_for_the_folder_when_upstreams_library_was_relative()
    {
        var ini = Path.Combine(_root, "Blender Launcher.ini");
        await File.WriteAllTextAsync(ini, "library_folder=.\n");
        using var vm = new FirstRunViewModel(SettingsStore.InMemory(), new FirstRunEnvironment
        {
            SuggestedLibraryFolder = Path.Combine(_root, "Suggested"),
            UpstreamIniPath = ini,
        });
        await vm.NextCommand.ExecuteAsync(null);
        await vm.CheckLibraryNowAsync();

        Assert.Equal(FirstRunPage.Library, vm.Page);
        Assert.True(vm.HasRelativeHint);
        Assert.Equal("", vm.LibraryPath);
        Assert.False(vm.CanGoNext);
    }

    [Fact]
    public void Library_check_accepts_a_new_folder_under_a_writable_parent_and_leaves_nothing_behind()
    {
        var target = Path.Combine(_root, "a", "b");
        var ok = LibraryFolderCheck.Run(target);
        Assert.True(ok.IsValid);
        Assert.False(ok.Exists);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));

        File.WriteAllText(Path.Combine(_root, "file"), "");
        Assert.Equal(LibraryCheckProblem.IsAFile, LibraryFolderCheck.Run(Path.Combine(_root, "file")).Problem);
        Assert.Equal(LibraryCheckProblem.NotAbsolute, LibraryFolderCheck.Run(@"relative\lib").Problem);
    }

    [AvaloniaFact]
    public void Custom_build_needs_an_executable_in_the_folder_and_a_version()
    {
        var folder = Path.Combine(_root, "custom", "Thing");
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "bin", "run.cmd"), "");
        File.WriteAllText(Path.Combine(_root, "outside.exe"), "");
        var vm = new CustomBuildViewModel(folder, null, null);

        Assert.Equal("", vm.Executable);
        vm.Executable = @"..\..\outside.exe";
        vm.Version = "4.2.0";
        Assert.False(vm.CanAccept);

        vm.Executable = @"bin\run.cmd";
        vm.Version = "not a version";
        Assert.False(vm.CanAccept);
        Assert.True(vm.HasVersionError);

        vm.Version = "4.3.0-alpha";
        var build = vm.ToBuildInfo();
        Assert.NotNull(build);
        Assert.Equal("bin/run.cmd", build.CustomExecutable);
        Assert.Equal("Thing", build.Branch);
        Assert.Null(build.BuildHash);
    }

    [AvaloniaFact]
    public void Editing_keeps_the_original_time_frozen_flag_and_reads_detected_output()
    {
        var folder = Path.Combine(_root, "daily", "blender-4.4");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "blender.exe"), "");
        var time = IsoTimestamp.Aware(new DateTimeOffset(2024, 5, 1, 14, 32, 17, TimeSpan.FromHours(-7)));
        var existing = new BuildInfo
        {
            Link = folder, Subversion = "4.4.0-alpha", BuildHash = "abc", CommitTime = time, Branch = "daily", IsFrozen = true,
        };
        var vm = new CustomBuildViewModel(folder, existing, null) { CustomName = "Mine" };

        var build = vm.ToBuildInfo()!;
        Assert.Equal(time, build.CommitTime);
        Assert.True(build.IsFrozen);
        Assert.Null(build.CustomExecutable);
        Assert.Equal("Mine", build.CustomName);

        vm.Apply(new VersionOutput(IsoTimestamp.Aware(new DateTimeOffset(2025, 1, 2, 3, 4, 0, TimeSpan.Zero)), "def456", "4.4.1", "Blender 4.4.1"));
        Assert.Equal("4.4.1", vm.Version);
        Assert.Equal("def456", vm.Hash);
        Assert.NotEqual(time, vm.ToBuildInfo()!.CommitTime);
    }

    [Fact]
    public void Update_is_offered_once_per_version()
    {
        var release = new AppRelease("v1.2.0", new SemVer(1, 2, 0));
        var check = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, release, null, null);

        Assert.Equal(release, UpdateDialog.ToOffer(check, null));
        Assert.Null(UpdateDialog.ToOffer(check, "v1.2.0"));
        Assert.Equal(release, UpdateDialog.ToOffer(check, "v1.1.0"));
        Assert.Null(UpdateDialog.ToOffer(check with { Status = UpdateCheckStatus.UpToDate }, null));
    }

    [Fact]
    public void Notifications_follow_their_setting_and_use_the_toast_while_the_window_shows()
    {
        var all = new AppearanceSettings();
        Assert.Equal(NotificationRoute.Toast, NotificationRouter.RouteFor(NotificationKind.DownloadFinished, all, windowShown: true, trayActive: true));
        Assert.Equal(NotificationRoute.Balloon, NotificationRouter.RouteFor(NotificationKind.Error, all, windowShown: false, trayActive: true));
        Assert.Equal(NotificationRoute.None, NotificationRouter.RouteFor(NotificationKind.NewBuilds, all, windowShown: false, trayActive: false));
        var noErrors = all with { NotifyErrors = false };
        Assert.Equal(NotificationRoute.None, NotificationRouter.RouteFor(NotificationKind.Error, noErrors, windowShown: true, trayActive: true));
    }
}
