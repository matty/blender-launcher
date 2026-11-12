using System.Text;
using BlenderLauncher.Core.Processes;

namespace BlenderLauncher.Core.Builds;

/// <summary>
/// Upstream's Windows branch of <c>get_args</c> and <c>launch_build</c> (<c>modules/build_info.py:681-799</c>): the
/// command that starts a build. Pure: it touches the file system only through the injected file-exists check and
/// starts nothing.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Executable: the custom executable, else <c>bforartists.exe</c> if it exists, else <c>blender.exe</c>.</item>
/// <item>A <c>.bat</c> or <c>.cmd</c> executable, and any <see cref="LaunchRequest.Script"/>, run as
/// <c>cmd /C path …</c>.</item>
/// <item>Startup arguments follow the executable (not a script), then <c>--open-last</c> or the .blend path.</item>
/// <item>Paths keep native separators, so UNC paths survive (<c>path_arg</c>).</item>
/// </list>
/// Startup arguments are split by <see cref="WindowsCommandLine.SplitArguments"/>, so quotes
/// group words, where upstream split on single spaces. Text without quotes, separated by single spaces, gives
/// upstream's result; runs of spaces, and leading or trailing spaces, no longer produce empty arguments, and a tab
/// now separates.
/// </remarks>
public static class LaunchArgsBuilder
{
    /// <summary>The Debug menu's scripts, shipped in Windows builds; each ends with <c>pause</c>.</summary>
    public static IReadOnlyList<string> DebugScripts { get; } =
    [
        "blender_debug_log.cmd",
        "blender_factory_startup.cmd",
        "blender_debug_gpu.cmd",
        "blender_debug_gpu_glitchworkaround.cmd",
    ];

    /// <summary>Builds the launch command; <paramref name="fileExists"/> is asked only about <c>bforartists.exe</c>.</summary>
    public static LaunchCommand Build(LaunchRequest request, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fileExists);

        var argv = new List<string>();
        bool usesCmd;
        var requiresConsole = false;
        if (!string.IsNullOrEmpty(request.Script))
        {
            argv.AddRange(["cmd", "/C", Join(request.BuildFolder, request.Script)]);
            usesCmd = true;
            requiresConsole = IsBatchFile(request.Script);
        }
        else
        {
            var exe = ResolveExecutable(request.BuildFolder, request.CustomExecutable, fileExists);
            usesCmd = IsBatchFile(exe);
            if (usesCmd)
            {
                argv.AddRange(["cmd", "/C"]);
            }

            argv.Add(exe);
            argv.AddRange(WindowsCommandLine.SplitArguments(request.StartupArguments));
        }

        switch (request.Mode.Kind)
        {
            case LaunchKind.BlendFile:
                argv.Add(NativePath(request.Mode.BlendFile!));
                break;
            case LaunchKind.OpenLast:
                argv.Add("--open-last");
                break;
        }

        if (string.IsNullOrEmpty(request.Script))
        {
            argv.AddRange(request.ExtraArguments);
        }

        var commandLine = usesCmd
            ? "cmd /S /C " + WindowsCommandLine.BuildCmdArgument(argv.Skip(2))
            : WindowsCommandLine.Build(argv[0], argv.Skip(1));

        return new LaunchCommand(
            argv,
            commandLine,
            argv[0],
            usesCmd,
            requiresConsole,
            request.HideConsole && !requiresConsole);
    }

    /// <summary>
    /// The executable: <paramref name="customExecutable"/> under the build folder when set (upstream treats an
    /// empty one as unset), else <c>bforartists.exe</c> when <paramref name="fileExists"/> says it exists, else
    /// <c>blender.exe</c>. The result has native separators.
    /// </summary>
    public static string ResolveExecutable(string buildFolder, string? customExecutable, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(buildFolder);
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!string.IsNullOrEmpty(customExecutable))
        {
            return Join(buildFolder, customExecutable);
        }

        var bforartists = Join(buildFolder, "bforartists.exe");
        return fileExists(bforartists) ? bforartists : Join(buildFolder, "blender.exe");
    }

    private static bool IsBatchFile(string path) =>
        path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>str(PureWindowsPath(folder) / relative)</c>: a relative path with a drive or a UNC prefix replaces the
    /// folder, one starting with a separator keeps only the folder's drive; the result is <see cref="NativePath"/>.
    /// </summary>
    internal static string Join(string folder, string relative)
    {
        var rel = relative.Replace('/', '\\');
        if (HasDrive(rel) || rel.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return NativePath(rel);
        }

        if (rel.StartsWith('\\'))
        {
            return NativePath(DriveOf(folder.Replace('/', '\\')) + rel);
        }

        return NativePath(folder + "\\" + rel);
    }

    /// <summary>
    /// <c>str(PureWindowsPath(path))</c>: forward slashes become backslashes, repeated separators collapse (except
    /// the two that start a UNC path), <c>.</c> segments and a trailing separator are dropped (except after a
    /// drive root), and <c>..</c> is kept.
    /// </summary>
    internal static string NativePath(string path)
    {
        var p = path.Replace('/', '\\');
        string prefix;
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            prefix = @"\\";
            p = p.TrimStart('\\');
        }
        else if (HasDrive(p))
        {
            prefix = p[..2];
            p = p[2..];
            if (p.StartsWith('\\'))
            {
                prefix += "\\";
            }
        }
        else
        {
            prefix = p.StartsWith('\\') ? "\\" : "";
        }

        var parts = p.Split('\\', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".");
        var sb = new StringBuilder(prefix);
        sb.AppendJoin('\\', parts);
        return sb.Length == 0 ? "." : sb.ToString();
    }

    private static bool HasDrive(string path) => path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]);

    private static string DriveOf(string path)
    {
        if (HasDrive(path))
        {
            return path[..2];
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\server\share
            var parts = path.TrimStart('\\').Split('\\');
            return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : path;
        }

        return "";
    }
}
