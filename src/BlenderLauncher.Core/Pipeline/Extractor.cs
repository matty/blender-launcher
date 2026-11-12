using System.IO.Compression;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.PlatformServices;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace BlenderLauncher.Core.Pipeline;

/// <summary>What to extract.</summary>
/// <param name="ArchivePath">The downloaded archive, normally inside <paramref name="StagingFolder"/>.</param>
/// <param name="StagingFolder">Where the extraction folder is made (the port's staging folder, same volume as the library).</param>
public sealed record ExtractRequest(string ArchivePath, string StagingFolder);

/// <summary>
/// An archive extracted into staging, not yet in the library. <see cref="BuildFolder"/> lies inside
/// <see cref="WorkFolder"/>; <see cref="LooseFiles"/> are files that sat beside the build's top folder in the
/// archive (Bforartists patch notes) and go into the channel folder with it, as upstream extracted them there.
/// </summary>
public sealed record StagedBuild(string WorkFolder, string BuildFolder, IReadOnlyList<string> LooseFiles)
{
    public string BuildFolderName => Path.GetFileName(BuildFolder);
}

/// <summary>Where a build ended up.</summary>
/// <param name="BuildFolder">The build's folder in the channel folder.</param>
/// <param name="ReplacedExisting">A folder of that name was there and was sent to the Recycle Bin first (upstream's <c>is_removed</c>).</param>
public sealed record ExtractResult(string BuildFolder, bool ReplacedExisting);

/// <summary>
/// Extracts a build archive (port of <c>threads/extractor.py</c>), in two steps so a
/// half-extracted build never appears in the library:
/// <list type="number">
/// <item><see cref="ExtractToStagingAsync"/>: every entry name is checked first (<see cref="ArchivePaths.Resolve"/>),
/// then the archive is extracted into a fresh folder under staging. Zip uses System.IO.Compression with
/// per-entry, per-chunk progress; 7z uses SharpCompress's solid-friendly reader (<c>ExtractAllEntries</c>), also
/// with progress. The top folder is found as upstream does; an archive without a single top folder is wrapped in
/// a folder named after the archive (upstream took the first entry's folder and scattered the rest).</item>
/// <item><see cref="CommitAsync"/>: an existing folder of the same name in the channel folder goes to the Recycle
/// Bin, then the build is moved in (a rename, as staging is on the library's volume).</item>
/// </list>
/// A corrupt, unsafe or unsupported archive is deleted; other failures (disk full, permissions) keep it. The
/// extraction folder is always removed on failure or cancel.
/// </summary>
public sealed class Extractor
{
    private const int BufferSize = 1024 * 1024;
    private readonly IRecycleBin _bin;
    private readonly RetryPolicy _retry;
    private readonly Log? _log;
    private readonly TimeSpan _progressInterval;

    public Extractor(IRecycleBin bin, RetryPolicy? retry = null, Log? log = null, TimeSpan? progressInterval = null)
    {
        ArgumentNullException.ThrowIfNull(bin);
        _bin = bin;
        _retry = retry ?? RetryPolicy.Default;
        _log = log;
        _progressInterval = progressInterval ?? TimeSpan.FromMilliseconds(100);
    }

    /// <summary>
    /// Extracts, fixes the UPBGE layout when <paramref name="isUpbge"/>, and commits into
    /// <paramref name="destinationFolder"/> in one call.
    /// </summary>
    public async Task<ExtractResult> ExtractAsync(ExtractRequest request, string destinationFolder, bool isUpbge,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var staged = await ExtractToStagingAsync(request, progress, ct).ConfigureAwait(false);
        try
        {
            if (isUpbge)
            {
                UpbgeLayoutFixer.Fix(staged.BuildFolder, _log);
            }
        }
        catch
        {
            Discard(staged);
            throw;
        }

        return await CommitAsync(staged, destinationFolder, ct).ConfigureAwait(false);
    }

