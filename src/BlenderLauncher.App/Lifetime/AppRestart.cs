using System.ComponentModel;
using System.Diagnostics;

namespace BlenderLauncher.App.Lifetime;

/// <summary>
/// "Restart now": the command line for the new copy and the hand-over of the single-instance pipe. The old
/// process saves its settings, releases the pipe and starts the new copy with <see cref="ArgumentsFor"/>; the new copy
/// sees <see cref="Marker"/> and retries the pipe for a short while (<see cref="PipeRetry"/>) before treating itself as a
/// second instance, as the old process may still be closing.
/// </summary>
public static class AppRestart
{
    /// <summary>Hidden switch that tells a new copy it was started by "Restart now".</summary>
    public const string Marker = "--restarted";

    /// <summary>How long a restarted copy keeps trying to take the instance pipe.</summary>
    public static TimeSpan PipeRetry { get; } = TimeSpan.FromSeconds(10);

    // One-shot commands: run once, never repeated by a restart.
    private static readonly string[] DroppedSwitches =
        ["-force-first-time", "--force-first-time", "--dev-throw", Marker];

    // Commands that take a value. The self-update's switches belong to the one run they were given to.
    private static readonly string[] DroppedWithValue =
        ["-set-library-folder", "--set-library-folder", SelfUpdate.UpdatedFromSwitch, SelfUpdate.UpdateSourceSwitch];

    // Sub-commands: everything from one of these on is a command, not an option.
    private static readonly string[] SubCommands = ["launch", "register", "unregister", "uninstall", "update"];

    /// <summary>
    /// The new copy's arguments: <paramref name="args"/> without the commands (one-shot switches, library folder,
    /// sub-commands with their arguments, a <c>.blend</c> path), plus <see cref="Marker"/>. Options such as
    /// <c>-debug</c>, <c>--offline</c> or <c>--window-pos X,Y</c> are kept with their values.
    /// </summary>
    public static IReadOnlyList<string> ArgumentsFor(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var result = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (DroppedWithValue.Contains(arg, StringComparer.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            if (DroppedSwitches.Contains(arg, StringComparer.OrdinalIgnoreCase)
                || arg.EndsWith(".blend", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (SubCommands.Contains(arg, StringComparer.OrdinalIgnoreCase))
            {
                break;
            }

            result.Add(arg);
        }

        result.Add(Marker);
        return result;
    }

    public static bool IsRestart(string[] args) => args.Contains(Marker, StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts this executable again with <see cref="ArgumentsFor"/>. Returns false when it could not be started.</summary>
    public static bool StartNewCopy(IReadOnlyList<string> args)
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return false;
        }

        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
        foreach (var arg in ArgumentsFor(args))
        {
            info.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            Trace.TraceError($"Could not restart: {e.Message}");
            return false;
        }
    }
}
