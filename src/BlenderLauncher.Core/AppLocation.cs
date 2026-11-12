namespace BlenderLauncher.Core;

/// <summary>How this copy of the launcher was put on the machine.</summary>
public enum InstallKind
{
    /// <summary>A loose exe (a development build or files copied by hand): no Velopack around it.</summary>
    Loose,

    /// <summary>Installed by Velopack's Setup.exe under <c>%LOCALAPPDATA%</c>; Velopack owns updates and uninstall.</summary>
    Installed,

    /// <summary>Velopack's portable zip, extracted anywhere; Velopack owns updates, the user deletes the folder.</summary>
    Portable,
}

/// <summary>
/// Where this copy of the launcher lives. Velopack runs the exe from <c>&lt;root&gt;\current</c> and replaces that folder
/// on every update, so anything meant to last (portable settings, a relative library folder) is resolved against
/// <see cref="BaseDirectory"/>, the Velopack root, rather than the exe's folder. The App sets this once at start-up
/// (<c>VelopackStartup</c>); until then, and for loose copies, everything is the exe's folder.
/// </summary>
public static class AppLocation
{
    private static State _state = new(InstallKind.Loose, null, null);

    /// <summary>How this copy was installed.</summary>
    public static InstallKind Kind => _state.Kind;

    /// <summary>True when Velopack manages this copy (installed or portable): updates go through it.</summary>
    public static bool IsVelopack => _state.Kind != InstallKind.Loose;

    /// <summary>The folder portable settings and relative paths are resolved against: the Velopack root, or the exe's folder.</summary>
    public static string BaseDirectory => _state.Root ?? AppContext.BaseDirectory;

    /// <summary>Velopack's <c>Update.exe</c>, when Velopack manages this copy.</summary>
    public static string? UpdateExePath => _state.UpdateExe;

    /// <summary>
    /// The folder Velopack deletes: the whole root for an installed copy (on uninstall), else <c>current</c> (on every
    /// update). Null for a loose copy. A library must never be inside it.
    /// </summary>
    public static string? VolatileDirectory => _state.Kind switch
    {
        InstallKind.Installed => _state.Root,
        InstallKind.Portable => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
        _ => null,
    };

    /// <summary>Records how this copy was installed. Called once by the App before anything reads the settings.</summary>
    public static void Set(InstallKind kind, string? root, string? updateExe)
    {
        if (kind != InstallKind.Loose)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(root);
        }

        _state = new State(kind, root is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), updateExe);
    }

    private sealed record State(InstallKind Kind, string? Root, string? UpdateExe);
}
