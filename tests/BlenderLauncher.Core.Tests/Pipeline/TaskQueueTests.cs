using BlenderLauncher.Core.Pipeline;

namespace BlenderLauncher.Core.Tests.Pipeline;

public sealed class TaskQueueTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public void Default_worker_count_is_three_quarters_of_the_cores()
    {
        Assert.Equal(Math.Max(1, (int)Math.Round(Environment.ProcessorCount * 0.75)), TaskQueue.DefaultWorkerCount);
        Assert.True(TaskQueue.DefaultWorkerCount >= 1);
    }

    [Fact]
    public async Task Runs_at_most_worker_count_tasks_at_once()
    {
        await using var queue = new TaskQueue(2);
        var running = 0;
        var peak = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 5).Select(i => queue.Enqueue($"t{i}", async _ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await release.Task;
            Interlocked.Decrement(ref running);
        })).ToList();

        await WaitUntil(() => Volatile.Read(ref running) == 2);
        Assert.Equal(2, queue.Busy.Count);
        Assert.Equal(3, queue.Pending.Count);
        Assert.True(queue.IsBusy);

        release.SetResult();
        var states = await Task.WhenAll(tasks.Select(t => t.Completion)).WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.All(states, s => Assert.Equal(QueuedTaskState.Completed, s));
        Assert.Equal(2, peak);
        await WaitUntil(() => !queue.IsBusy);
    }

    [Fact]
    public async Task One_worker_runs_tasks_in_order()
    {
        await using var queue = new TaskQueue(1);
        var order = new List<int>();
        var tasks = Enumerable.Range(0, 5).Select(i => queue.Enqueue($"t{i}", _ =>
        {
            lock (order)
            {
                order.Add(i);
            }

            return Task.CompletedTask;
        })).ToList();
        await Task.WhenAll(tasks.Select(t => t.Completion)).WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal([0, 1, 2, 3, 4], order);
    }

    [Fact]
    public async Task Cancelling_a_queued_task_means_it_never_runs()
    {
        await using var queue = new TaskQueue(1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = queue.Enqueue("blocker", _ => gate.Task);
        var ran = false;
        var victim = queue.Enqueue("victim", _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        Assert.True(victim.Cancel());
        Assert.Equal(QueuedTaskState.Cancelled, await victim.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken));
        gate.SetResult();
        await blocker.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(ran);
        Assert.False(victim.Cancel());
    }

    [Fact]
    public async Task Cancelling_a_running_task_cancels_its_token()
    {
        await using var queue = new TaskQueue(1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = queue.Enqueue("long", async ctx =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
        });

        await started.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(QueuedTaskState.Running, task.State);
        task.Cancel();
        Assert.Equal(QueuedTaskState.Cancelled, await task.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Null(task.Error);
    }

    [Fact]
    public async Task An_exception_fails_only_its_task_and_the_worker_carries_on()
    {
        await using var queue = new TaskQueue(1);
        var bad = queue.Enqueue("bad", _ => throw new InvalidOperationException("boom"));
        var good = queue.Enqueue("good", _ => Task.CompletedTask);

        Assert.Equal(QueuedTaskState.Failed, await bad.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.IsType<InvalidOperationException>(bad.Error);
        Assert.Equal(QueuedTaskState.Completed, await good.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Progress_is_reported_with_indeterminate_total()
    {
        await using var queue = new TaskQueue(1);
        var seen = new List<TransferProgress>();
        var task = queue.Enqueue("p", ctx =>
        {
            ctx.Report(0, null);
            ctx.Report(5, 10);
            ctx.Progress.Report(new TransferProgress(10, 10));
            return Task.CompletedTask;
        });
        task.ProgressChanged += (_, p) =>
        {
            lock (seen)
            {
                seen.Add(p);
            }
        };

        await task.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(new TransferProgress(10, 10), task.Progress);
        Assert.Contains(seen, p => p.IsIndeterminate);
        Assert.Equal(0.5, new TransferProgress(5, 10).Fraction);
    }

    [Fact]
    public async Task Dispose_cancels_running_and_queued_tasks()
    {
        var queue = new TaskQueue(1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = queue.Enqueue("running", async ctx =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
        });
        var queued = queue.Enqueue("queued", _ => Task.CompletedTask);
        await started.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        await queue.DisposeAsync().AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(QueuedTaskState.Cancelled, await running.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Equal(QueuedTaskState.Cancelled, await queued.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => queue.Enqueue("late", _ => Task.CompletedTask));
    }

    [Fact]
    public async Task State_events_follow_the_life_cycle()
    {
        await using var queue = new TaskQueue(1);
        var states = new List<QueuedTaskState>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = queue.Enqueue("blocker", _ => gate.Task);
        var task = queue.Enqueue("t", _ => Task.CompletedTask);
        task.StateChanged += t =>
        {
            lock (states)
            {
                states.Add(t.State);
            }
        };
        gate.SetResult();
        await task.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken);
        await blocker.Completion;
        Assert.Equal([QueuedTaskState.Running, QueuedTaskState.Completed], states);
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
