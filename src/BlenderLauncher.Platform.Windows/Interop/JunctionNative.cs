using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BlenderLauncher.Platform.Windows.Interop;

/// <summary>kernel32 calls for writing a mount-point (junction) reparse point onto an empty directory.</summary>
internal static partial class JunctionNative
{
    public const uint GenericWrite = 0x40000000;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileFlagOpenReparsePoint = 0x00200000;
    public const uint FsctlSetReparsePoint = 0x000900A4;
    public const uint IoReparseTagMountPoint = 0xA0000003;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, void* lpInBuffer, uint nInBufferSize,
        void* lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, nint lpOverlapped);
}
