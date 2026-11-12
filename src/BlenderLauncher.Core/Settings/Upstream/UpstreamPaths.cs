namespace BlenderLauncher.Core.Settings.Upstream;

/// <summary>Where upstream Blender Launcher V2 keeps things on Windows.</summary>
public static class UpstreamPaths
{
    /// <summary>Upstream's folder name under <c>%LOCALAPPDATA%</c>.</summary>
    public const string FolderName = "Blender Launcher";

    /// <summary>Upstream's INI file name, in its user folder or (portable) beside its exe.</summary>
    public const string IniFileName = "Blender Launcher.ini";

    /// <summary>Upstream's Python keyring service name.</summary>
    public const string KeyringService = "BlenderLauncher";

    /// <summary><c>&lt;localAppData&gt;\Blender Launcher\Blender Launcher.ini</c>; the user's local app data when null.</summary>
    public static string UserIni(string? localAppData = null) => Path.Combine(UserFolder(localAppData), IniFileName);

    /// <summary>
    /// <c>&lt;localAppData&gt;\Blender Launcher</c>: upstream's config, cache and default library folder (all the same
    /// folder on Windows).
    /// </summary>
    public static string UserFolder(string? localAppData = null) =>
        Path.Combine(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);
}
