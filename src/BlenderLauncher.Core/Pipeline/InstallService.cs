using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Pipeline;

/// <summary>The library folders a job works in, read when the job starts.</summary>
/// <param name="LibraryRoot">The library folder.</param>
/// <param name="StagingFolder">The port's staging folder, normally <see cref="LibraryLayout.StagingFolder"/> of <paramref name="LibraryRoot"/>.</param>
public sealed record InstallPaths(string LibraryRoot, string StagingFolder)
{
    /// <summary><c>&lt;library&gt;/template</c>.</summary>
    public string TemplateFolder => Path.Combine(LibraryRoot, "template");

    /// <summary>Paths for <paramref name="libraryRoot"/> with the standard staging folder.</summary>
    public static InstallPaths For(string libraryRoot)
    {
        var layout = new LibraryLayout(libraryRoot);
        return new(layout.Root, layout.StagingFolder);
    }
}

/// <summary>What <see cref="InstallService"/> needs from the rest of the app.</summary>
public sealed record InstallServiceOptions
{
    /// <summary>The current library's folders; called once per job as it starts.</summary>
    public required Func<InstallPaths> Paths { get; init; }

    /// <summary>
    /// Reads the installed build's information and writes its <c>.blinfo</c> (upstream's <c>ReadBuildTask</c>; the
    /// library's <c>BuildInfoReader</c>). Its result's full version names the build's folder.
    /// </summary>
    public required Func<ReadBuildInfoRequest, CancellationToken, Task<BuildInfo>> ReadBuildInfo { get; init; }

    /// <summary>The <c>install_template</c> setting, read per job.</summary>
    public Func<bool> InstallTemplate { get; init; } = static () => false;
}

/// <summary>
/// Installs builds (port of the chain in <c>download_widget.py:184-399</c>). One
/// <see cref="InstallJob"/> per build, run as one task on the <see cref="TaskQueue"/>:
/// download → extract into staging → fix the UPBGE layout → move into the channel folder → Bforartists patch
/// notes → template (when enabled) → read info (callback) → rename to <c>blender-&lt;full version&gt;</c> → delete
/// the archive (a plain delete; upstream sent it to the Recycle Bin). Cancelling cleans the staging files.
/// A request for a build that already has a job (same link, or same full version) returns that job.
/// Events are raised on worker threads.
/// </summary>
public sealed class InstallService
{
    private readonly InstallServiceOptions _options;
    private readonly TaskQueue _queue;
    private readonly Downloader _downloader;
    private readonly Extractor _extractor;
    private readonly Renamer _renamer;
    private readonly Log? _log;
    private readonly Lock _gate = new();
    private readonly List<InstallJob> _jobs = [];

    public InstallService(InstallServiceOptions options, TaskQueue queue, Downloader downloader, Extractor extractor, Renamer renamer, Log? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(renamer);
        _options = options;
        _queue = queue;
        _downloader = downloader;
        _extractor = extractor;
        _renamer = renamer;
        _log = log;
    }

    /// <summary>Raised when a job is added or a job finishes and leaves <see cref="Jobs"/>.</summary>
    public event Action<InstallJob>? JobsChanged;

    /// <summary>Raised when any job changes state.</summary>
    public event Action<InstallJob>? JobStateChanged;

