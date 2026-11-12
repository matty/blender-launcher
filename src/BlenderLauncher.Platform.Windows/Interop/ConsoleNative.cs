using System.Runtime.InteropServices;

namespace BlenderLauncher.Platform.Windows.Interop;

/// <summary>kernel32 console functions for attaching a GUI-subsystem process to its parent's console.</summary>
internal static partial class ConsoleNative
{
    public const uint AttachParentProcess = 0xFFFFFFFF;
    public const int StdInputHandle = -10;
    public const int StdOutputHandle = -11;
    public const int StdErrorHandle = -12;
    public const uint FileTypeChar = 0x0002;
    public const int ErrorAccessDenied = 5;
    public const int ErrorInvalidHandle = 6;
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareReadWrite = 0x3;
    public const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct ConsoleScreenBufferInfo
    {
        public short SizeX;
        public short SizeY;
        public short CursorX;
        public short CursorY;
        public ushort Attributes;
        public short WindowLeft;
        public short WindowTop;
        public short WindowRight;
        public short WindowBottom;
        public short MaxSizeX;
        public short MaxSizeY;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FreeConsole();

    [LibraryImport("kernel32.dll")]
    public static partial nint GetConsoleWindow();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetStdHandle(int nStdHandle, nint hHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetFileType(nint hFile);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "WriteConsoleW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool WriteConsole(
        nint hConsoleOutput, char* lpBuffer, uint nNumberOfCharsToWrite, out uint lpNumberOfCharsWritten, nint lpReserved);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetConsoleScreenBufferInfo(nint hConsoleOutput, out ConsoleScreenBufferInfo lpConsoleScreenBufferInfo);

    /// <summary>dwReadCoord is a COORD passed by value: X in the low word, Y in the high word.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "ReadConsoleOutputCharacterW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool ReadConsoleOutputCharacter(
        nint hConsoleOutput, char* lpCharacter, uint nLength, uint dwReadCoord, out uint lpNumberOfCharsRead);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);
}
