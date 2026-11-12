using System.Runtime.InteropServices;

namespace BlenderLauncher.Platform.Windows.Interop;

/// <summary>shell32 SHFileOperationW, used only to send items to the Recycle Bin.</summary>
internal static partial class RecycleBinNative
{
    public const uint FoDelete = 0x0003;
    public const ushort FofSilent = 0x0004;
    public const ushort FofNoConfirmation = 0x0010;
    public const ushort FofAllowUndo = 0x0040;
    public const ushort FofNoErrorUi = 0x0400;
    public const ushort FofWantNukeWarning = 0x4000;

    /// <summary>
    /// SHFILEOPSTRUCTW with natural x64 alignment. On 32-bit Windows the header declares it with 1-byte
    /// packing; the app ships win-x64 only, so that layout is not handled.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ShFileOpStructW
    {
        public nint Hwnd;
        public uint WFunc;
        public char* PFrom;
        public char* PTo;
        public ushort FFlags;
        public int FAnyOperationsAborted;
        public nint HNameMappings;
        public char* LpszProgressTitle;
    }

    [LibraryImport("shell32.dll")]
    public static partial int SHFileOperationW(ref ShFileOpStructW lpFileOp);
}
