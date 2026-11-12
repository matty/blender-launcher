namespace BlenderLauncher.Core.PlatformServices;

/// <summary>What happened to an item handed to <see cref="IRecycleBin.SendToBin"/>.</summary>
public enum RecycleOutcome
{
    /// <summary>The item is in the Recycle Bin.</summary>
    Recycled,

    /// <summary>
    /// The bin could not take the item (too big, no bin on that volume, path too long for the shell) and the
    /// caller allowed a permanent delete, so it was deleted permanently. <see cref="RecycleResult.Reason"/> says why.
    /// </summary>
    DeletedPermanently,

    /// <summary>
    /// The bin could not take the item, or the user declined the shell's prompt, and nothing was deleted.
    /// <see cref="RecycleResult.Reason"/> says why; the caller may ask and then call
    /// <see cref="IRecycleBin.DeletePermanently"/>.
    /// </summary>
    NotRecycled,

    /// <summary>Nothing exists at the path; counts as success (upstream's <c>RemovalTask</c>).</summary>
    Missing,
}

/// <summary>The result of <see cref="IRecycleBin.SendToBin"/>. <see cref="Reason"/> is null for <see cref="RecycleOutcome.Recycled"/> and <see cref="RecycleOutcome.Missing"/>.</summary>
public readonly record struct RecycleResult(RecycleOutcome Outcome, string? Reason = null)
{
    /// <summary>True when the item is gone, whether binned, deleted or never there.</summary>
    public bool IsGone => Outcome != RecycleOutcome.NotRecycled;
}

/// <summary>
/// The Recycle Bin. Implemented by <c>Platform.Windows</c>; <see cref="NullRecycleBin"/>
/// stands in when no bin is available. Calls are synchronous and may block (the shell, antivirus locks), so run
/// them off the UI thread.
/// </summary>
public interface IRecycleBin
{
    /// <summary>False when this implementation can never recycle (the null bin); the UI can then hide "move to bin" wording.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Moves the file or folder at <paramref name="path"/> (fully qualified) to the Recycle Bin. When the bin cannot
    /// take it, deletes it permanently if <paramref name="allowPermanentDelete"/> is true, otherwise leaves it in
    /// place and returns <see cref="RecycleOutcome.NotRecycled"/>.
    /// </summary>
    /// <exception cref="IOException">The item exists but could not be recycled or deleted (for example, it is locked).</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the item was denied.</exception>
    RecycleResult SendToBin(string path, bool allowPermanentDelete);

    /// <summary>Deletes the file or folder at <paramref name="path"/> permanently, read-only files included. A missing path is not an error.</summary>
    void DeletePermanently(string path);
}

/// <summary>The bin used when the platform has none: never recycles, and deletes permanently only when allowed.</summary>
public sealed class NullRecycleBin : IRecycleBin
{
    public static NullRecycleBin Instance { get; } = new();

    public bool IsAvailable => false;

    public RecycleResult SendToBin(string path, bool allowPermanentDelete)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!Path.Exists(path))
        {
            return new RecycleResult(RecycleOutcome.Missing);
        }

        const string reason = "no Recycle Bin is available";
        if (!allowPermanentDelete)
        {
            return new RecycleResult(RecycleOutcome.NotRecycled, reason);
        }

        PermanentDelete.Run(path);
        return new RecycleResult(RecycleOutcome.DeletedPermanently, reason);
    }

    public void DeletePermanently(string path) => PermanentDelete.Run(path);
}

/// <summary>Permanent deletion shared by the bin implementations.</summary>
public static class PermanentDelete
{
    /// <summary>
    /// Deletes a file or a folder tree. Read-only attributes are cleared first (Blender builds ship some read-only
    /// files, which <see cref="Directory.Delete(string, bool)"/> refuses). A folder that is a junction or symbolic
    /// link is removed as a link; its target is not touched. A missing path is not an error.
    /// </summary>
    public static void Run(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var info = new FileInfo(path);
        if (!info.Exists && !Directory.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }

            File.Delete(path);
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            ClearReadOnly(new DirectoryInfo(path));
        }

        Directory.Delete(path, recursive: true);
    }

    private static void ClearReadOnly(DirectoryInfo folder)
    {
        foreach (var entry in folder.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }

            if (entry is DirectoryInfo sub && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                ClearReadOnly(sub);
            }
        }
    }
}
