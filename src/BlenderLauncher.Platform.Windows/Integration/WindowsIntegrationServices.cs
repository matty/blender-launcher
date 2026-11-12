using System.Runtime.InteropServices;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Integration;
using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Platform.Windows.RegistryAccess;
using BlenderLauncher.Platform.Windows.Shell;

namespace BlenderLauncher.Platform.Windows.Integration;

/// <summary>
/// The launcher in the <c>.blend</c> "Open with" list, under <see cref="IntegrationRoots.ClassesKey"/>
/// (<c>HKCU\Software\Classes</c>):
/// <list type="bullet">
/// <item><c>BlenderLauncherNet.blend\shell\open\command</c> = <c>"&lt;exe&gt;" "%1"</c></item>
/// <item><c>BlenderLauncherNet.blend\DefaultIcon</c> = <c>"&lt;exe&gt;",1</c> (the exe's second icon, the <c>.blend</c> file icon), or an <c>.ico</c> path</item>
/// <item><c>.blend\OpenWithProgids</c> value <c>BlenderLauncherNet.blend</c> = empty</item>
/// </list>
/// Unlike upstream it then sends <c>SHChangeNotify</c> so Explorer picks the change up at once.
/// </summary>
public sealed class WindowsFileAssociation : IFileAssociation
{
    private const string FriendlyName = "Blender File";
    private readonly IntegrationRoots _roots;

    public WindowsFileAssociation(IntegrationRoots roots) => _roots = roots;

    public bool IsAvailable => true;

    private string ProgIdKey => _roots.ClassesKey + @"\" + AppIdentity.FileTypeProgId;

    private string CommandKey => ProgIdKey + @"\shell\open\command";

    /// <summary>The open command for <paramref name="exePath"/>.</summary>
    public static string CommandFor(string exePath) => $"\"{exePath}\" \"%1\"";

    /// <summary>The icon index of the <c>.blend</c> file icon inside the exe (Win32Resources.targets: group 32513).</summary>
    public const int BlendFileIconIndex = 1;

    public FileAssociationState GetState(string exePath)
    {
        if (!UserRegistry.KeyExists(ProgIdKey))
        {
            return FileAssociationState.NotRegistered;
        }

        var command = UserRegistry.GetString(CommandKey, null);
        return StartupCommand.Matches(command, exePath) ? FileAssociationState.ThisExecutable : FileAssociationState.OtherExecutable;
    }

    public void Register(string exePath, string? iconPath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        UserRegistry.SetString(ProgIdKey, null, FriendlyName);
        UserRegistry.SetString(CommandKey, null, CommandFor(exePath));
        UserRegistry.SetString(ProgIdKey + @"\DefaultIcon", null, iconPath ?? $"\"{exePath}\",{BlendFileIconIndex}");
        UserRegistry.SetString(_roots.ClassesKey + @"\.blend\OpenWithProgids", AppIdentity.FileTypeProgId, "");
        _roots.NotifyAssociationsChanged();
    }

    public void Unregister()
    {
        UserRegistry.DeleteTree(ProgIdKey);
        foreach (var extension in new[] { ".blend", ".blend1" })
        {
            var openWith = _roots.ClassesKey + @"\" + extension + @"\OpenWithProgids";
            UserRegistry.DeleteValue(openWith, AppIdentity.FileTypeProgId);

            // Tidy only what is now empty; another program's values keep the keys.
            if (UserRegistry.DeleteIfEmpty(openWith))
            {
                UserRegistry.DeleteIfEmpty(_roots.ClassesKey + @"\" + extension);
            }
        }

        _roots.NotifyAssociationsChanged();
    }
}

/// <summary>The <c>BlenderLauncherNet</c> value under <see cref="IntegrationRoots.RunKey"/>, holding the quoted exe path.</summary>
public sealed class WindowsStartupRegistration : IStartupRegistration
{
    private readonly IntegrationRoots _roots;

    public WindowsStartupRegistration(IntegrationRoots roots) => _roots = roots;

    public bool IsAvailable => true;

    public bool Exists => UserRegistry.ValueExists(_roots.RunKey, AppIdentity.StartupRunValueName);

    public bool IsEnabled(string exePath) =>
        StartupCommand.Matches(UserRegistry.GetString(_roots.RunKey, AppIdentity.StartupRunValueName), exePath);

    public void Enable(string exePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        UserRegistry.SetString(_roots.RunKey, AppIdentity.StartupRunValueName, StartupCommand.Format(exePath));
    }

    public void Disable() => UserRegistry.DeleteValue(_roots.RunKey, AppIdentity.StartupRunValueName);
}

/// <summary><c>.lnk</c> files through <see cref="ShortcutService"/>, with the roots' Start Menu and Desktop folders.</summary>
public sealed class WindowsShortcutService : IShortcutService
{
    private readonly IntegrationRoots _roots;

    public WindowsShortcutService(IntegrationRoots roots) => _roots = roots;

    public bool IsAvailable => true;

    public string? StartMenuFolder => _roots.StartMenuFolder;

    public string? DesktopFolder => _roots.DesktopFolder;

    public void Write(string path, ShortcutSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ShortcutService.Write(path, new ShortcutDefinition(spec.TargetPath, spec.Arguments, spec.WorkingDirectory, spec.IconPath, spec.IconIndex, spec.Description));
    }

    public ShortcutSpec? TryRead(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var d = ShortcutService.Read(path);
            return new ShortcutSpec(d.TargetPath, d.Arguments, d.WorkingDirectory, d.IconPath, d.IconIndex, d.Description);
        }
        catch (COMException)
        {
            return null;
        }
    }
}

/// <summary>
/// The opt-in Add/Remove Programs entry, key <c>BlenderLauncherNet.Portable</c> under <see cref="IntegrationRoots.UninstallKey"/>.
/// Upstream's WinGet values are not written (winget under the port's own ID is later).
/// </summary>
public sealed class WindowsAppRegistration : IAppRegistration
{
    /// <summary>The Publisher shown in Settings › Apps.</summary>
    public const string Publisher = "Blender Launcher contributors";

    private readonly IntegrationRoots _roots;

    public WindowsAppRegistration(IntegrationRoots roots) => _roots = roots;

    public bool IsAvailable => true;

    private string Key => _roots.UninstallKey + @"\" + AppIdentity.UninstallKeyName;

    public bool IsRegistered() => UserRegistry.KeyExists(Key);

    public void Register(AppRegistrationInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var exe = info.ExePath;
        UserRegistry.SetString(Key, "DisplayName", AppIdentity.DisplayName);
        UserRegistry.SetString(Key, "DisplayVersion", info.Version);
        UserRegistry.SetString(Key, "Publisher", Publisher);
        UserRegistry.SetString(Key, "InstallLocation", Path.GetDirectoryName(exe) ?? "");
        UserRegistry.SetString(Key, "DisplayIcon", $"\"{exe}\",0");
        UserRegistry.SetString(Key, "UninstallString", $"\"{exe}\" uninstall");
        UserRegistry.SetString(Key, "QuietUninstallString", $"\"{exe}\" uninstall -q");
        UserRegistry.SetDword(Key, "NoModify", 1);
        UserRegistry.SetDword(Key, "NoRepair", 1);
    }

    public void Unregister() => UserRegistry.DeleteTree(Key);
}