    /// <summary>Extracts <paramref name="request"/> into a new folder under its staging folder.</summary>
    public Task<StagedBuild> ExtractToStagingAsync(ExtractRequest request, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => ExtractToStaging(request, progress, ct), ct);
    }

    /// <summary>
    /// Moves <paramref name="staged"/> into <paramref name="destinationFolder"/> (created when missing), sending an
    /// existing folder of the same name to the Recycle Bin first, and removes the extraction folder. When the bin
    /// cannot take that folder it is deleted permanently: it holds the same build being installed again.
    /// </summary>
    public async Task<ExtractResult> CommitAsync(StagedBuild staged, string destinationFolder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentException.ThrowIfNullOrEmpty(destinationFolder);
        var destination = Path.GetFullPath(destinationFolder);
        try
        {
            if (!Directory.Exists(staged.BuildFolder))
            {
                throw PipelineException.StagingVanished(staged.WorkFolder);
            }

            Directory.CreateDirectory(destination);
            var target = Path.Combine(destination, staged.BuildFolderName);
            var replaced = await ReplaceExistingAsync(_bin, target, _retry, _log, ct).ConfigureAwait(false);
            await MoveDirectoryAsync(staged.BuildFolder, target, _retry, _log, ct).ConfigureAwait(false);
            foreach (var loose in staged.LooseFiles)
            {
                if (File.Exists(loose))
                {
                    var to = Path.Combine(destination, Path.GetFileName(loose));
                    await FileRetry.RunAsync(() => File.Move(loose, to, overwrite: true), _retry, _log, ct).ConfigureAwait(false);
                }
            }

            Discard(staged);
            _log?.Info($"Installed {staged.BuildFolderName} into {destination}{(replaced ? " (replaced the existing folder)" : "")}");
            return new ExtractResult(target, replaced);
        }
        catch (Exception ex) when (ex is not PipelineException and not OperationCanceledException)
        {
            Discard(staged);
            throw Translate(ex, destination);
        }
    }

    /// <summary>Deletes a staged build's extraction folder (after a failure or a cancel).</summary>
    public void Discard(StagedBuild staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        TryDeleteFolder(staged.WorkFolder);
    }

    /// <summary>
    /// When <paramref name="target"/> exists, sends it to the bin (permanently deleting it when the bin cannot take
    /// it) and returns true. Shared with <see cref="Renamer"/>.
    /// </summary>
    internal static async Task<bool> ReplaceExistingAsync(IRecycleBin bin, string target, RetryPolicy retry, Log? log, CancellationToken ct)
    {
        if (!Path.Exists(target))
        {
            return false;
        }

        var result = await FileRetry.RunAsync(() => bin.SendToBin(target, allowPermanentDelete: true), retry, log, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case RecycleOutcome.NotRecycled:
                throw new PipelineException(PipelineErrorKind.RecycleRefused,
                    $"Could not move the existing {target} to the Recycle Bin: {result.Reason}") { Folder = target };
            case RecycleOutcome.DeletedPermanently:
                log?.Warning($"Deleted the existing {target} permanently: {result.Reason}");
                break;
            default:
                log?.Info($"Sent the existing {target} to the Recycle Bin");
                break;
        }

        return result.Outcome != RecycleOutcome.Missing;
    }

    /// <summary>Moves a folder, retrying on locks, copying when source and target are on different volumes.</summary>
    internal static async Task MoveDirectoryAsync(string source, string target, RetryPolicy retry, Log? log, CancellationToken ct)
    {
        try
        {
            await FileRetry.RunAsync(() => Directory.Move(source, target), retry, log, ct).ConfigureAwait(false);
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070011)) // ERROR_NOT_SAME_DEVICE
        {
            log?.Warning($"{source} and {target} are on different volumes; copying");
            CopyDirectory(source, target);
            PermanentDelete.Run(source);
        }
    }

    internal static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var folder in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(folder, Path.Combine(target, Path.GetFileName(folder)));
        }
    }

    private StagedBuild ExtractToStaging(ExtractRequest request, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var archive = Path.GetFullPath(request.ArchivePath);
        var staging = Path.GetFullPath(request.StagingFolder);
        var fileName = Path.GetFileName(archive);
        var stem = ArchiveNames.Stem(fileName);
        var throttle = new ProgressThrottle(progress, _progressInterval);
        throttle.Report(0, null, force: true);

        if (ArchiveNames.KindOf(fileName) is not { } kind)
        {
            DeleteArchive(archive);
            throw new PipelineException(PipelineErrorKind.UnsupportedArchive, $"Unsupported archive format: {fileName}");
        }

        if (!File.Exists(archive))
        {
            throw Directory.Exists(staging)
                ? new PipelineException(PipelineErrorKind.StagingVanished, $"The downloaded archive is missing: {archive}") { Folder = staging }
                : PipelineException.StagingVanished(staging);
        }

        var work = Path.Combine(staging, $"{stem}.{Guid.NewGuid().ToString("N")[..8]}.extract");
        try
        {
            Directory.CreateDirectory(work);
            var staged = kind == ArchiveKind.Zip
                ? ExtractZip(archive, stem, work, throttle, ct)
                : ExtractSevenZip(archive, stem, work, throttle, ct);
            _log?.Info($"Extracted {fileName} into {staged.BuildFolder}");
            return staged;
        }
        catch (OperationCanceledException)
        {
            TryDeleteFolder(work);
            throw;
        }
        catch (PipelineException ex)
        {
            TryDeleteFolder(work);
            if (ex.Kind is PipelineErrorKind.UnsafeArchive or PipelineErrorKind.CorruptArchive or PipelineErrorKind.UnsupportedArchive)
            {
                _log?.Error($"Extraction failed: {ex.Message}; removing {archive}");
                DeleteArchive(archive);
            }

            throw;
        }
        catch (Exception ex) when (IsCorrupt(ex))
        {
            TryDeleteFolder(work);
            _log?.Error($"Extraction failed: {ex.Message}; removing the corrupted {archive}", ex);
            DeleteArchive(archive);
            var kindOfError = ex is NotSupportedException ? PipelineErrorKind.UnsupportedArchive : PipelineErrorKind.CorruptArchive;
            throw new PipelineException(kindOfError, $"Extraction failed: {fileName} could not be read ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            TryDeleteFolder(work);
            if (!Directory.Exists(staging) || ex is FileNotFoundException && !File.Exists(archive))
            {
                throw PipelineException.StagingVanished(staging, ex);
            }

            throw Translate(ex, staging);
        }
    }

    private StagedBuild ExtractZip(string archivePath, string stem, string work, ProgressThrottle throttle, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        var (root, buildFolder) = Plan(names, stem, work);
        var targets = zip.Entries.Select(e => ArchivePaths.Resolve(root, e.FullName)).ToList();
        var total = zip.Entries.Sum(e => e.Length);
        throttle.Report(0, total, force: true);

        var buffer = new byte[BufferSize];
        long done = 0;
        for (var i = 0; i < zip.Entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = zip.Entries[i];
            var target = targets[i];
            if (IsDirectoryName(entry.FullName))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = entry.Open())
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1))
            {
                done = Copy(input, output, buffer, done, total, throttle, ct);
            }

            TrySetTime(target, entry.LastWriteTime.UtcDateTime);
        }

        throttle.Report(total, total, force: true);
        return Finish(work, buildFolder);
    }

    private StagedBuild ExtractSevenZip(string archivePath, string stem, string work, ProgressThrottle throttle, CancellationToken ct)
    {
        using var archive = SevenZipArchive.OpenArchive(archivePath, new ReaderOptions());
        var entries = archive.Entries.ToList();
        var names = entries.Select(e => e.Key ?? "").ToList();
        var (root, buildFolder) = Plan(names, stem, work);
        foreach (var name in names)
        {
            ArchivePaths.Resolve(root, name);
        }

        var total = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
        throttle.Report(0, total, force: true);

        // The reader decodes a solid block once, in order; opening entries one by one would decode it again per entry.
        var buffer = new byte[BufferSize];
        long done = 0;
        using var reader = archive.ExtractAllEntries();
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            var target = ArchivePaths.Resolve(root, entry.Key ?? "");
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = reader.OpenEntryStream())
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1))
            {
                done = Copy(input, output, buffer, done, total, throttle, ct);
            }

            if (entry.LastModifiedTime is { } modified)
            {
                TrySetTime(target, modified.Kind == DateTimeKind.Local ? modified.ToUniversalTime() : modified);
            }
        }

        throttle.Report(total, total, force: true);
        return Finish(work, buildFolder);
    }

    /// <summary>Where entries go and which folder is the build: upstream's rules, plus wrapping when there is no single top folder.</summary>
    private (string Root, string BuildFolderName) Plan(IReadOnlyList<string> names, string stem, string work)
    {
        if (names.Count == 0)
        {
            throw new PipelineException(PipelineErrorKind.CorruptArchive, "The archive is empty");
        }

        var top = ArchivePaths.FindTopFolder(names);
        if (ArchivePaths.IsUpbgeFlat(top, names))
        {
            _log?.Info($"UPBGE archive with a bin/Release layout; extracting into {stem}");
            return (Path.Combine(work, stem), stem);
        }

        if (top is null)
        {
            _log?.Info($"No single top folder in the archive; extracting into {stem}");
            return (Path.Combine(work, stem), stem);
        }

        return (work, top);
    }

    private static StagedBuild Finish(string work, string buildFolderName)
    {
        var buildFolder = Path.Combine(work, buildFolderName);
        Directory.CreateDirectory(buildFolder);
        var loose = Directory.EnumerateFiles(work).ToList();
        return new StagedBuild(work, buildFolder, loose);
    }

    private static long Copy(Stream input, Stream output, byte[] buffer, long done, long total, ProgressThrottle throttle, CancellationToken ct)
    {
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            done += read;
            throttle.Report(Math.Min(done, total), total);
        }

        return done;
    }

    private static bool IsDirectoryName(string name) => name.EndsWith('/') || name.EndsWith('\\');

    private static bool IsCorrupt(Exception ex) =>
        ex is InvalidDataException or SharpCompressException or EndOfStreamException or NotSupportedException
            or IndexOutOfRangeException or ArgumentOutOfRangeException;

    private static PipelineException Translate(Exception ex, string folder) => ex switch
    {
        PipelineException p => p,
        UnauthorizedAccessException => PipelineException.Permission(folder, ex),
        IOException io when io.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) =>
            new PipelineException(PipelineErrorKind.DiskSpace, $"The disk holding {folder} is full", ex) { Folder = folder },
        IOException io when FileRetry.IsLock(io) =>
            new PipelineException(PipelineErrorKind.FileInUse, $"A file in {folder} is in use: {ex.Message}", ex) { Folder = folder },
        _ => new PipelineException(PipelineErrorKind.Io, $"Extraction failed: {ex.Message}", ex) { Folder = folder },
    };

    private static void TrySetTime(string path, DateTime utc)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, utc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // Timestamps are cosmetic.
        }
    }

    private void DeleteArchive(string archive)
    {
        try
        {
            File.Delete(archive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Warning($"Could not delete {archive}", ex);
        }
    }

    private void TryDeleteFolder(string folder)
    {
        try
        {
            PermanentDelete.Run(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Warning($"Could not delete {folder}", ex);
        }
    }
}
