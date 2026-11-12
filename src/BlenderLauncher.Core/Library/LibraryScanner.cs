using System.Runtime.CompilerServices;
using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.Core.Library;

/// <summary>How a folder under a branch folder was classified.</summary>
public enum LibraryEntryKind
{
    /// <summary>Recognised and read: <see cref="LibraryEntry.Build"/> is set.</summary>
    Build,

    /// <summary>No <c>.blinfo</c> and no executable; offered "Initialise" (upstream's <c>UnrecoBuildWidget</c>).</summary>
    Unrecognised,

    /// <summary>Recognised, but reading it failed (upstream's <c>LibraryDamagedWidget</c>); <see cref="LibraryEntry.Error"/> says why.</summary>
    Damaged,
}

/// <summary>A folder found by the scanner: its absolute path and the branch folder holding it.</summary>
public sealed record ScannedFolder(string Path, string ParentFolder, bool IsRecognised, bool HasBlinfo);

/// <summary>One row of the installed list.</summary>
public sealed record LibraryEntry(string Path, string ParentFolder, LibraryEntryKind Kind, BuildInfo? Build = null, Exception? Error = null)
{
    /// <summary>The folder name.</summary>
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Finds builds in a library (a port of <c>threads/library_drawer.py</c> and
/// <c>window.py:758-839</c>). For each branch folder, each child folder is recognised when it holds a
/// <c>.blinfo</c>, <c>blender.exe</c> or <c>bforartists.exe</c>; anything else is unrecognised. Recognised folders
/// are read with a <see cref="BuildInfoReader"/>; a failed read makes the entry damaged.
/// </summary>
public static class LibraryScanner
{
    /// <summary>True when <paramref name="buildFolder"/> holds a <c>.blinfo</c> or a Blender or Bforartists executable.</summary>
    public static bool IsRecognised(string buildFolder) =>
        File.Exists(Path.Combine(buildFolder, BlinfoCodec.FileName)) ||
        File.Exists(Path.Combine(buildFolder, BuildExecutables.Blender)) ||
        File.Exists(Path.Combine(buildFolder, BuildExecutables.Bforartists));

    /// <summary>
    /// Lists the child folders of <paramref name="folders"/> (default <see cref="LibraryLayout.BuildFolders"/>) under
    /// <paramref name="libraryRoot"/>, classifying each. Files, and branch folders that are missing or cannot be
    /// listed, are skipped. Folders not in <see cref="LibraryLayout.AcceptedParents"/> are skipped, as upstream
    /// draws nothing from them.
    /// </summary>
    public static IEnumerable<ScannedFolder> Enumerate(string libraryRoot, IEnumerable<string>? folders = null, CancellationToken ct = default)
    {
        var root = LibraryLayout.NormaliseRoot(libraryRoot);
        foreach (var name in folders ?? LibraryLayout.BuildFolders)
        {
            if (!LibraryLayout.IsAcceptedParent(name))
            {
                continue;
            }

            var parent = Path.Combine(root, name);
            string[] children;
            try
            {
                children = Directory.Exists(parent) ? Directory.GetDirectories(parent) : [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(children, LibraryLayout.PathComparer);
            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                var hasBlinfo = File.Exists(Path.Combine(child, BlinfoCodec.FileName));
                yield return new ScannedFolder(child, name, hasBlinfo || IsRecognised(child), hasBlinfo);
            }
        }
    }

    /// <summary>
    /// Enumerates and reads the library, yielding each entry as soon as it is known. Unrecognised folders come
    /// first in discovery order; builds are read up to <paramref name="parallelism"/> at a time (a build without
    /// a <c>.blinfo</c> runs its executable) and come in completion order. Cancellation stops the scan.
    /// </summary>
    public static async IAsyncEnumerable<LibraryEntry> ScanAsync(
        string libraryRoot,
        BuildInfoReader reader,
        IEnumerable<string>? folders = null,
        int parallelism = 4,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<LibraryEntry>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
        var producer = Task.Run(async () =>
        {
            try
            {
                var found = new List<ScannedFolder>();
                foreach (var scanned in Enumerate(libraryRoot, folders, ct))
                {
                    if (scanned.IsRecognised)
                    {
                        found.Add(scanned);
                    }
                    else
                    {
                        channel.Writer.TryWrite(new LibraryEntry(scanned.Path, scanned.ParentFolder, LibraryEntryKind.Unrecognised));
                    }
                }

                await Parallel.ForEachAsync(
                    found,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = ct },
                    async (scanned, token) => channel.Writer.TryWrite(await ReadEntryAsync(reader, scanned.Path, scanned.ParentFolder, token).ConfigureAwait(false)))
                    .ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception e)
            {
                channel.Writer.TryComplete(e);
            }
        }, CancellationToken.None);

        await foreach (var entry in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return entry;
        }

        await producer.ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one folder into an entry: a build, or damaged when the read throws anything but cancellation;
    /// unrecognised when it holds nothing the scanner recognises.
    /// </summary>
    public static async Task<LibraryEntry> ReadEntryAsync(BuildInfoReader reader, string buildFolder, string? parentFolder = null, CancellationToken ct = default)
    {
        var path = LibraryLayout.NormaliseRoot(buildFolder);
        var parent = parentFolder ?? Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
        if (!IsRecognised(path))
        {
            return new LibraryEntry(path, parent, LibraryEntryKind.Unrecognised);
        }

        try
        {
            var build = await reader.ReadAsync(path, ct: ct).ConfigureAwait(false);
            return new LibraryEntry(path, parent, LibraryEntryKind.Build, build);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new LibraryEntry(path, parent, LibraryEntryKind.Damaged, Error: e);
        }
    }
}
