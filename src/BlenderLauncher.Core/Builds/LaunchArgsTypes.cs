namespace BlenderLauncher.Core.Builds;

/// <summary>What Blender is asked to open (upstream's <c>LaunchMode</c> classes).</summary>
public enum LaunchKind
{
    /// <summary>No extra argument.</summary>
    Plain,

    /// <summary><c>--open-last</c> (upstream <c>LaunchOpenLast</c>).</summary>
    OpenLast,

    /// <summary>A .blend file path (upstream <c>LaunchWithBlendFile</c>).</summary>
    BlendFile,
}

/// <summary>The launch mode: plain, open the last file, or open a given .blend file.</summary>
public sealed record LaunchMode
{
    private LaunchMode(LaunchKind kind, string? blendFile)
    {
        Kind = kind;
        BlendFile = blendFile;
    }

    public LaunchKind Kind { get; }

    /// <summary>The .blend file for <see cref="LaunchKind.BlendFile"/>; null otherwise.</summary>
    public string? BlendFile { get; }

    public static LaunchMode Plain { get; } = new(LaunchKind.Plain, null);

    public static LaunchMode OpenLast { get; } = new(LaunchKind.OpenLast, null);

    public static LaunchMode WithBlendFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new LaunchMode(LaunchKind.BlendFile, path);
    }
}

/// <summary>The inputs of <see cref="LaunchArgsBuilder.Build"/>.</summary>
public sealed record LaunchRequest
{
    /// <summary>The build folder (upstream's <c>library_folder / info.link</c>), absolute.</summary>
    public required string BuildFolder { get; init; }

    /// <summary>The build's <see cref="BuildInfo.CustomExecutable"/>, relative to the build folder (or absolute).</summary>
    public string? CustomExecutable { get; init; }

    /// <summary>
    /// A script or program to run through <c>cmd /C</c> instead of Blender (upstream's <c>exe</c> argument), such as
    /// one of <see cref="LaunchArgsBuilder.DebugScripts"/>; relative to the build folder. Startup arguments are not
    /// passed to it, as upstream.
    /// </summary>
    public string? Script { get; init; }

    /// <summary>The "Blender startup arguments" setting, split by Windows command-line rules.</summary>
    public string StartupArguments { get; init; } = "";

    public LaunchMode Mode { get; init; } = LaunchMode.Plain;

    /// <summary>
    /// Arguments for Blender from the command line (<c>launch … -- ARGS</c>), added last, after the .blend file or
    /// <c>--open-last</c>. Not passed to a <see cref="Script"/>.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>The "hide console" setting (upstream <c>launch_blender_no_console</c>).</summary>
    public bool HideConsole { get; init; }

    /// <summary>A request for an installed build: its folder (<see cref="BuildInfo.Link"/>) and custom executable.</summary>
    public static LaunchRequest For(BuildInfo build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return new LaunchRequest { BuildFolder = build.Link, CustomExecutable = build.CustomExecutable };
    }
}

/// <summary>The result of <see cref="LaunchArgsBuilder.Build"/>.</summary>
/// <param name="Argv">
/// Upstream's argv list: the program (<c>cmd</c> for a batch file) and its arguments, as <c>get_args</c> returns it.
/// </param>
/// <param name="CommandLine">
/// The exact <c>CreateProcess</c> command line. For a program it is <paramref name="Argv"/> quoted by the CRT
/// rules. For a batch file it is <c>cmd /S /C "…"</c>, quoted for cmd.exe; the caller should pass
/// <c>%ComSpec%</c> as the application name so no other <c>cmd.exe</c> is found on the search path.
/// </param>
/// <param name="ApplicationName">The program to start: the executable path, or <c>cmd</c> for a batch file.</param>
/// <param name="UsesCmd">True when the command runs through <c>cmd /C</c>.</param>
/// <param name="RequiresConsole">
/// True for a <see cref="LaunchRequest.Script"/> that is a <c>.cmd</c> or <c>.bat</c>: the debug scripts end with
/// <c>pause</c>, so they always need a visible console.
/// </param>
/// <param name="HideConsole">Whether to start with the console hidden: the request's flag, unless a console is required.</param>
public sealed record LaunchCommand(
    IReadOnlyList<string> Argv,
    string CommandLine,
    string ApplicationName,
    bool UsesCmd,
    bool RequiresConsole,
    bool HideConsole);
