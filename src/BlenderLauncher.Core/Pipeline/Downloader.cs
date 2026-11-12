using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Net;

namespace BlenderLauncher.Core.Pipeline;

/// <summary>What to download and where.</summary>
/// <param name="Url">The archive's URL (the build's <c>link</c>).</param>
/// <param name="StagingFolder">The port's staging folder (<see cref="Library.LibraryLayout.StagingFolder"/>); created when missing.</param>
public sealed record DownloadRequest(Uri Url, string StagingFolder)
{
    /// <summary>The file name to save as; null takes it from the URL (<see cref="ArchiveNames.FromUrl"/>).</summary>
    public string? FileName { get; init; }

    /// <summary>The size the source listed, used for the free-space check before any request is made.</summary>
    public long? ExpectedSize { get; init; }
}

/// <summary>Tunables for <see cref="Downloader"/>.</summary>
public sealed record DownloaderOptions
{
    /// <summary>
    /// Bound on getting the response headers and the first body bytes (upstream's <c>timeout=10</c>). Connecting
    /// is bounded separately by the client (<see cref="HttpClientProvider.ConnectTimeout"/>).
    /// </summary>
    public TimeSpan FirstByteTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Bound on the one retry's wait for the first bytes (upstream retried without any timeout).</summary>
    public TimeSpan RetryFirstByteTimeout { get; init; } = NetTimeouts.DownloadStall;

    /// <summary>The download fails when no bytes arrive for this long once the body is flowing.</summary>
    public TimeSpan StallTimeout { get; init; } = NetTimeouts.DownloadStall;

    /// <summary>Minimum time between progress reports.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Read size (upstream: 1 MiB on Windows).</summary>
    public int BufferSize { get; init; } = 1024 * 1024;

    /// <summary>Free space wanted on the staging volume, as a multiple of the archive size: archive, extracted build, headroom.</summary>
    public double FreeSpaceFactor { get; init; } = 3.0;

    /// <summary>Free bytes on the volume holding a folder, or null when unknown (the check is then skipped).</summary>
    public Func<string, long?> FreeSpace { get; init; } = DefaultFreeSpace;

    /// <summary>Free space through <see cref="DriveInfo"/>; null for paths it cannot handle (UNC shares).</summary>
    public static long? DefaultFreeSpace(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Downloads an archive into the staging folder (port of <c>threads/downloader.py</c>).
/// <list type="bullet">
/// <item>Writes <c>&lt;staging&gt;/&lt;name&gt;.part</c> and renames it to <c>&lt;name&gt;</c> once complete;
/// the part file is deleted on any failure or cancel.</item>
/// <item>Status 400 and above fails; a non-empty <c>Content-Type</c> must contain one of
/// <see cref="AllowedContentTypes"/>.</item>
/// <item>No <c>Content-Length</c> is fine: progress is indeterminate (upstream required the header).
/// When the length is given, a shorter body is an error.</item>
/// <item>A timeout or connection failure before the body starts is retried once.</item>
/// <item>Free space: about <see cref="DownloaderOptions.FreeSpaceFactor"/> times the archive size must be free on
/// the staging volume, checked before the request (listed size) and again once the length is known.</item>
/// <item>No resume, no checksum.</item>
/// </list>
/// Errors are <see cref="PipelineException"/>s; cancelling throws <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class Downloader
{
    /// <summary>Upstream's <c>VALID_ARCHIVE_TYPES</c>, matched as substrings of the lower-cased header.</summary>
    public static IReadOnlyList<string> AllowedContentTypes { get; } =
    [
        "application/zip",
        "application/x-apple-diskimage",
        "application/x-zip-compressed",
        "application/octet-stream",
        "application/x-tar",
        "application/x-gzip",
        "application/x-xz",
        "application/gzip",
        "application/x-dmg",
    ];

    private readonly Func<HttpClient> _client;
    private readonly DownloaderOptions _options;
    private readonly Log? _log;

    /// <param name="client">Fetches the current client per download (see <see cref="HttpClientProvider.Client"/>).</param>
    public Downloader(Func<HttpClient> client, DownloaderOptions? options = null, Log? log = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _options = options ?? new DownloaderOptions();
        _log = log;
    }

    /// <summary>True when <paramref name="contentType"/> is empty or contains an allowed archive type.</summary>
    public static bool IsAllowedContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return true;
        }

        var lower = contentType.ToLowerInvariant();
        return AllowedContentTypes.Any(t => lower.Contains(t, StringComparison.Ordinal));
    }

    /// <summary>Downloads <paramref name="request"/> and returns the full path of the finished archive.</summary>
    public async Task<string> DownloadAsync(DownloadRequest request, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var staging = Path.GetFullPath(request.StagingFolder);
        var fileName = request.FileName ?? ArchiveNames.FromUrl(request.Url);
        var final = Path.Combine(staging, fileName);
        var part = final + ".part";
        var throttle = new ProgressThrottle(progress, _options.ProgressInterval);
        throttle.Report(0, null, force: true);

        try
        {
            Directory.CreateDirectory(staging);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw PipelineException.Permission(staging, ex);
        }
        catch (IOException ex)
        {
            throw new PipelineException(PipelineErrorKind.Io, $"Could not create {staging}: {ex.Message}", ex) { Folder = staging };
        }

        if (request.ExpectedSize is { } listed)
        {
            CheckFreeSpace(staging, listed);
        }

        try
        {
            await DownloadWithRetryAsync(request.Url, staging, part, throttle, ct).ConfigureAwait(false);
            try
            {
                File.Move(part, final, overwrite: true);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw PipelineException.StagingVanished(staging, ex);
            }
        }
        catch
        {
            TryDelete(part);
            throw;
        }

        _log?.Info($"Downloaded {request.Url} to {final}");
        return final;
    }

