namespace BlenderLauncher.Core.Library;

/// <summary>Whether "Add custom build" copies the chosen folder or moves it.</summary>
public enum CustomImportMode
{
    /// <summary>Copy; the source is left as it is.</summary>
    Copy,

    /// <summary>Move; a rename on the same volume, else a copy followed by deleting the source.</summary>
    Move,
}

/// <summary>Progress of a folder copy.</summary>
public readonly record struct CopyProgress(long BytesDone, long BytesTotal, int FilesDone, int FilesTotal);

/// <summary>Recursive folder copy with progress and cancellation. Reparse points are not followed.</summary>
public static class FolderCopy
{
    private const int BufferSize = 1 << 20;

    /// <summary>
    /// Copies the tree under <paramref name="source"/> into <paramref name="target"/> (created when missing).
    /// Existing files are replaced when <paramref name="overwrite"/> is set, else an <see cref="IOException"/> is
    /// thrown. Symbolic links and junctions are skipped.
    /// </summary>
    public static void CopyTree(string source, string target, bool overwrite, IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        var files = new List<(FileInfo File, string Target)>();
        var directories = new List<string> { target };
        long total = 0;
        var pending = new Stack<(DirectoryInfo Dir, string Target)>();
        pending.Push((new DirectoryInfo(source), target));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, dest) = pending.Pop();
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var to = Path.Combine(dest, entry.Name);
                if (entry is DirectoryInfo sub)
                {
                    directories.Add(to);
                    pending.Push((sub, to));
                }
                else if (entry is FileInfo file)
                {
                    files.Add((file, to));
                    total += file.Length;
                }
            }
        }

        foreach (var dir in directories)
        {
            Directory.CreateDirectory(dir);
        }

        long done = 0;
        var buffer = new byte[BufferSize];
        progress?.Report(new CopyProgress(0, total, 0, files.Count));
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (file, to) = files[i];
            using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            using (var output = new FileStream(to, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    done += read;
                    progress?.Report(new CopyProgress(done, total, i, files.Count));
                }
            }

            try
            {
                File.SetLastWriteTimeUtc(to, file.LastWriteTimeUtc);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            progress?.Report(new CopyProgress(done, total, i + 1, files.Count));
        }
    }
}

/// <summary>
/// "Add custom build" (new in the port, upstream asks the user to copy the folder by hand): copies or moves
/// a chosen folder into the library's <c>custom</c> folder under a free name. A copy is staged in
/// <c>.temp\BlenderLauncherNet</c> and renamed into place, so a scan never sees a half-copied build and a
/// cancelled copy leaves nothing behind. The caller then reads the folder (or opens the custom build dialog when it
/// is unrecognised).
/// </summary>
public static class CustomBuildImporter
{
    /// <summary>
    /// Imports <paramref name="sourceFolder"/> into <c>&lt;library&gt;\custom</c> and returns the new folder's path.
    /// Throws <see cref="DirectoryNotFoundException"/> when the source is missing and <see cref="ArgumentException"/>
    /// when it is the library, inside the library's <c>custom</c> folder already, or contains the library.
    /// </summary>
    public static async Task<string> ImportAsync(
        string sourceFolder,
        string libraryRoot,
        CustomImportMode mode,
        IProgress<CopyProgress>? progress = null,
        CancellationToken ct = default)
    {
        var layout = new LibraryLayout(libraryRoot);
        var source = LibraryLayout.NormaliseRoot(sourceFolder);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Folder not found: {source}");
        }

        if (LibraryLayout.IsSameOrInside(layout.Root, source))
        {
            throw new ArgumentException($"{source} contains the library folder", nameof(sourceFolder));
        }

        if (LibraryLayout.IsSameOrInside(source, layout.CustomFolder))
        {
            throw new ArgumentException($"{source} is already in the custom folder", nameof(sourceFolder));
        }

        Directory.CreateDirectory(layout.CustomFolder);
        var name = Path.GetFileName(source);
        if (name.Length == 0)
        {
            name = "custom-build";
        }

        if (mode == CustomImportMode.Move && SameVolume(source, layout.Root))
        {
            var target = UniqueTarget(layout.CustomFolder, name);
            await BuildConfigFolders.RetryOnLockAsync(() => Directory.Move(source, target), ct).ConfigureAwait(false);
            return target;
        }

        Directory.CreateDirectory(layout.StagingFolder);
        var staging = Path.Combine(layout.StagingFolder, "import-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Task.Run(() => FolderCopy.CopyTree(source, staging, overwrite: false, progress, ct), ct).ConfigureAwait(false);
            var target = UniqueTarget(layout.CustomFolder, name);
            await BuildConfigFolders.RetryOnLockAsync(() => Directory.Move(staging, target), ct).ConfigureAwait(false);
            if (mode == CustomImportMode.Move)
            {
                await Task.Run(() => Directory.Delete(source, recursive: true), CancellationToken.None).ConfigureAwait(false);
            }

            return target;
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>
    /// <paramref name="name"/> under <paramref name="folder"/>, or <c>name (2)</c>, <c>name (3)</c> … when taken
    /// by a file or folder.
    /// </summary>
    public static string UniqueTarget(string folder, string name)
    {
        var candidate = Path.Combine(folder, name);
        for (var n = 2; Directory.Exists(candidate) || File.Exists(candidate); n++)
        {
            candidate = Path.Combine(folder, $"{name} ({n})");
        }

        return candidate;
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
