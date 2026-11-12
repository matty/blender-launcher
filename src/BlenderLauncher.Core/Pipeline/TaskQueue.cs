using System.Threading.Channels;
using BlenderLauncher.Core.Logging;

namespace BlenderLauncher.Core.Pipeline;

/// <summary>Where a <see cref="QueuedTask"/> is in its life.</summary>
public enum QueuedTaskState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>What a task's body gets: its cancellation token and a way to report progress.</summary>
public sealed class TaskContext
{
    private readonly QueuedTask _task;

    internal TaskContext(QueuedTask task, CancellationToken cancellationToken)
    {
        _task = task;
        CancellationToken = cancellationToken;
    }

    /// <summary>Cancelled by <see cref="QueuedTask.Cancel"/> or when the queue is disposed. Cancellation is cooperative.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Reports progress; a null <paramref name="total"/> is indeterminate.</summary>
    public void Report(long done, long? total) => _task.SetProgress(new TransferProgress(done, total));

    /// <summary>An <see cref="IProgress{T}"/> that reports into this task on the calling thread.</summary>
    public IProgress<TransferProgress> Progress => new SyncProgress<TransferProgress>(_task.SetProgress);
}

/// <summary>
/// One unit of work in a <see cref="TaskQueue"/> (upstream's <c>Task</c>). Events are raised on the worker
/// thread (or the caller's thread for a cancel before start).
/// </summary>
public sealed class QueuedTask
{
    private readonly Func<TaskContext, Task> _body;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<QueuedTaskState> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private QueuedTaskState _state = QueuedTaskState.Queued;
    private TransferProgress _progress = TransferProgress.Indeterminate;

    internal QueuedTask(string name, Func<TaskContext, Task> body)
    {
        Name = name;
        _body = body;
    }

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>For logs and the quit prompt, for example "Download blender-4.3.zip".</summary>
    public string Name { get; }

    public QueuedTaskState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

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

    /// <summary>The exception the body threw, for <see cref="QueuedTaskState.Failed"/>.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Completes with the final state; never faults.</summary>
    public Task<QueuedTaskState> Completion => _completion.Task;

    public bool IsFinished => State is QueuedTaskState.Completed or QueuedTaskState.Failed or QueuedTaskState.Cancelled;

    public event Action<QueuedTask>? StateChanged;

    public event Action<QueuedTask, TransferProgress>? ProgressChanged;

    /// <summary>
    /// Requests cancellation. A queued task is cancelled at once and never runs; a running one sees its token
    /// cancelled and ends when its body returns. Returns false when the task had already finished.
    /// </summary>
    public bool Cancel()
    {
        bool cancelledBeforeStart;
        lock (_gate)
        {
            if (_state is QueuedTaskState.Completed or QueuedTaskState.Failed or QueuedTaskState.Cancelled)
            {
                return false;
            }

            cancelledBeforeStart = _state == QueuedTaskState.Queued;
            if (cancelledBeforeStart)
            {
                _state = QueuedTaskState.Cancelled;
            }
        }

        _cts.Cancel();
        if (cancelledBeforeStart)
        {
            StateChanged?.Invoke(this);
            _completion.TrySetResult(QueuedTaskState.Cancelled);
        }

        return true;
    }

    public override string ToString() => Name;

    internal void SetProgress(TransferProgress progress)
    {
        lock (_gate)
        {
            _progress = progress;
        }

        ProgressChanged?.Invoke(this, progress);
    }

    internal async Task RunAsync(CancellationToken queueToken, Log? log)
    {
        lock (_gate)
        {
            if (_state != QueuedTaskState.Queued)
            {
                return; // cancelled while queued
            }

            _state = QueuedTaskState.Running;
        }

        StateChanged?.Invoke(this);
        QueuedTaskState final;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, queueToken);
        try
        {
            await _body(new TaskContext(this, linked.Token)).ConfigureAwait(false);
            final = QueuedTaskState.Completed;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            final = QueuedTaskState.Cancelled;
        }
        catch (Exception ex)
        {
            log?.Error($"Task {Name} failed", ex);
            Error = ex;
            final = QueuedTaskState.Failed;
        }

