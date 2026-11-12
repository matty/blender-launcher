using BlenderLauncher.App.Lifetime;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Integration;
using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Platform.Windows;
using BlenderLauncher.Platform.Windows.Credentials;
using BlenderLauncher.Platform.Windows.Integration;
using BlenderLauncher.Platform.Windows.RegistryAccess;

namespace BlenderLauncher.App.SelfTest.Checks;

/// <summary>
/// The Windows integration services in the native exe, against throwaway roots only
/// (<see cref="IntegrationRoots.Throwaway"/>: <c>HKCU\Software\BlenderLauncherNet\TestRoot\&lt;guid&gt;</c>, a temp folder
/// for the Start Menu and Desktop, a <c>BlenderLauncherNet/selftest-&lt;guid&gt;/</c> credential prefix, no shell
/// notification). File association, Run value, Add/Remove Programs entry, launcher shortcuts (including the "ask before
/// replacing another" path), the hotkey parser and registration, and an uninstall that must keep a library placed inside
/// the data folder. Everything is deleted afterwards and the deletion is checked.
/// </summary>
internal sealed class IntegrationCheck : ISelfTestCheck
{
    public string Id => "Integration";
    public string Name => "Windows integration services (throwaway roots)";
    public bool NeedsUiThread => false;
    public bool NeedsNetwork => false;

