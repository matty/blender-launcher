using System.Diagnostics;

namespace BlenderLauncher.Core.Pipeline;

/// <summary>
/// Progress of a step: <see cref="Done"/> units (bytes) of <see cref="Total"/>. A null total means indeterminate,
/// where upstream used <c>(0, 0)</c>.
/// </summary>
public readonly record struct TransferProgress(long Done, long? Total)
{
    public static TransferProgress Indeterminate { get; } = new(0, null);

    public bool IsIndeterminate => Total is null;

    /// <summary>Done as a fraction from 0 to 1, or null when indeterminate.</summary>
    public double? Fraction => Total is > 0 and var t ? Math.Clamp((double)Done / t, 0, 1) : Total is 0 ? 1 : null;
}

/// <summary>Passes progress on at most once per interval, plus the first and any forced report.</summary>
internal sealed class ProgressThrottle(IProgress<TransferProgress>? target, TimeSpan interval)
{
    private long _lastTicks = long.MinValue;

    public void Report(long done, long? total, bool force = false)
    {
        if (target is null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (!force && _lastTicks != long.MinValue && Stopwatch.GetElapsedTime(_lastTicks, now) < interval)
        {
            return;
        }

        _lastTicks = now;
        target.Report(new TransferProgress(done, total));
    }
}

/// <summary>
/// An <see cref="IProgress{T}"/> that calls back on the reporting thread. <see cref="Progress{T}"/> posts to the
/// captured synchronisation context or the thread pool, which reorders reports; services raise events on worker
/// threads and leave marshalling to the view models.
/// </summary>
public sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
