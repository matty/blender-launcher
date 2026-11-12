using Avalonia.Headless.XUnit;
using BlenderLauncher.App.ViewModels.Settings;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Platform.Windows;
using BlenderLauncher.Core.Integration;
using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Platform.Windows.FileSystem;
using BlenderLauncher.Platform.Windows.Hotkeys;
using BlenderLauncher.Platform.Windows.Integration;
using BlenderLauncher.Platform.Windows.RegistryAccess;

namespace BlenderLauncher.App.Tests.Integration;

/// <summary>The hotkey text parser: the stored <c>ctrl+alt+f11</c> form to RegisterHotKey's modifiers and key.</summary>
public sealed class HotkeyChordTests
{
    [Theory]
    [InlineData("alt+f11", HotkeyModifiers.Alt, 0x7Au)]
    [InlineData("ctrl+alt+f11", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x7Au)]
    [InlineData("CTRL+Shift+B", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x42u)]
    [InlineData("shift+1", HotkeyModifiers.Shift, 0x31u)]
    [InlineData("ctrl++", HotkeyModifiers.Control, 0x6Bu)]
    [InlineData("<ctrl>+<f24>", HotkeyModifiers.Control, 0x87u)]
    [InlineData("win+page_down", HotkeyModifiers.Win, 0x22u)]
    [InlineData("f5", HotkeyModifiers.None, 0x74u)]
    public void Parses(string text, HotkeyModifiers modifiers, uint vk)
    {
        Assert.True(HotkeyChord.TryParse(text, out var chord));
        Assert.Equal(new HotkeyChord(modifiers, vk), chord);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ctrl+")]
    [InlineData("ctrl+alt")]
    [InlineData("ctrl+ctrl+a")]
    [InlineData("hyper+a")]
    [InlineData("ctrl+f25")]
    [InlineData("ctrl+ab")]
    public void Refuses(string text) => Assert.False(HotkeyChord.TryParse(text, out _));
}

/// <summary>
/// The registry and shortcut services against throwaway roots (<c>HKCU\Software\BlenderLauncherNet\TestRoot\&lt;guid&gt;</c>
/// and a temp folder), deleted afterwards. The user's real Classes, Run and Uninstall keys are never opened for writing.
/// </summary>
public sealed class WindowsIntegrationServiceTests : IDisposable
{
    private readonly IntegrationRoots _roots = IntegrationRoots.Throwaway("test-" + Guid.NewGuid().ToString("N"));
    private readonly string _exe;

    public WindowsIntegrationServiceTests()
    {
        _exe = Path.Combine(_roots.TestFolder!, "App Folder", AppIdentity.ExecutableName);
    }

    public void Dispose() => _roots.DeleteThrowaway();