        lock (_gate)
        {
            _state = final;
        }

        StateChanged?.Invoke(this);
        _completion.TrySetResult(final);
    }
}

/// <summary>
/// First-in first-out work queue with a fixed number of workers (port of upstream's <c>TaskQueue</c>):
/// a <see cref="Channel{T}"/> drained by N loops, cooperative cancellation per task instead of
/// <c>QThread.terminate()</c>, progress per task, exceptions kept on the task, and <see cref="Busy"/> for the
/// quit prompt. A failing task never takes its worker down.
/// </summary>
public sealed class TaskQueue : IAsyncDisposable
{
    private readonly Channel<QueuedTask> _channel = Channel.CreateUnbounded<QueuedTask>(new UnboundedChannelOptions { SingleWriter = false });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private readonly Log? _log;
    private readonly Lock _gate = new();
    private readonly List<QueuedTask> _live = [];
    private bool _disposed;

    /// <param name="workerCount">Number of workers; 0 or less means <see cref="DefaultWorkerCount"/> (upstream's <c>worker_thread_count</c> of 0).</param>
    /// <param name="log">Where task failures are logged.</param>
    public TaskQueue(int workerCount = 0, Log? log = null)
    {
        _log = log;
        WorkerCount = workerCount > 0 ? workerCount : DefaultWorkerCount;
        _workers = new Task[WorkerCount];
        for (var i = 0; i < WorkerCount; i++)
        {
            _workers[i] = Task.Run(WorkerLoopAsync);
        }
    }

    /// <summary>Three quarters of the logical processors, rounded, at least one (upstream <c>round(max(cpu*3/4, 1))</c>).</summary>
    public static int DefaultWorkerCount => Math.Max(1, (int)Math.Round(Environment.ProcessorCount * 3 / 4.0));

    public int WorkerCount { get; }

    /// <summary>Raised on a worker thread when any task changes state.</summary>
    public event Action<QueuedTask>? TaskStateChanged;

    /// <summary>Tasks that are running now (upstream's <c>get_busy_threads</c>), for the quit prompt.</summary>
    public IReadOnlyList<QueuedTask> Busy => Snapshot(QueuedTaskState.Running);

    /// <summary>Tasks waiting for a worker, oldest first.</summary>
    public IReadOnlyList<QueuedTask> Pending => Snapshot(QueuedTaskState.Queued);

    /// <summary>True while any task is queued or running.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _live.Count > 0;
            }
        }
    }

    /// <summary>Queues <paramref name="body"/> to run on a worker. Throws <see cref="ObjectDisposedException"/> after <see cref="DisposeAsync"/>.</summary>
    public QueuedTask Enqueue(string name, Func<TaskContext, Task> body)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(body);
        var task = new QueuedTask(name, body);
        task.StateChanged += OnStateChanged;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _live.Add(task);
        }

        if (!_channel.Writer.TryWrite(task))
        {
            task.Cancel();
            throw new ObjectDisposedException(nameof(TaskQueue));
        }

        return task;
    }

    /// <summary>Cancels every queued and running task, waits for running bodies to return, and stops the workers.</summary>
    public async ValueTask DisposeAsync()
    {
        List<QueuedTask> live;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            live = [.. _live];
        }

        _channel.Writer.TryComplete();
        foreach (var task in live)
        {
            task.Cancel();
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_workers).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private void OnStateChanged(QueuedTask task)
    {
        if (task.IsFinished)
        {
            lock (_gate)
            {
                _live.Remove(task);
            }
        }

        TaskStateChanged?.Invoke(task);
    }

    private IReadOnlyList<QueuedTask> Snapshot(QueuedTaskState state)
    {
        lock (_gate)
        {
            return _live.Where(t => t.State == state).ToArray();
        }
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var task))
                {
                    await task.RunAsync(_shutdown.Token, _log).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