    public async Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct)
    {
        var roots = IntegrationRoots.Throwaway("selftest-" + Guid.NewGuid().ToString("N")[..12]);
        var platform = WindowsPlatformServices.Create(roots);
        var done = new List<string>();
        try
        {
            var exeFolder = Path.Combine(ctx.WorkDirectory, "Launcher Copy");
            Directory.CreateDirectory(exeFolder);
            var exe = Path.Combine(exeFolder, AppIdentity.ExecutableName);
            File.WriteAllText(exe, "stand-in");

            // File association.
            platform.FileAssociation.Register(exe);
            var command = UserRegistry.GetString(roots.ClassesKey + @"\" + AppIdentity.FileTypeProgId + @"\shell\open\command", null);
            Expect(command == $"\"{exe}\" \"%1\"", $"open command '{command}'");
            Expect(UserRegistry.ValueExists(roots.ClassesKey + @"\.blend\OpenWithProgids", AppIdentity.FileTypeProgId), "OpenWithProgids value missing");
            Expect(platform.FileAssociation.GetState(exe) == FileAssociationState.ThisExecutable, "state after register");
            Expect(platform.FileAssociation.GetState(exe + "x") == FileAssociationState.OtherExecutable, "state for another exe");
            platform.FileAssociation.Unregister();
            Expect(platform.FileAssociation.GetState(exe) == FileAssociationState.NotRegistered, "state after unregister");
            Expect(!UserRegistry.KeyExists(roots.ClassesKey + @"\.blend"), ".blend key left after unregister");
            done.Add("association register/unregister");

            // Run value.
            platform.Startup.Enable(exe);
            Expect(UserRegistry.GetString(roots.RunKey, AppIdentity.StartupRunValueName) == $"\"{exe}\"", "Run value not quoted");
            Expect(platform.Startup.IsEnabled(exe) && !platform.Startup.IsEnabled(exe + "x"), "Run value match");
            platform.Startup.Disable();
            Expect(!platform.Startup.Exists, "Run value left");
            done.Add("Run value");

            // Add/Remove Programs.
            platform.AppRegistration.Register(new AppRegistrationInfo(exe, AppIdentity.Version));
            var arpKey = roots.UninstallKey + @"\" + AppIdentity.UninstallKeyName;
            Expect(UserRegistry.GetString(arpKey, "QuietUninstallString") == $"\"{exe}\" uninstall -q", "QuietUninstallString");
            Expect(UserRegistry.GetString(arpKey, "DisplayName") == AppIdentity.DisplayName, "DisplayName");
            platform.AppRegistration.Unregister();
            Expect(!platform.AppRegistration.IsRegistered(), "ARP key left");
            done.Add("ARP entry");

            // Launcher shortcuts; a foreign "Blender Launcher.lnk" is kept unless the user agrees.
            var created = await LauncherShortcut.CreateAsync(platform.Shortcuts, ShortcutLocation.StartMenu, exe, _ => Task.FromResult(false));
            var again = await LauncherShortcut.CreateAsync(platform.Shortcuts, ShortcutLocation.StartMenu, exe, _ => Task.FromResult(false));
            Expect(created == ShortcutOutcome.Created && again == ShortcutOutcome.Replaced, $"start menu {created}/{again}");
            var desktop = LauncherShortcut.PathFor(platform.Shortcuts, ShortcutLocation.Desktop)!;
            Directory.CreateDirectory(Path.GetDirectoryName(desktop)!);
            platform.Shortcuts.Write(desktop, new ShortcutSpec(Path.Combine(ctx.WorkDirectory, "upstream.exe")));
            var kept = await LauncherShortcut.CreateAsync(platform.Shortcuts, ShortcutLocation.Desktop, exe, _ => Task.FromResult(false));
            Expect(kept == ShortcutOutcome.Kept && !LauncherShortcut.PointsAt(platform.Shortcuts.TryRead(desktop), exe), "foreign shortcut not kept");
            Expect(Path.GetFileName(desktop) == LauncherShortcut.FileName && Path.GetDirectoryName(desktop) == roots.DesktopFolder, "desktop path");
            done.Add("shortcuts (created, ours replaced, foreign kept)");

            // Hotkey text and registration on an unlikely combination, released at once.
            var hotkey = await platform.Hotkey.SetAsync("ctrl+alt+shift+f24");
            var off = await platform.Hotkey.SetAsync(null);
            var invalid = await platform.Hotkey.SetAsync("ctrl+nokey");
            Expect((hotkey.Success || hotkey.Failure == HotkeyFailure.InUse) && off.Success && invalid.Failure == HotkeyFailure.Invalid,
                $"hotkey {hotkey.Failure}/{off.Failure}/{invalid.Failure}");
            done.Add($"hotkey ({(hotkey.Success ? "registered, released" : "in use elsewhere")}, invalid text refused)");

            // Uninstall with everything set, a credential under the throwaway prefix, and the library inside data.
            var data = Path.Combine(exeFolder, "data");
            var library = Path.Combine(data, "library");
            Directory.CreateDirectory(Path.Combine(library, "stable", "blender-4.2"));
            File.WriteAllText(Path.Combine(library, "stable", "blender-4.2", "blender.exe"), "build");
            Directory.CreateDirectory(Path.Combine(data, "logs"));
            File.WriteAllText(Path.Combine(data, "logs", "launcher.log"), "log");
            var settingsFile = Path.Combine(exeFolder, DataDirectories.SettingsFileName);
            File.WriteAllText(settingsFile, "{}");
            platform.FileAssociation.Register(exe);
            platform.Startup.Enable(exe);
            platform.AppRegistration.Register(new AppRegistrationInfo(exe, AppIdentity.Version));
            platform.Credentials.Set(CredentialKeys.GitHubToken, "selftest-token");
            var report = Uninstaller.Run(new UninstallContext
            {
                Platform = platform,
                Directories = new DataDirectories(data, settingsFile, isPortable: true),
                LibraryRoot = library,
                ExePath = exe,
                ShippedFileNames = LauncherExe.ShippedFileNames,
            });
            ctx.Log(string.Join(" | ", report.Steps.Select(s => s.Text)));
            Expect(report.Succeeded, "an uninstall step failed");
            Expect(File.Exists(Path.Combine(library, "stable", "blender-4.2", "blender.exe")), "LIBRARY DELETED");
            Expect(!Directory.Exists(Path.Combine(data, "logs")) && !File.Exists(settingsFile), "data not removed");
            Expect(platform.FileAssociation.GetState(exe) == FileAssociationState.NotRegistered && !platform.Startup.Exists
                && !platform.AppRegistration.IsRegistered(), "registrations left");
            Expect(!File.Exists(LauncherShortcut.PathFor(platform.Shortcuts, ShortcutLocation.StartMenu)), "our Start Menu shortcut left");
            Expect(File.Exists(desktop), "the foreign Desktop shortcut was removed");
            Expect(platform.Credentials.Get(CredentialKeys.GitHubToken) is null, "credential left");
            Expect(!report.RemoveExeFolder && report.ExeFolderKeptReason is not null, "exe folder with the library in it was going to be deleted");
            done.Add("uninstall (library kept, foreign shortcut kept)");
        }
        catch (CheckFailed e)
        {
            return SelfTestResult.Fail($"{e.Message} (after: {string.Join(", ", done)})");
        }
        finally
        {
            platform.Hotkey.Dispose();
            foreach (var key in CredentialKeys.All)
            {
                CredentialStore.Delete(roots.CredentialPrefix + key);
            }

            roots.DeleteThrowaway();
        }

        var leftovers = UserRegistry.KeyExists(IntegrationRoots.TestRootParent + @"\" + roots.TestName) || Directory.Exists(roots.TestFolder);
        return leftovers
            ? SelfTestResult.Fail("the throwaway root was not removed")
            : SelfTestResult.Pass(string.Join("; ", done) + "; throwaway root removed");
    }

    private static void Expect(bool condition, string what)
    {
        if (!condition)
        {
            throw new CheckFailed(what);
        }
    }

    private sealed class CheckFailed(string message) : Exception(message);
}
