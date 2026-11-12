namespace BlenderLauncher.Core;

/// <summary>
/// The launcher's data folders, derived from one root: <c>%LOCALAPPDATA%\BlenderLauncherNet\data</c>,
/// or <c>&lt;exe dir&gt;\data</c> in portable mode (<see cref="Resolve"/>). <c>%LOCALAPPDATA%\BlenderLauncherNet</c> is
/// also where Velopack installs the launcher, so the data sits beside <c>current</c> as in a portable copy. Nothing here
/// touches the disk except <see cref="Resolve"/>, which checks for the portable settings file and moves data left in
/// the old layout (directly in <c>BlenderLauncherNet</c>) into <c>data</c>.
/// </summary>
public sealed record DataDirectories
{
    /// <summary>The settings file name.</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>The data folder beside the exe in portable mode.</summary>
    public const string PortableDataFolderName = AppIdentity.DataSubfolderName;

    /// <summary>What the old layout kept directly in <c>%LOCALAPPDATA%\BlenderLauncherNet</c>; moved into <c>data</c>.</summary>
    private static readonly string[] LegacyEntries = [SettingsFileName, SettingsFileName + ".corrupt", "api", "cache", "state", "logs"];

    /// <summary>Creates the folder set under <paramref name="root"/>, which is made absolute. Settings live at <c>&lt;root&gt;\settings.json</c>.</summary>
    public DataDirectories(string root)
        : this(root, settingsFile: null, isPortable: false)
    {
    }

    /// <summary>Creates the folder set under <paramref name="root"/> with the settings file somewhere else (portable mode).</summary>
    public DataDirectories(string root, string? settingsFile, bool isPortable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        SettingsFile = settingsFile is null ? Path.Combine(Root, SettingsFileName) : Path.GetFullPath(settingsFile);
        IsPortable = isPortable;
    }

    /// <summary>The data root (<c>&lt;data&gt;</c>).</summary>
    public string Root { get; }

    /// <summary>
    /// <c>settings.json</c>: <c>&lt;data&gt;\settings.json</c>, or beside the exe in portable mode.
    /// </summary>
    public string SettingsFile { get; }

    /// <summary>True when <see cref="Resolve"/> found <c>settings.json</c> beside the exe.</summary>
    public bool IsPortable { get; }

    /// <summary><c>&lt;data&gt;\api</c>: the fetched <c>blender_launcher_api.json</c>.</summary>
    public string Api => Path.Combine(Root, "api");

    /// <summary><c>&lt;data&gt;\cache</c>: the stable and Bforartists index caches, PR titles and download sizes.</summary>
    public string Cache => Path.Combine(Root, "cache");

    /// <summary><c>&lt;data&gt;\state</c>: per-build extras.</summary>
    public string State => Path.Combine(Root, "state");

    /// <summary><c>&lt;data&gt;\logs</c>.</summary>
    public string Logs => Path.Combine(Root, "logs");

    /// <summary>The default root: <c>%LOCALAPPDATA%\BlenderLauncherNet\data</c>.</summary>
    public static DataDirectories Default() => new(DefaultRoot(LocalAppData()));

    /// <summary>The launcher's folder under <paramref name="localAppData"/>: Velopack's install root, holding <c>data</c>.</summary>
    public static string AppRoot(string localAppData) => Path.Combine(localAppData, AppIdentity.DataFolderName);

    /// <summary>The launcher's folder under the user's <c>%LOCALAPPDATA%</c>.</summary>
    public static string DefaultAppRoot() => AppRoot(LocalAppData());

    /// <summary>The non-portable data root under <paramref name="localAppData"/>.</summary>
    public static string DefaultRoot(string localAppData) => Path.Combine(AppRoot(localAppData), AppIdentity.DataSubfolderName);

    /// <summary>
    /// Chooses the data folders for the exe in <paramref name="exeDirectory"/>: portable when
    /// <c>&lt;exe dir&gt;\settings.json</c> exists (data under <c>&lt;exe dir&gt;\data</c>), otherwise
    /// <c>&lt;localAppData&gt;\BlenderLauncherNet\data</c>. The launcher's own folder under <paramref name="localAppData"/>
    /// is never portable, even with a <c>settings.json</c> left there by the old layout; that data is moved into
    /// <c>data</c> first (<see cref="MigrateLegacyLayout"/>). The portable settings file is never created here.
    /// </summary>
    /// <param name="exeDirectory">The exe's folder (<see cref="AppLocation.BaseDirectory"/> when null).</param>
    /// <param name="localAppData">The local application data folder (the user's when null).</param>
    public static DataDirectories Resolve(string? exeDirectory = null, string? localAppData = null)
    {
        var exe = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exeDirectory ?? AppLocation.BaseDirectory));
        var local = localAppData ?? LocalAppData();
        var appRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppRoot(local)));
        var portableSettings = Path.Combine(exe, SettingsFileName);
        if (!string.Equals(exe, appRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(portableSettings))
        {
            return new DataDirectories(Path.Combine(exe, PortableDataFolderName), portableSettings, isPortable: true);
        }

        MigrateLegacyLayout(local);
        return new DataDirectories(DefaultRoot(local));
    }

    /// <summary>
    /// Moves data from the old layout (<c>settings.json</c>, <c>api</c>, <c>cache</c>, <c>state</c>, <c>logs</c> directly
    /// in <c>&lt;localAppData&gt;\BlenderLauncherNet</c>) into its <c>data</c> subfolder, once: only when the old
    /// <c>settings.json</c> exists and the new one does not. An entry already present in <c>data</c> is left where it is.
    /// Velopack's files in the same folder are never touched. Returns true when something was moved.
    /// </summary>
    public static bool MigrateLegacyLayout(string localAppData)
    {
        var appRoot = AppRoot(localAppData);
        var dataRoot = DefaultRoot(localAppData);
        if (!File.Exists(Path.Combine(appRoot, SettingsFileName)) || File.Exists(Path.Combine(dataRoot, SettingsFileName)))
        {
            return false;
        }

        var moved = false;
        try
        {
            Directory.CreateDirectory(dataRoot);
            foreach (var name in LegacyEntries)
            {
                var from = Path.Combine(appRoot, name);
                var to = Path.Combine(dataRoot, name);
                if (Path.Exists(to))
                {
                    continue;
                }

                if (File.Exists(from))
                {
                    File.Move(from, to);
                    moved = true;
                }
                else if (Directory.Exists(from))
                {
                    Directory.Move(from, to);
                    moved = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left as it is: the launcher starts with defaults rather than not at all, and the old files stay readable.
        }

        return moved;
    }

    private static string LocalAppData() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}
