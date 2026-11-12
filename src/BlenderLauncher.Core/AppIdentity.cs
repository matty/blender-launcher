using System.Reflection;

namespace BlenderLauncher.Core;

/// <summary>
/// Every name and identifier the launcher uses towards the OS and the network.
/// Nothing else may hard-code these.
/// </summary>
public static class AppIdentity
{
    public const string DisplayName = "Blender Launcher";
    /// <summary>
    /// The shipped file name. The App's assembly is <see cref="AssemblyName"/>; the publish renames the native binary to
    /// this name (target <c>RenameShippedExecutable</c> in the App project).
    /// </summary>
    public const string ExecutableName = "Blender Launcher.exe";

    /// <summary>The App's assembly name, as used in <c>avares://BlenderLauncher/...</c> URIs. No space, on purpose.</summary>
    public const string AssemblyName = "BlenderLauncher";
    public const string TechnicalId = "BlenderLauncherNet";

    /// <summary>
    /// The launcher's folder under <c>%LOCALAPPDATA%</c>. Velopack installs into it (it is the pack id) and the data
    /// lives in its <see cref="DataSubfolderName"/> subfolder, so one folder holds everything and uninstalling removes it.
    /// </summary>
    public const string DataFolderName = TechnicalId;

    /// <summary>The data subfolder of <see cref="DataFolderName"/> (and of a portable copy's folder).</summary>
    public const string DataSubfolderName = "data";
    public const string InstancePipeName = "blender-launcher-net";
    public const string FileTypeProgId = "BlenderLauncherNet.blend";
    public const string StartupRunValueName = TechnicalId;
    /// <summary>
    /// The opt-in Add/Remove Programs key of a copy the installer did not set up. Not <see cref="TechnicalId"/>: that is
    /// the key Velopack writes for an installed copy (its pack id), which this launcher must never overwrite or delete.
    /// </summary>
    public const string UninstallKeyName = TechnicalId + ".Portable";
    public const string CredentialTargetPrefix = "BlenderLauncherNet/";

    /// <summary>
    /// Repository used for self-update, as "owner/name": the update check reads its releases and the updater downloads
    /// their <c>Blender_Launcher_{tag}_Windows_x64.zip</c> (see <c>Updates/ReleaseAssets</c>). Null switches self-update off.
    /// </summary>
    public const string? UpdateRepo = "matty/blender-launcher";

    /// <summary>Base URL for upstream's API data files; append "{file}.json".</summary>
    public const string UpstreamApiBaseUrl =
        "https://raw.githubusercontent.com/Victor-IX/Blender-Launcher-V2/main/source/resources/api/";

    /// <summary>
    /// The User-Agent format: <c>{0}</c> technical ID, <c>{1}</c> version, <c>{2}</c> OS,
    /// <c>{3}</c> architecture. <c>Net.UserAgent</c> fills it in.
    /// </summary>
    public const string UserAgentFormat = "{0}/{1} ({2}; {3})";

    /// <summary>The User-Agent format with a user ID (<c>{4}</c>), sent only when one is set in Settings.</summary>
    public const string UserAgentWithUserIdFormat = "{0}/{1} ({2}; {3}; UserID-{4})";

    /// <summary>The product version, from <c>Version</c> in Directory.Build.props.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>
    /// The git commit the build was made from (the "+&lt;commit&gt;" suffix the SDK appends to the informational
    /// version when source-control information is available), or null when the build had none.
    /// </summary>
    public static string? Commit { get; } = ReadCommit();

    private static string? InformationalVersion() =>
        typeof(AppIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static string ReadVersion()
    {
        var info = InformationalVersion();
        if (string.IsNullOrEmpty(info))
        {
            return "0.0.0";
        }

        // Strip the "+<commit>" suffix the SDK appends when source-link information is available.
        var plus = info.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? info[..plus] : info;
    }

    private static string? ReadCommit()
    {
        var info = InformationalVersion();
        var plus = info?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        return plus >= 0 && plus + 1 < info!.Length ? info[(plus + 1)..] : null;
    }
}
