using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using BlenderLauncher.Platform.Windows.Interop;

namespace BlenderLauncher.Platform.Windows.FileSystem;

/// <summary>
/// Directory junctions (for <c>bl_symlink</c>). .NET can create symbolic links, which
/// need Developer Mode or admin rights, but not junctions, which need neither. This writes the
/// mount-point reparse point directly instead of shelling out to <c>mklink /J</c>. Reading the target
/// and deleting the junction use the framework (<see cref="FileSystemInfo.LinkTarget"/>,
/// <see cref="Directory.Delete(string)"/>, which removes the link and not the target).
/// </summary>
public static class Junction
{
    /// <summary>Creates a junction at <paramref name="junctionPath"/> pointing at the directory <paramref name="targetPath"/>.</summary>
    /// <exception cref="IOException">Something already exists at <paramref name="junctionPath"/>.</exception>
    /// <exception cref="Win32Exception">Writing the reparse point failed.</exception>
    public static unsafe void Create(string junctionPath, string targetPath)
    {
        var link = Path.GetFullPath(junctionPath);
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        if (Path.Exists(link))
        {
            throw new IOException($"{link} already exists");
        }

        // Substitute name in NT form, print name as the user sees it; both null-terminated.
        var substitute = @"\??\" + target;
        var substituteBytes = substitute.Length * 2;
        var printBytes = target.Length * 2;
        var pathBufferBytes = substituteBytes + 2 + printBytes + 2;
        const int headerBytes = 8; // ReparseTag, ReparseDataLength, Reserved
        const int mountPointHeaderBytes = 8; // four USHORT offsets and lengths
        var dataLength = mountPointHeaderBytes + pathBufferBytes;
        var buffer = new byte[headerBytes + dataLength];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, JunctionNative.IoReparseTagMountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], checked((ushort)dataLength));
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)substituteBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)(substituteBytes + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)printBytes);
        MemoryMarshal.AsBytes(substitute.AsSpan()).CopyTo(span[16..]);
        MemoryMarshal.AsBytes(target.AsSpan()).CopyTo(span[(16 + substituteBytes + 2)..]);

        Directory.CreateDirectory(link);
        try
        {
            using var handle = JunctionNative.CreateFile(
                link, JunctionNative.GenericWrite, 0, 0, JunctionNative.OpenExisting,
                JunctionNative.FileFlagBackupSemantics | JunctionNative.FileFlagOpenReparsePoint, 0);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not open {link}");
            }

            fixed (byte* p = buffer)
            {
                if (!JunctionNative.DeviceIoControl(
                        handle, JunctionNative.FsctlSetReparsePoint, p, (uint)buffer.Length, null, 0, out _, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"FSCTL_SET_REPARSE_POINT failed for {link}");
                }
            }
        }
        catch
        {
            Directory.Delete(link);
            throw;
        }
    }
}
