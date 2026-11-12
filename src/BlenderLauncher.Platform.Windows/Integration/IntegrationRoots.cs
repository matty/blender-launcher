using System.Runtime.InteropServices;
using BlenderLauncher.Core;
using BlenderLauncher.Platform.Windows.RegistryAccess;

namespace BlenderLauncher.Platform.Windows.Integration;

/// <summary>
/// Where the Windows integration services write: registry keys (paths under HKEY_CURRENT_USER), folders, the
/// credential prefix and the shell notification. <see cref="Real"/> is the user's profile; <see cref="Throwaway"/>
/// redirects everything to keys and folders that exist only for a test and are deleted by <see cref="DeleteThrowaway"/>,
/// so tests, the self-test and verification runs never touch the user's real integration.
/// </summary>
public sealed record IntegrationRoots
{
    /// <summary>The key test roots live under: <c>HKCU\Software\BlenderLauncherNet\TestRoot</c>.</summary>
    public const string TestRootParent = @"Software\" + AppIdentity.TechnicalId + @"\TestRoot";

    /// <summary><c>Software\Classes</c>: ProgIDs and file extensions.</summary>
    public string ClassesKey { get; init; } = @"Software\Classes";

    /// <summary><c>Software\Microsoft\Windows\CurrentVersion\Run</c>.</summary>
    public string RunKey { get; init; } = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary><c>Software\Microsoft\Windows\CurrentVersion\Uninstall</c>.</summary>
    public string UninstallKey { get; init; } = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>The Start Menu's Programs folder.</summary>
    public string? StartMenuFolder { get; init; } = Folder(Environment.SpecialFolder.Programs);

    /// <summary>The Desktop folder.</summary>
    public string? DesktopFolder { get; init; } = Folder(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>Prefix of the credential targets (<see cref="AppIdentity.CredentialTargetPrefix"/>).</summary>
    public string CredentialPrefix { get; init; } = AppIdentity.CredentialTargetPrefix;

    /// <summary>Tells Explorer that file associations changed (<c>SHChangeNotify</c>); a no-op for throwaway roots.</summary>
    public Action NotifyAssociationsChanged { get; init; } = ShellNotify.AssociationsChanged;

    /// <summary>The throwaway root's name, or null for the real profile.</summary>
    public string? TestName { get; init; }

    /// <summary>The throwaway root's folder (Start Menu and Desktop live in it), or null.</summary>
    public string? TestFolder { get; init; }

    /// <summary>The user's real profile.</summary>
    public static IntegrationRoots Real { get; } = new();

    /// <summary>
    /// The roots the app uses: <see cref="Real"/> unless the hidden, test-only <c>--test-integration-root NAME</c>
    /// switch set <see cref="Throwaway"/> roots for this process.
    /// </summary>
    public static IntegrationRoots Current { get; set; } = Real;

    /// <summary>
    /// Throwaway roots called <paramref name="name"/>: registry under <c>HKCU\Software\BlenderLauncherNet\TestRoot\NAME</c>,
    /// folders under <c>%TEMP%\BlenderLauncherNet-TestRoot\NAME</c>, credentials under <c>BlenderLauncherNet/selftest-NAME/</c>,
    /// and no shell notification.
    /// </summary>
    public static IntegrationRoots Throwaway(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny([.. Path.GetInvalidFileNameChars(), '\\', '/']) >= 0)
        {
            throw new ArgumentException("The test root name must be a plain name.", nameof(name));
        }

        var key = TestRootParent + @"\" + name;
        var folder = Path.Combine(Path.GetTempPath(), AppIdentity.TechnicalId + "-TestRoot", name);
        return new IntegrationRoots
        {
            ClassesKey = key + @"\Classes",
            RunKey = key + @"\Run",
            UninstallKey = key + @"\Uninstall",
            StartMenuFolder = Path.Combine(folder, "StartMenu"),
            DesktopFolder = Path.Combine(folder, "Desktop"),
            CredentialPrefix = AppIdentity.CredentialTargetPrefix + "selftest-" + name + "/",
            NotifyAssociationsChanged = static () => { },
            TestName = name,
            TestFolder = folder,
        };
    }

    /// <summary>Deletes a throwaway root's keys and folder (and the empty parents it created). Does nothing for real roots.</summary>
    public void DeleteThrowaway()
    {
        if (TestName is null)
        {
            return;
        }

        UserRegistry.DeleteTree(TestRootParent + @"\" + TestName);
        UserRegistry.DeleteIfEmpty(TestRootParent);
        UserRegistry.DeleteIfEmpty(@"Software\" + AppIdentity.TechnicalId);
        if (TestFolder is { } folder && Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
            var parent = Path.GetDirectoryName(folder);
            if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
    }

    private static string? Folder(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify) is { Length: > 0 } path ? path : null;
}

/// <summary>Shell change notifications.</summary>
internal static partial class ShellNotify
{
    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    /// <summary><c>SHChangeNotify(SHCNE_ASSOCCHANGED)</c>: Explorer refreshes "Open with" lists and icons.</summary>
    public static void AssociationsChanged() => SHChangeNotify(ShcneAssocChanged, ShcnfIdList, 0, 0);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int wEventId, uint uFlags, nint dwItem1, nint dwItem2);
}
