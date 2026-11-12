using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BlenderLauncher.Platform.Windows.Interop;

/// <summary>kernel32 process creation, for the flags System.Diagnostics.Process cannot set.</summary>
internal static partial class ProcessNative
{
    public const uint CreateNewConsole = 0x00000010;
    public const uint CreateUnicodeEnvironment = 0x00000400;
    public const uint CreateNoWindow = 0x08000000;
    public const int StartfUseStdHandles = 0x00000100;
    public const int StartfUseShowWindow = 0x00000001;
    public const short SwHide = 0;
    public const uint StillActive = 259;

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct StartupInfoW
    {
        public int Cb;
        public char* LpReserved;
        public char* LpDesktop;
        public char* LpTitle;
        public int DwX;
        public int DwY;
        public int DwXSize;
        public int DwYSize;
        public int DwXCountChars;
        public int DwYCountChars;
        public int DwFillAttribute;
        public int DwFlags;
        public short WShowWindow;
        public short CbReserved2;
        public nint LpReserved2;
        public nint HStdInput;
        public nint HStdOutput;
        public nint HStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint HProcess;
        public nint HThread;
        public uint DwProcessId;
        public uint DwThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool CreateProcessW(
        char* lpApplicationName,
        char* lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        char* lpCurrentDirectory,
        in StartupInfoW lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);
}
