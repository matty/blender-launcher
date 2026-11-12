using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Scraping;

namespace BlenderLauncher.Core.Pipeline;

/// <summary>Where an <see cref="InstallJob"/> is (upstream's <c>DownloadState</c>).</summary>
public enum InstallState
{
    /// <summary>Queued, waiting for a worker.</summary>
    Idle,
    Downloading,

    /// <summary>Extracting into staging and fixing the layout. The last state that can be cancelled.</summary>
    Extracting,

    /// <summary>Moving into the library, template, reading the build's information, renaming. Cannot be cancelled.</summary>
    Finishing,
    Done,
    Failed,
    Cancelled,
}

/// <summary>What the build-info callback gets (upstream's <c>ReadBuildTask</c> arguments, <c>download_widget.py:318-360</c>).</summary>
/// <param name="Folder">The build's folder in the library, before the rename.</param>
/// <param name="Seed">Upstream's <c>info</c>: the scraped build with <c>link</c> set to <paramref name="Folder"/>, the version from the folder name and no hash (<see cref="InstallService.SeedInfo"/>).</param>
/// <param name="Scraped">The build as the scraper reported it.</param>
/// <param name="ArchiveName">The link's file name without its extension (upstream's <c>archive_name</c>), which names the branch of experimental builds.</param>
public sealed record ReadBuildInfoRequest(string Folder, BuildInfo Seed, BuildInfo Scraped, string ArchiveName);

/// <summary>
/// One install of one build. Created by <see cref="InstallService.Install"/>; a second request for the
/// same build gets the same job. Events are raised on worker threads.
/// </summary>
public sealed class InstallJob
{
    private readonly Lock _gate = new();
    private InstallState _state = InstallState.Idle;
    private TransferProgress _progress = TransferProgress.Indeterminate;
    private bool _cancelRequested;
    private readonly TaskCompletionSource<InstallState> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal InstallJob(AvailableBuild build, string key)
    {
        Build = build;
        Key = key;
    }

    public AvailableBuild Build { get; }

    /// <summary>The build's link, which identifies the job.</summary>
    public string Key { get; }

    public InstallState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Progress of the current state (bytes downloaded, bytes extracted); indeterminate otherwise.</summary>
    public TransferProgress Progress
    {
        get
        {
            lock (_gate)
            {
                return _progress;
            }
        }
    }

    /// <summary>For <see cref="InstallState.Failed"/>: usually a <see cref="PipelineException"/>.</summary>
    public Exception? Error { get; private set; }

    /// <summary>For <see cref="InstallState.Done"/>: the installed build's folder.</summary>
    public string? InstalledFolder { get; private set; }

    /// <summary>For <see cref="InstallState.Done"/>: the build's information from the callback, with <c>Link</c> set to <see cref="InstalledFolder"/>.</summary>
    public BuildInfo? InstalledBuild { get; private set; }

    /// <summary>True when an existing folder of the same build was replaced (sent to the Recycle Bin).</summary>
    public bool ReplacedExisting { get; private set; }

    /// <summary>Completes with the final state; never faults.</summary>
    public Task<InstallState> Completion => _completion.Task;

    public bool IsFinished => State is InstallState.Done or InstallState.Failed or InstallState.Cancelled;

    /// <summary>True while the job can still be cancelled (<see cref="InstallState.Idle"/> to <see cref="InstallState.Extracting"/>).</summary>
    public bool CanCancel => State is InstallState.Idle or InstallState.Downloading or InstallState.Extracting;

    public event Action<InstallJob>? StateChanged;

    public event Action<InstallJob, TransferProgress>? ProgressChanged;

    internal QueuedTask? QueuedTask { get; set; }

    internal bool CancelRequested
    {
        get
        {
            lock (_gate)
            {
                return _cancelRequested;
            }
        }
    }

    /// <summary>
    /// Cancels the job if it has not reached <see cref="InstallState.Finishing"/>; the staging files are cleaned up.
    /// Returns false when it is too late.
    /// </summary>
    public bool Cancel()
    {
        lock (_gate)
        {
            if (_state is not (InstallState.Idle or InstallState.Downloading or InstallState.Extracting))
            {
                return false;
            }

            _cancelRequested = true;
        }

        QueuedTask?.Cancel();
        return true;
    }

    public override string ToString() => $"Install {Key} ({State})";

    internal void SetProgress(TransferProgress progress)
    {
        lock (_gate)
        {
            _progress = progress;
        }

        ProgressChanged?.Invoke(this, progress);
    }

    /// <summary>Moves to <paramref name="state"/>; entering <see cref="InstallState.Finishing"/> fails with a cancellation if one was requested.</summary>
    internal void Enter(InstallState state)
    {
        lock (_gate)
        {
            if (state == InstallState.Finishing && _cancelRequested)
            {
                throw new OperationCanceledException();
            }

            _state = state;
            _progress = TransferProgress.Indeterminate;
        }

        StateChanged?.Invoke(this);
    }

    internal bool TryFinish(InstallState state, Exception? error = null, string? folder = null, BuildInfo? info = null, bool replaced = false)
    {
        lock (_gate)
        {
            if (_state is InstallState.Done or InstallState.Failed or InstallState.Cancelled)
            {
                return false;
            }

            Error = error;
            InstalledFolder = folder;
            InstalledBuild = info;
            ReplacedExisting = replaced;
            _state = state;
        }

        StateChanged?.Invoke(this);
        _completion.TrySetResult(state);
        return true;
    }
}
