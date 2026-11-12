using System.ComponentModel;
using BlenderLauncher.Platform.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace BlenderLauncher.Platform.Windows.Processes;

/// <summary>
/// Starts a program the way upstream starts Blender: its own new console (<c>CREATE_NEW_CONSOLE</c>),
/// shown hidden (<c>SW_HIDE</c>) unless asked otherwise, inheriting no handles from the launcher and with the
/// launcher's environment. .NET's Process cannot do
/// this: it never sets <c>CREATE_NEW_CONSOLE</c> and always inherits handles. The
/// hidden console keeps Blender's "Toggle System Console" working.
/// </summary>
public static class HiddenConsoleLauncher
{
    /// <summary>Starts <paramref name="fileName"/>. The returned object owns the process handle.</summary>
    /// <param name="fileName">Fully qualified path of the program.</param>
    /// <param name="commandLine">
    /// The full command line, including the quoted program name as argument zero. Use
    /// <see cref="CommandLine.Build"/> for ordinary programs.
    /// </param>
    /// <param name="workingDirectory">Working directory, or null for the launcher's.</param>
    /// <param name="hideConsole">
    /// True: the new console starts hidden (upstream's <c>launch_blender_no_console</c>). False: it is shown as
    /// Windows chooses (no <c>STARTF_USESHOWWINDOW</c>).
    /// </param>
    /// <exception cref="Win32Exception">CreateProcessW failed.</exception>
    public static unsafe LaunchedProcess Start(string fileName, string commandLine, string? workingDirectory = null, bool hideConsole = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentException.ThrowIfNullOrEmpty(commandLine);

        var startup = new ProcessNative.StartupInfoW
        {
            Cb = sizeof(ProcessNative.StartupInfoW),
            DwFlags = hideConsole ? ProcessNative.StartfUseShowWindow : 0,
            WShowWindow = hideConsole ? ProcessNative.SwHide : (short)0,
        };

        // CreateProcessW may write to the command-line buffer, so it must be a private, writable copy.
        var commandBuffer = new char[commandLine.Length + 1];
        commandLine.CopyTo(commandBuffer);

        ProcessNative.ProcessInformation info;
        bool ok;
        fixed (char* app = fileName)
        fixed (char* cmd = commandBuffer)
        fixed (char* dir = workingDirectory)
        {
            ok = ProcessNative.CreateProcessW(
                app,
                cmd,
                0,
                0,
                bInheritHandles: false,
                ProcessNative.CreateNewConsole | ProcessNative.CreateUnicodeEnvironment,
                0,
                dir,
                in startup,
                out info);
        }

        if (!ok)
        {
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError(), $"CreateProcessW failed for {fileName}");
        }

        ProcessNative.CloseHandle(info.HThread);
        return new LaunchedProcess(new SafeProcessHandle(info.HProcess, ownsHandle: true), (int)info.DwProcessId);
    }
}