    /// <summary>A snapshot of the jobs not yet finished, oldest first.</summary>
    public IReadOnlyList<InstallJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobs];
            }
        }
    }

    /// <summary>The unfinished job for <paramref name="build"/>, or null.</summary>
    public InstallJob? Find(BuildInfo build)
    {
        ArgumentNullException.ThrowIfNull(build);
        lock (_gate)
        {
            return FindLocked(build);
        }
    }

    /// <summary>
    /// Starts installing <paramref name="build"/>, or returns the unfinished job already installing it
    /// (double-click, replace-on-new-build and the update button all land here).
    /// </summary>
    public InstallJob Install(AvailableBuild build)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (!Uri.TryCreate(build.Build.Link, UriKind.Absolute, out var url))
        {
            throw new ArgumentException($"Not a download link: {build.Build.Link}", nameof(build));
        }

        var archiveName = ArchiveNames.FromUrl(url);

        InstallJob job;
        lock (_gate)
        {
            if (FindLocked(build.Build) is { } existing)
            {
                _log?.Info($"Install of {build.Build.Link} already in progress; attaching");
                return existing;
            }

            job = new InstallJob(build, build.Build.Link);
            _jobs.Add(job);
        }

        job.StateChanged += OnJobStateChanged;
        var queued = _queue.Enqueue($"Install {archiveName}", ctx => RunAsync(job, url, ctx));
        job.QueuedTask = queued;
        queued.StateChanged += t =>
        {
            // Cancelled before a worker picked it up: the body never runs.
            if (t.State == QueuedTaskState.Cancelled)
            {
                job.TryFinish(InstallState.Cancelled);
            }
        };
        if (job.CancelRequested)
        {
            queued.Cancel();
        }

        JobsChanged?.Invoke(job);
        return job;
    }

    /// <summary>
    /// Upstream's <c>info</c> for <c>ReadBuildTask</c> (<c>download_get_info</c>): the scraped build with
    /// <c>link</c> set to <paramref name="folder"/>, no hash, and the version taken from the folder name (major,
    /// minor, patch and prerelease); Bforartists dev builds get prerelease <c>dev</c>, UPBGE weekly builds keep
    /// the scraped version. Where the folder name holds no version, the scraped one is kept (upstream raised).
    /// </summary>
    public static BuildInfo SeedInfo(string folder, BuildInfo scraped)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(scraped);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        string subversion;
        if (scraped.IsBfaDev)
        {
            subversion = BlenderVersionParser.TryParse(name, out var v, search: true)
                ? v.WithBuild(null).WithPrerelease("dev").ToString()
                : scraped.SemVersion?.ToString() ?? scraped.Subversion;
        }
        else if (scraped.Branch == "upbge-weekly")
        {
            subversion = BlenderVersionParser.TryParse(scraped.Subversion, out var v) ? v.ToString() : scraped.Subversion;
        }
        else
        {
            subversion = BlenderVersionParser.TryParse(name, out var v, search: true)
                ? new SemVer(v.Major, v.Minor, v.Patch, v.Prerelease).ToString()
                : scraped.SemVersion?.ToString() ?? scraped.Subversion;
        }

        return scraped with { Link = folder, Subversion = subversion, BuildHash = null };
    }

    private InstallJob? FindLocked(BuildInfo build)
    {
        var full = build.FullSemVersion?.ToString();
        return _jobs.FirstOrDefault(j =>
            string.Equals(j.Key, build.Link, StringComparison.OrdinalIgnoreCase)
            || (full is not null && j.Build.Build.FullSemVersion?.ToString() is { } other
                && string.Equals(other, full, StringComparison.OrdinalIgnoreCase)
                && string.Equals(j.Build.Build.Branch, build.Branch, StringComparison.Ordinal)));
    }

    private void OnJobStateChanged(InstallJob job)
    {
        JobStateChanged?.Invoke(job);
        if (job.IsFinished)
        {
            bool removed;
            lock (_gate)
            {
                removed = _jobs.Remove(job);
            }

            if (removed)
            {
                JobsChanged?.Invoke(job);
            }
        }
    }

    private async Task RunAsync(InstallJob job, Uri url, TaskContext ctx)
    {
        var ct = ctx.CancellationToken;
        var scraped = job.Build.Build;
        var progress = new SyncProgress<TransferProgress>(job.SetProgress);
        string? archive = null;
        StagedBuild? staged = null;
        try
        {
            var paths = _options.Paths();
            job.Enter(InstallState.Downloading);
            archive = await _downloader.DownloadAsync(
                new DownloadRequest(url, paths.StagingFolder) { ExpectedSize = job.Build.SizeBytes }, progress, ct).ConfigureAwait(false);

            job.Enter(InstallState.Extracting);
            staged = await _extractor.ExtractToStagingAsync(new ExtractRequest(archive, paths.StagingFolder), progress, ct).ConfigureAwait(false);
            if (Channels.IsUpbge(Channels.FromBranch(scraped.Branch)))
            {
                UpbgeLayoutFixer.Fix(staged.BuildFolder, _log);
            }

            // Past this point the library changes, so cancelling is no longer offered.
            ct.ThrowIfCancellationRequested();
            job.Enter(InstallState.Finishing);
            var channelFolder = Path.Combine(paths.LibraryRoot, Channels.DownloadFolderForBranch(scraped.Branch));
            var committed = await _extractor.CommitAsync(staged, channelFolder, CancellationToken.None).ConfigureAwait(false);
            staged = null;

            if (scraped.Branch == "bforartists")
            {
                BforartistsPatchNotes.Move(channelFolder, _log);
            }

            if (_options.InstallTemplate())
            {
                TemplateInstaller.Install(paths.TemplateFolder, committed.BuildFolder);
            }

            var request = new ReadBuildInfoRequest(committed.BuildFolder, SeedInfo(committed.BuildFolder, scraped), scraped,
                ArchiveNames.Stem(ArchiveNames.FromUrl(url)));
            BuildInfo info;
            try
            {
                info = await _options.ReadBuildInfo(request, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not PipelineException)
            {
                throw new PipelineException(PipelineErrorKind.BuildInfo, $"Reading the build's information failed for {committed.BuildFolder}: {ex.Message}", ex)
                {
                    Folder = committed.BuildFolder,
                };
            }

            var renamed = await _renamer.RenameAsync(committed.BuildFolder, Renamer.FolderNameFor(info), CancellationToken.None).ConfigureAwait(false);
            DeleteFile(archive);
            _log?.Info($"Installed {url} as {renamed.BuildFolder}");
            job.TryFinish(InstallState.Done, folder: renamed.BuildFolder, info: info with { Link = renamed.BuildFolder },
                replaced: committed.ReplacedExisting || renamed.ReplacedExisting);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || job.CancelRequested)
        {
            _log?.Info($"Install of {url} cancelled");
            CleanUp(archive, staged);
            job.TryFinish(InstallState.Cancelled);
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex)
        {
            _log?.Error($"Install of {url} failed", ex);
            CleanUp(archive, staged);
            job.TryFinish(InstallState.Failed, ex);
            throw;
        }
    }

    private void CleanUp(string? archive, StagedBuild? staged)
    {
        if (staged is not null)
        {
            _extractor.Discard(staged);
        }

        if (archive is not null)
        {
            DeleteFile(archive);
        }
    }

    private void DeleteFile(string path)
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
}
