using System.ComponentModel;
using BlenderLauncher.Platform.Windows.Cli;
using BlenderLauncher.Platform.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace BlenderLauncher.Platform.Windows.Processes;

/// <summary>
/// Starts a program that shares this process's console and standard handles (<c>launch -c</c>, upstream's
/// <c>Popen(shell=True)</c>), so its output appears where the launcher's does:
/// <list type="bullet">
/// <item>With standard handles redirected to files or pipes, they are passed on (<c>STARTF_USESTDHANDLES</c>), and
/// without a console the program runs with none (<c>CREATE_NO_WINDOW</c>) instead of opening a console window.</item>
/// <item>Otherwise the program simply inherits the console this process attached to.</item>
/// </list>
/// .NET's Process passes the standard handles only when it redirects them itself, so a console program started from
/// a GUI-subsystem process with redirected output would open its own console window.
/// </summary>
public static class SharedConsoleLauncher
{
    /// <exception cref="Win32Exception">CreateProcessW failed.</exception>
    public static unsafe LaunchedProcess Start(string fileName, string commandLine, string? workingDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentException.ThrowIfNullOrEmpty(commandLine);

        var redirected = ParentConsole.IsRedirected(ConsoleNative.StdOutputHandle)
            || ParentConsole.IsRedirected(ConsoleNative.StdErrorHandle)
            || ParentConsole.IsRedirected(ConsoleNative.StdInputHandle);
        var startup = new ProcessNative.StartupInfoW { Cb = sizeof(ProcessNative.StartupInfoW) };
        if (redirected)
        {
            startup.DwFlags = ProcessNative.StartfUseStdHandles;
            startup.HStdInput = ConsoleNative.GetStdHandle(ConsoleNative.StdInputHandle);
            startup.HStdOutput = ConsoleNative.GetStdHandle(ConsoleNative.StdOutputHandle);
            startup.HStdError = ConsoleNative.GetStdHandle(ConsoleNative.StdErrorHandle);
        }

        var flags = ProcessNative.CreateUnicodeEnvironment;
        if (ConsoleNative.GetConsoleWindow() == 0)
        {
            flags |= ProcessNative.CreateNoWindow;
        }

        var commandBuffer = new char[commandLine.Length + 1];
        commandLine.CopyTo(commandBuffer);
        ProcessNative.ProcessInformation info;
        bool ok;
        fixed (char* app = fileName)
        fixed (char* cmd = commandBuffer)
        fixed (char* dir = workingDirectory)
        {
            ok = ProcessNative.CreateProcessW(app, cmd, 0, 0, bInheritHandles: redirected, flags, 0, dir, in startup, out info);
        }

        if (!ok)
        {
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError(), $"CreateProcessW failed for {fileName}");
        }

        ProcessNative.CloseHandle(info.HThread);
        return new LaunchedProcess(new SafeProcessHandle(info.HProcess, ownsHandle: true), (int)info.DwProcessId);
    }
}
