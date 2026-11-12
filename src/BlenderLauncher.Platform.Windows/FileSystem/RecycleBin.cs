using System.Text;
using BlenderLauncher.Platform.Windows.Interop;

namespace BlenderLauncher.Platform.Windows.FileSystem;

/// <summary>Sends files and folders to the Recycle Bin through SHFileOperationW.</summary>
public static class RecycleBin
{
    /// <summary>SHFileOperationW does not accept paths of this length or longer, nor the \\?\ prefix.</summary>
    public const int MaxPathLength = 260;

    /// <summary>
    /// Moves the given items to the Recycle Bin without confirmation or progress UI. Items too large for
    /// the bin raise the shell's "delete permanently?" prompt (<c>FOF_WANTNUKEWARNING</c>) rather than
    /// being deleted silently.
    /// </summary>
    /// <returns>Null on success, otherwise a description of the failure.</returns>
    public static unsafe string? TrySend(IReadOnlyCollection<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return null;
        }

        var list = new StringBuilder();
        foreach (var path in paths)
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return $"not a fully qualified path: {path}";
            }

            if (path.Length >= MaxPathLength)
            {
                return $"path is {path.Length} characters, SHFileOperationW needs fewer than {MaxPathLength}: {path}";
            }

            // Each path is null-terminated, and the list ends with an extra null.
            list.Append(path.TrimEnd('\\')).Append('\0');
        }

        list.Append('\0');
        var buffer = list.ToString();

        fixed (char* from = buffer)
        {
            var op = new RecycleBinNative.ShFileOpStructW
            {
                WFunc = RecycleBinNative.FoDelete,
                PFrom = from,
                FFlags = RecycleBinNative.FofAllowUndo | RecycleBinNative.FofNoConfirmation | RecycleBinNative.FofSilent
                         | RecycleBinNative.FofNoErrorUi | RecycleBinNative.FofWantNukeWarning,
            };
            var result = RecycleBinNative.SHFileOperationW(ref op);
            if (result != 0)
            {
                // Legacy DE_* codes (some overlap Win32 codes); only the number is meaningful.
                return $"SHFileOperationW returned 0x{result:X}";
            }

            if (op.FAnyOperationsAborted != 0)
            {
                return "the operation was cancelled";
            }
        }

        return null;
    }
}
