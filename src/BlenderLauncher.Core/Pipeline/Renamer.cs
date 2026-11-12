using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.PlatformServices;

namespace BlenderLauncher.Core.Pipeline;

/// <summary>Where a renamed build ended up.</summary>
/// <param name="BuildFolder">The build's new folder.</param>
/// <param name="ReplacedExisting">A folder of that name was there and went to the Recycle Bin first (upstream's <c>is_removed</c>).</param>
public sealed record RenameResult(string BuildFolder, bool ReplacedExisting);

/// <summary>Port of <c>threads/renamer.py</c>.</summary>
public sealed class Renamer
{
    private readonly IRecycleBin _bin;
    private readonly RetryPolicy _retry;
    private readonly Log? _log;

    public Renamer(IRecycleBin bin, RetryPolicy? retry = null, Log? log = null)
    {
        ArgumentNullException.ThrowIfNull(bin);
        _bin = bin;
        _retry = retry ?? RetryPolicy.Default;
        _log = log;
    }

    /// <summary>
    /// The folder name for an installed build: <c>blender-&lt;full_semversion&gt;</c> lower-cased with spaces as
    /// hyphens, for example <c>blender-4.3.0-alpha+daily.ddc9f92777cd</c>.
    /// </summary>
    public static string FolderNameFor(BuildInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var version = info.FullSemVersion ?? throw new ArgumentException($"The build's version '{info.Subversion}' does not parse", nameof(info));
        return NormaliseName($"blender-{version}");
    }

    /// <summary>Upstream's <c>dst_name.lower().replace(" ", "-")</c>.</summary>
    public static string NormaliseName(string name) => name.ToLowerInvariant().Replace(' ', '-');

    /// <summary>
    /// Renames <paramref name="source"/> to <see cref="NormaliseName"/>(<paramref name="newName"/>) beside it. An
    /// existing folder of that name goes to the Recycle Bin first (deleted permanently when the bin cannot take
    /// it). The rename retries on locks (10 × 0.5 s by default). Renaming a folder to its own name, or to a name
    /// differing only in case, works.
    /// </summary>
    public async Task<RenameResult> RenameAsync(string source, string newName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(newName);
        var src = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var dst = Path.Combine(Path.GetDirectoryName(src)!, NormaliseName(newName));
        if (string.Equals(src, dst, StringComparison.Ordinal))
        {
            return new RenameResult(dst, false);
        }

        try
        {
            var replaced = false;
            if (!string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
            {
                replaced = await Extractor.ReplaceExistingAsync(_bin, dst, _retry, _log, ct).ConfigureAwait(false);
            }

            await FileRetry.RunAsync(() => Directory.Move(src, dst), _retry, _log, ct).ConfigureAwait(false);
            _log?.Info($"Renamed {src} to {dst}");
            return new RenameResult(dst, replaced);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Error($"Failed to rename {src} to {dst}", ex);
            var kind = FileRetry.IsLock(ex) ? PipelineErrorKind.FileInUse : PipelineErrorKind.Io;
            throw new PipelineException(kind, $"Failed to rename {Path.GetFileName(src)} to {Path.GetFileName(dst)}: {ex.Message}", ex)
            {
                Folder = src,
            };
        }
    }
}