    [Fact]
    public void Throwaway_roots_never_point_at_the_real_profile()
    {
        Assert.StartsWith(IntegrationRoots.TestRootParent + @"\", _roots.ClassesKey, StringComparison.Ordinal);
        Assert.StartsWith(IntegrationRoots.TestRootParent + @"\", _roots.RunKey, StringComparison.Ordinal);
        Assert.StartsWith(IntegrationRoots.TestRootParent + @"\", _roots.UninstallKey, StringComparison.Ordinal);
        Assert.StartsWith(Path.GetTempPath(), _roots.DesktopFolder!, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(AppIdentity.CredentialTargetPrefix + "selftest-", _roots.CredentialPrefix, StringComparison.Ordinal);
    }

    [Fact]
    public void Association_registers_and_unregisters_without_touching_other_programs_entries()
    {
        var notified = 0;
        var roots = _roots with { NotifyAssociationsChanged = () => notified++ };
        var association = new WindowsFileAssociation(roots);
        var openWith = roots.ClassesKey + @"\.blend\OpenWithProgids";
        UserRegistry.SetString(openWith, "blender.4.2", "");
        UserRegistry.SetString(roots.ClassesKey + @"\.blend1\OpenWithProgids", AppIdentity.FileTypeProgId, "");

        association.Register(_exe);
        Assert.Equal($"\"{_exe}\" \"%1\"", UserRegistry.GetString(roots.ClassesKey + @"\BlenderLauncherNet.blend\shell\open\command", null));
        Assert.Equal($"\"{_exe}\",1", UserRegistry.GetString(roots.ClassesKey + @"\BlenderLauncherNet.blend\DefaultIcon", null));
        Assert.Equal(FileAssociationState.ThisExecutable, association.GetState(_exe));

        association.Unregister();
        Assert.Equal(FileAssociationState.NotRegistered, association.GetState(_exe));
        Assert.False(UserRegistry.ValueExists(openWith, AppIdentity.FileTypeProgId));
        Assert.True(UserRegistry.ValueExists(openWith, "blender.4.2"));
        Assert.False(UserRegistry.KeyExists(roots.ClassesKey + @"\.blend1"));
        Assert.Equal(2, notified);
    }

    [Fact]
    public void Startup_reads_on_only_for_this_exe()
    {
        var startup = new WindowsStartupRegistration(_roots);
        Assert.False(startup.Exists);
        startup.Enable(_exe);
        Assert.Equal($"\"{_exe}\"", UserRegistry.GetString(_roots.RunKey, AppIdentity.StartupRunValueName));
        Assert.True(startup.IsEnabled(_exe));
        Assert.False(startup.IsEnabled(_exe + ".other.exe"));
        startup.Disable();
        Assert.False(startup.Exists);
    }

    [Fact]
    public async Task Launcher_shortcut_is_written_inside_the_desktop_folder_with_the_exe_icon()
    {
        var shortcuts = new WindowsShortcutService(_roots);
        Assert.Equal(ShortcutOutcome.Created,
            await LauncherShortcut.CreateAsync(shortcuts, ShortcutLocation.Desktop, _exe, _ => Task.FromResult(false)));
        var path = Path.Combine(_roots.DesktopFolder!, "Blender Launcher.lnk");
        var spec = shortcuts.TryRead(path);
        Assert.NotNull(spec);
        Assert.Equal(_exe, spec.TargetPath, ignoreCase: true);
        Assert.Equal(_exe, spec.IconPath, ignoreCase: true);
        Assert.True(LauncherShortcut.RemoveIfOurs(shortcuts, ShortcutLocation.Desktop, _exe));
    }

    [Fact]
    public void Uninstall_removes_a_junction_in_the_data_folder_without_following_it()
    {
        var target = Path.Combine(_roots.TestFolder!, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "precious.blend"), "x");
        var data = Path.Combine(_roots.TestFolder!, "data");
        Directory.CreateDirectory(data);
        Junction.Create(Path.Combine(data, "link"), target);

        Uninstaller.DeleteTreeExcept(data, keep: null);

        Assert.False(Directory.Exists(data));
        Assert.True(File.Exists(Path.Combine(target, "precious.blend")));
    }

    [AvaloniaFact]
    public async Task Settings_controls_follow_the_registry_state()
    {
        var platform = WindowsPlatformServices.Create(_roots);
        using var vm = new SettingsViewModel(SettingsStore.InMemory(), new InMemoryCredentialStore(),
            new SettingsEnvironment { Platform = platform, ExePath = _exe });
        try
        {
            Assert.True(vm.HasIntegration);
            Assert.True(vm.CanRegisterAssociation);
            Assert.False(vm.CanUnregisterAssociation);

            await vm.RegisterAssociationCommand.ExecuteAsync(null);
            Assert.False(vm.CanRegisterAssociation);
            Assert.True(vm.CanUnregisterAssociation);
            Assert.Equal(FileAssociationState.ThisExecutable, platform.FileAssociation.GetState(_exe));

            vm.LaunchAtStartup = true;
            Assert.True(platform.Startup.IsEnabled(_exe));
            platform.Startup.Enable(_exe + ".other.exe");
            using var reopened = new SettingsViewModel(SettingsStore.InMemory(), new InMemoryCredentialStore(),
                new SettingsEnvironment { Platform = platform, ExePath = _exe });
            Assert.False(reopened.LaunchAtStartup);
            Assert.True(reopened.HasStartupNote);

            await vm.UnregisterAssociationCommand.ExecuteAsync(null);
            Assert.True(vm.CanRegisterAssociation);
        }
        finally
        {
            platform.Hotkey.Dispose();
        }
    }
}
