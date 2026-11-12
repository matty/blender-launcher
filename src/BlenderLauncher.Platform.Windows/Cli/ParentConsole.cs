using System.Runtime.InteropServices;
using BlenderLauncher.Platform.Windows.Interop;

namespace BlenderLauncher.Platform.Windows.Cli;

public enum ConsoleAttachOutcome
{
    /// <summary>Attached to the parent's console; standard output and error now point at it.</summary>
    Attached,

    /// <summary>Standard output was already a file or pipe, so it was left alone and nothing was attached.</summary>
    Redirected,

    /// <summary>The parent has no console (started from Explorer, a shortcut or a GUI app).</summary>
    NoParentConsole,

    /// <summary>The process already has a console.</summary>
    AlreadyAttached,

    Failed,
}

/// <summary>
/// Lets the GUI-subsystem exe print to the console it was started from. Call
/// <see cref="TryAttach"/> before the first use of <see cref="Console"/>, and only for command-line
/// verbs that print. The console allocation policy manifest element is not used: it needs Windows 11 24H2.
/// </summary>
public static class ParentConsole
{
    /// <summary>Attaches to the parent's console unless standard output is already redirected.</summary>
    public static ConsoleAttachOutcome TryAttach(out int win32Error)
    {
        win32Error = 0;
        if (IsRedirected(ConsoleNative.StdOutputHandle))
        {
            return ConsoleAttachOutcome.Redirected;
        }

        if (!ConsoleNative.AttachConsole(ConsoleNative.AttachParentProcess))
        {
            win32Error = Marshal.GetLastPInvokeError();
            return win32Error switch
            {
                ConsoleNative.ErrorInvalidHandle => ConsoleAttachOutcome.NoParentConsole,
                ConsoleNative.ErrorAccessDenied => ConsoleAttachOutcome.AlreadyAttached,
                _ => ConsoleAttachOutcome.Failed,
            };
        }

        // A GUI process started without redirection has no standard handles; point them at the console
        // so that Console.Out, created lazily on first use, writes there. Error is left alone if redirected.
        EnsureConsoleHandle(ConsoleNative.StdOutputHandle);
        if (!IsRedirected(ConsoleNative.StdErrorHandle))
        {
            EnsureConsoleHandle(ConsoleNative.StdErrorHandle);
        }

        return ConsoleAttachOutcome.Attached;
    }

    /// <summary>
    /// After <see cref="TryAttach"/> returned <see cref="ConsoleAttachOutcome.Attached"/>: points standard input at the
    /// console too, unless it is already a console or redirected, so <c>Console.In</c> can read what the user types.
    /// The parent shell (cmd, PowerShell) does not wait for a GUI-subsystem exe and reads the same console, so the
    /// caller should tell the user to start it with <c>start /wait</c> or through a pipe.
    /// </summary>
    public static void EnsureConsoleInput()
    {
        var current = ConsoleNative.GetStdHandle(ConsoleNative.StdInputHandle);
        if (current is not (0 or -1) && ConsoleNative.GetFileType(current) != 0)
        {
            return;
        }

        var conin = ConsoleNative.CreateFile(
            "CONIN$", ConsoleNative.GenericRead | ConsoleNative.GenericWrite, ConsoleNative.FileShareReadWrite,
            0, ConsoleNative.OpenExisting, 0, 0);
        if (conin is not (0 or -1))
        {
            ConsoleNative.SetStdHandle(ConsoleNative.StdInputHandle, conin);
        }
    }

    /// <summary>Detaches from the console. Handles opened by <see cref="TryAttach"/> stay open until exit.</summary>
    public static void Detach() => ConsoleNative.FreeConsole();

    /// <summary>The console window, or zero. Under Windows Terminal this is a hidden pseudo-window.</summary>
    public static nint ConsoleWindow => ConsoleNative.GetConsoleWindow();

    /// <summary>True when the standard handle is a file or pipe rather than a console or nothing.</summary>
    public static bool IsRedirected(int stdHandle)
    {
        var handle = ConsoleNative.GetStdHandle(stdHandle);
        if (handle is 0 or -1)
        {
            return false;
        }

        // FILE_TYPE_UNKNOWN for a stale handle counts as not redirected; FILE_TYPE_CHAR is a console.
        var type = ConsoleNative.GetFileType(handle);
        return type is not (0 or ConsoleNative.FileTypeChar);
    }

    private static void EnsureConsoleHandle(int stdHandle)
    {
        var current = ConsoleNative.GetStdHandle(stdHandle);
        if (current is not (0 or -1) && ConsoleNative.GetFileType(current) == ConsoleNative.FileTypeChar)
        {
            return;
        }

        var conout = ConsoleNative.CreateFile(
            "CONOUT$", ConsoleNative.GenericRead | ConsoleNative.GenericWrite, ConsoleNative.FileShareReadWrite,
            0, ConsoleNative.OpenExisting, 0, 0);
        if (conout is not (0 or -1))
        {
            ConsoleNative.SetStdHandle(stdHandle, conout);
        }
    }
}
