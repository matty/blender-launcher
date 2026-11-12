using System.Runtime.InteropServices;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Platform.Windows.Interop;

namespace BlenderLauncher.Platform.Windows.Processes;

/// <summary>
/// <see cref="IProcessLauncher"/> for Windows: <c>CreateProcessW</c> through
/// <see cref="HiddenConsoleLauncher"/>, so every build gets its own console (<c>CREATE_NEW_CONSOLE</c>), hidden or
/// shown per <see cref="LaunchCommand.HideConsole"/> (a Debug-menu script, <see cref="LaunchCommand.RequiresConsole"/>,
/// is always shown), inherits no handles and gets the launcher's environment unchanged. A batch file runs with
/// <c>%ComSpec%</c> as the application name, so no other <c>cmd.exe</c> on the search path
/// can be picked up.
/// </summary>
public sealed class WindowsProcessLauncher : IProcessLauncher
{
    public bool IsAvailable => true;

    public ILaunchedProcess Start(LaunchCommand command, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        var application = command.UsesCmd ? ComSpec() : command.ApplicationName;
        return HiddenConsoleLauncher.Start(application, command.CommandLine, workingDirectory, command.HideConsole && !command.RequiresConsole);
    }

    /// <summary>
    /// Posts <c>WM_CLOSE</c> to each visible, unowned top-level window of the processes, as the title bar's close
    /// button does. Console windows (<c>ConsoleWindowClass</c>) are skipped: closing one ends the process without
    /// asking.
    /// </summary>
    public int RequestClose(IReadOnlyCollection<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        if (processIds.Count == 0)
        {
            return 0;
        }

        var asked = 0;
        foreach (var window in FindTopLevelWindows(processIds))
        {
            if (WindowNative.PostMessageW(window, WindowNative.WmClose, 0, 0))
            {
                asked++;
            }
        }

        return asked;
    }

    /// <summary>The visible, unowned, non-console top-level windows of <paramref name="processIds"/>.</summary>
    public static IReadOnlyList<nint> FindTopLevelWindows(IReadOnlyCollection<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        var search = new WindowSearch([.. processIds.Select(id => unchecked((uint)id))]);
        var handle = GCHandle.Alloc(search);
        try
        {
            unsafe
            {
                WindowNative.EnumWindows(&EnumWindowsProc, GCHandle.ToIntPtr(handle));
            }
        }
        finally
        {
            handle.Free();
        }

        return search.Found;
    }

    /// <summary><c>%ComSpec%</c> when it is a full path, else <c>System32\cmd.exe</c>.</summary>
    public static string ComSpec()
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec");
        return !string.IsNullOrWhiteSpace(comSpec) && Path.IsPathFullyQualified(comSpec)
            ? comSpec
            : Path.Combine(Environment.SystemDirectory, "cmd.exe");
    }

    [UnmanagedCallersOnly]
    private static int EnumWindowsProc(nint window, nint state)
    {
        try
        {
            var search = (WindowSearch)GCHandle.FromIntPtr(state).Target!;
            WindowNative.GetWindowThreadProcessId(window, out var pid);
            if (search.ProcessIds.Contains(pid)
                && WindowNative.IsWindowVisible(window)
                && WindowNative.GetWindow(window, WindowNative.GwOwner) == 0
                && !IsConsoleWindow(window))
            {
                search.Found.Add(window);
            }
        }
        catch
        {
            // An exception must not cross the native boundary; skip the window.
        }

        return 1;
    }

    private static unsafe bool IsConsoleWindow(nint window)
    {
        const int capacity = 64;
        var buffer = stackalloc char[capacity];
        var length = WindowNative.GetClassNameW(window, buffer, capacity);
        if (length <= 0)
        {
            return false;
        }

        var name = new ReadOnlySpan<char>(buffer, length);
        return name.SequenceEqual("ConsoleWindowClass") || name.SequenceEqual("PseudoConsoleWindow");
    }

    private sealed class WindowSearch(HashSet<uint> processIds)
    {
        public HashSet<uint> ProcessIds { get; } = processIds;

        public List<nint> Found { get; } = [];
    }
}