    private async Task DownloadWithRetryAsync(Uri url, string staging, string part, ProgressThrottle throttle, CancellationToken ct)
    {
        try
        {
            await DownloadOnceAsync(url, staging, part, _options.FirstByteTimeout, throttle, ct).ConfigureAwait(false);
        }
        catch (RetryableException first)
        {
            _log?.Warning($"Requesting {url} is taking longer than usual; retrying once", first.InnerException);
            try
            {
                await DownloadOnceAsync(url, staging, part, _options.RetryFirstByteTimeout, throttle, ct).ConfigureAwait(false);
            }
            catch (RetryableException second)
            {
                throw new PipelineException(PipelineErrorKind.Network, $"Download failed: {second.Message}", second.InnerException);
            }
        }
    }

    private async Task DownloadOnceAsync(Uri url, string staging, string part, TimeSpan firstByteTimeout, ProgressThrottle throttle, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(firstByteTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _client().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new RetryableException($"no response from {url.Host} within {firstByteTimeout.TotalSeconds:0} s", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new RetryableException($"could not connect to {url.Host}: {ex.Message}", ex);
        }

        using (response)
        {
            var contentType = response.Content.Headers.ContentType?.ToString();
            _log?.Info($"Response status: {(int)response.StatusCode}, Content-Type: {contentType}");
            if ((int)response.StatusCode >= 400)
            {
                throw new PipelineException(PipelineErrorKind.HttpStatus, $"Download failed: HTTP {(int)response.StatusCode} for {url}")
                {
                    StatusCode = (int)response.StatusCode,
                };
            }

            if (!IsAllowedContentType(contentType))
            {
                throw new PipelineException(PipelineErrorKind.BadContentType, $"Download failed: unexpected content type '{contentType}' for {url}");
            }

            var total = response.Content.Headers.ContentLength;
            if (total is { } length)
            {
                CheckFreeSpace(staging, length);
            }

            throttle.Report(0, total, force: true);
            Stream body;
            try
            {
                body = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new RetryableException($"no data from {url.Host} within {firstByteTimeout.TotalSeconds:0} s", ex);
            }

            await using (body.ConfigureAwait(false))
            {
                await CopyBodyAsync(body, url, staging, part, total, attempt, firstByteTimeout, throttle, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task CopyBodyAsync(Stream body, Uri url, string staging, string part, long? total, CancellationTokenSource attempt,
        TimeSpan firstByteTimeout, ProgressThrottle throttle, CancellationToken ct)
    {
        FileStream file;
        try
        {
            file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw PipelineException.Permission(staging, ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw PipelineException.StagingVanished(staging, ex);
        }

        await using (file.ConfigureAwait(false))
        {
            var buffer = new byte[_options.BufferSize];
            long done = 0;
            var started = false;
            while (true)
            {
                int read;
                try
                {
                    read = await body.ReadAsync(buffer, attempt.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    if (!started)
                    {
                        throw new RetryableException($"no data from {url.Host} within {firstByteTimeout.TotalSeconds:0} s", ex);
                    }

                    throw new PipelineException(PipelineErrorKind.Network,
                        $"Download stalled: no data for {_options.StallTimeout.TotalSeconds:0} s from {url.Host}", ex);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    throw new PipelineException(PipelineErrorKind.Network, $"Download failed: {ex.Message}", ex);
                }

                if (read == 0)
                {
                    break;
                }

                // Data flows: from here on the timer bounds stalls rather than the first byte.
                started = true;
                attempt.CancelAfter(_options.StallTimeout);
                try
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw PipelineException.Permission(staging, ex);
                }
                catch (IOException ex) when (!Directory.Exists(staging))
                {
                    throw PipelineException.StagingVanished(staging, ex);
                }
                catch (IOException ex) when (IsDiskFull(ex))
                {
                    throw new PipelineException(PipelineErrorKind.DiskSpace, $"The disk holding {staging} is full", ex) { Folder = staging };
                }

                done += read;
                throttle.Report(done, total);
            }

            await file.FlushAsync(ct).ConfigureAwait(false);
            if (total is { } expected && done != expected)
            {
                throw new PipelineException(PipelineErrorKind.Network, $"Download incomplete: {done:N0} of {expected:N0} bytes from {url}");
            }

            throttle.Report(done, total ?? done, force: true);
        }

        if (!File.Exists(part))
        {
            throw PipelineException.StagingVanished(staging);
        }
    }

    private void CheckFreeSpace(string staging, long archiveSize)
    {
        if (archiveSize <= 0 || _options.FreeSpace(staging) is not { } free)
        {
            return;
        }

        var required = (long)Math.Ceiling(archiveSize * _options.FreeSpaceFactor);
        if (free < required)
        {
            throw new PipelineException(PipelineErrorKind.DiskSpace,
                $"Not enough free space for {staging}: the {FormatBytes(archiveSize)} archive needs about {FormatBytes(required)} free " +
                $"(archive, extracted build and headroom), {FormatBytes(free)} available")
            {
                Folder = staging,
                RequiredBytes = required,
                AvailableBytes = free,
            };
        }
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} bytes",
    };

    private static bool IsDiskFull(IOException ex) =>
        ex.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027); // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Warning($"Could not delete {path}", ex);
        }
    }

    /// <summary>A failure before the body started: worth one retry.</summary>
    private sealed class RetryableException(string message, Exception inner) : Exception(message, inner);
}
