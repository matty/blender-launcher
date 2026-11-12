using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Platform.Windows.Credentials;
using BlenderLauncher.Platform.Windows.FileSystem;
using BlenderLauncher.Platform.Windows.Hotkeys;
using BlenderLauncher.Platform.Windows.Integration;
using BlenderLauncher.Platform.Windows.Processes;

namespace BlenderLauncher.Platform.Windows;

/// <summary>The Windows implementations of the platform services, as the bundle <c>AppServices</c> takes.</summary>
public static class WindowsPlatformServices
{
    /// <summary>
    /// Every Windows service: the Recycle Bin, Credential Manager, the process launcher, and the shell integration
    /// (file association, start-up, shortcuts, hotkey, Add/Remove Programs) over <paramref name="roots"/>
    /// (<see cref="IntegrationRoots.Current"/> by default). The version probe stays Core's process probe.
    /// </summary>
    public static PlatformServiceBundle Create(IntegrationRoots? roots = null)
    {
        roots ??= IntegrationRoots.Current;
        return new()
        {
            RecycleBin = new WindowsRecycleBin(),
            Credentials = new WindowsCredentialStore(roots.CredentialPrefix),
            ProcessLauncher = new WindowsProcessLauncher(),
            FileAssociation = new WindowsFileAssociation(roots),
            Startup = new WindowsStartupRegistration(roots),
            Shortcuts = new WindowsShortcutService(roots),
            Hotkey = new WindowsGlobalHotkey(),
            AppRegistration = new WindowsAppRegistration(roots),
        };
    }
}
