using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Platform.Windows.Interop;

namespace BlenderLauncher.Platform.Windows.FileSystem;

/// <summary>
/// <see cref="IRecycleBin"/> over <c>SHFileOperationW</c> (spike S11). One item per call, fully
/// qualified, double-null-terminated.
/// <list type="bullet">
/// <item>Volumes that are not fixed drives (network shares, removable media) have no bin; the item is then
/// deleted permanently only when the caller allows it.</item>
/// <item>Paths of 260 characters or more are refused by the shell (S11); treated the same way. The
/// <c>IFileOperation</c> fallback from S11 is not implemented yet.</item>
/// <item>Items too big for the bin raise the shell's own "delete permanently?" prompt
/// (<c>FOF_WANTNUKEWARNING</c>), so the user is asked before anything is deleted permanently. Declining
/// returns <see cref="RecycleOutcome.NotRecycled"/>. The shell does not say afterwards whether an accepted
/// prompt deleted permanently, so that case reports <see cref="RecycleOutcome.Recycled"/>.</item>
/// </list>
/// </summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    public bool IsAvailable => true;

    public RecycleResult SendToBin(string path, bool allowPermanentDelete)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"Not a fully qualified path: {path}", nameof(path));
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Path.Exists(full))
        {
            return new RecycleResult(RecycleOutcome.Missing);
        }

        if (UnbinnableReason(full) is { } reason)
        {
            if (!allowPermanentDelete)
            {
                return new RecycleResult(RecycleOutcome.NotRecycled, reason);
            }

            PermanentDelete.Run(full);
            return new RecycleResult(RecycleOutcome.DeletedPermanently, reason);
        }

        var (code, aborted) = ShellDelete(full);
        if (!Path.Exists(full))
        {
            return new RecycleResult(RecycleOutcome.Recycled);
        }

        if (aborted)
        {
            return new RecycleResult(RecycleOutcome.NotRecycled, "the deletion was cancelled");
        }

        // Legacy DE_* codes overlap Win32 codes below 0x71; map those to an HRESULT so callers can recognise
        // sharing violations and retry.
        var exception = new IOException($"Could not move {full} to the Recycle Bin (SHFileOperationW returned 0x{code:X})");
        if (code is > 0 and < 0x71)
        {
            exception.HResult = unchecked((int)0x80070000 | code);
        }

        throw exception;
    }

    public void DeletePermanently(string path) => PermanentDelete.Run(path);

    private static string? UnbinnableReason(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return "network locations have no Recycle Bin";
        }

        try
        {
            var root = Path.GetPathRoot(path);
            if (root is not null && new DriveInfo(root).DriveType != DriveType.Fixed)
            {
                return "this drive has no Recycle Bin";
            }
        }
        catch (ArgumentException)
        {
            return "this drive has no Recycle Bin";
        }

        if (path.Length >= RecycleBin.MaxPathLength)
        {
            return $"the path is {path.Length} characters long, too long for the Recycle Bin (fewer than {RecycleBin.MaxPathLength})";
        }

        return null;
    }

    private static unsafe (int Code, bool Aborted) ShellDelete(string path)
    {
        var buffer = path + "\0\0";
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
            return (result, op.FAnyOperationsAborted != 0);
        }
    }
}
