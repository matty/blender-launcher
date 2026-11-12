using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.PlatformServices;

namespace BlenderLauncher.Core.Pipeline;

/// <summary>Port of <c>threads/remover.py</c>.</summary>
public sealed class Remover
{
    private readonly IRecycleBin _bin;
    private readonly RetryPolicy _retry;
    private readonly Log? _log;

    public Remover(IRecycleBin bin, RetryPolicy? retry = null, Log? log = null)
    {
        ArgumentNullException.ThrowIfNull(bin);
        _bin = bin;
        _retry = retry ?? RetryPolicy.Default;
        _log = log;
    }

    /// <summary>
    /// Removes the file or folder at <paramref name="path"/>: to the Recycle Bin when
    /// <paramref name="toRecycleBin"/> (upstream's default <c>trash=True</c>), otherwise permanently. Retries on
    /// locks. A missing path is success (<see cref="RecycleOutcome.Missing"/>). When the bin cannot take the item,
    /// nothing is deleted and <see cref="RecycleOutcome.NotRecycled"/> comes back with the reason, so the caller can
    /// ask before calling again with <paramref name="toRecycleBin"/> false.
    /// </summary>
    /// <exception cref="PipelineException">The item could not be removed (<see cref="PipelineErrorKind.FileInUse"/> after the retries, or <see cref="PipelineErrorKind.Io"/>).</exception>
    public async Task<RecycleResult> RemoveAsync(string path, bool toRecycleBin, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Path.Exists(full))
        {
            _log?.Info($"Path {full} does not exist, nothing to remove.");
            return new RecycleResult(RecycleOutcome.Missing);
        }

        try
        {
            if (toRecycleBin)
            {
                var result = await FileRetry.RunAsync(() => _bin.SendToBin(full, allowPermanentDelete: false), _retry, _log, ct).ConfigureAwait(false);
                _log?.Info($"Remove {full}: {result.Outcome}{(result.Reason is null ? "" : $" ({result.Reason})")}");
                return result;
            }

            await FileRetry.RunAsync(() => _bin.DeletePermanently(full), _retry, _log, ct).ConfigureAwait(false);
            _log?.Info($"Deleted {full} permanently");
            return new RecycleResult(RecycleOutcome.DeletedPermanently);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Error($"Failed to remove {full}", ex);
            var kind = FileRetry.IsLock(ex) ? PipelineErrorKind.FileInUse : PipelineErrorKind.Io;
            throw new PipelineException(kind, $"Failed to remove {full}: {ex.Message}", ex) { Folder = full };
        }
    }

    /// <summary>
    /// Empties the port's own staging folder (<c>&lt;library&gt;\.temp\BlenderLauncherNet</c>), permanently, and
    /// nothing else: upstream's files in <c>.temp</c> are left alone. Refuses any folder that
    /// is not of that shape. Items that stay locked are skipped and logged; returns false when any were.
    /// Do not call it while installs are running.
    /// </summary>
    public async Task<bool> PurgeStagingAsync(string stagingFolder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stagingFolder);
        if (!LibraryLayout.IsStagingFolder(stagingFolder))
        {
            throw new ArgumentException($"{stagingFolder} is not a {LibraryLayout.TempFolderName}\\{LibraryLayout.StagingFolderName} staging folder", nameof(stagingFolder));
        }

        var full = Path.GetFullPath(stagingFolder);
        if (!Directory.Exists(full))
        {
            return true;
        }

        var clean = true;
        foreach (var item in Directory.EnumerateFileSystemEntries(full).ToList())
        {
            try
            {
                await FileRetry.RunAsync(() => PermanentDelete.Run(item), _retry, _log, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Warning($"Could not purge {item}", ex);
                clean = false;
            }
        }

        return clean;
    }
}
