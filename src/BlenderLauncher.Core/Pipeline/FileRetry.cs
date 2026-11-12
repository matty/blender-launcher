using BlenderLauncher.Core.Logging;

namespace BlenderLauncher.Core.Pipeline;

/// <summary>How often and how far apart a locked-file operation is retried.</summary>
public sealed record RetryPolicy(int Attempts, TimeSpan Delay)
{
    /// <summary>Upstream's <c>retry_on_permission_error</c>: 10 attempts, 0.5 s apart.</summary>
    public static RetryPolicy Default { get; } = new(10, TimeSpan.FromMilliseconds(500));

    /// <summary>One attempt, no retry.</summary>
    public static RetryPolicy None { get; } = new(1, TimeSpan.Zero);
}

/// <summary>
/// Port of upstream's <c>retry_on_permission_error</c> (<c>modules/file_utils.py</c>), aimed at antivirus and
/// indexer locks on Windows. Python's <c>PermissionError</c> covers access denied and, on Windows, sharing
/// violations; here that is <see cref="UnauthorizedAccessException"/> and an <see cref="IOException"/> whose
/// HRESULT is a sharing, lock or access-denied error.
/// </summary>
public static class FileRetry
{
    private const int ErrorAccessDenied = unchecked((int)0x80070005);
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    /// <summary>True for the errors worth retrying.</summary>
    public static bool IsLock(Exception exception) => exception switch
    {
        UnauthorizedAccessException => true,
        IOException io => io.HResult is ErrorSharingViolation or ErrorLockViolation or ErrorAccessDenied,
        _ => false,
    };

    /// <summary>Runs <paramref name="action"/>, retrying on a lock as <paramref name="policy"/> says; the last error is rethrown.</summary>
    public static async Task RunAsync(Action action, RetryPolicy? policy = null, Log? log = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await RunAsync<object?>(() =>
        {
            action();
            return null;
        }, policy, log, ct).ConfigureAwait(false);
    }

    /// <summary>Runs <paramref name="func"/>, retrying on a lock as <paramref name="policy"/> says; the last error is rethrown.</summary>
    public static async Task<T> RunAsync<T>(Func<T> func, RetryPolicy? policy = null, Log? log = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(func);
        policy ??= RetryPolicy.Default;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return func();
            }
            catch (Exception ex) when (IsLock(ex) && attempt < policy.Attempts)
            {
                log?.Warning($"Attempt {attempt}/{policy.Attempts} failed: {ex.Message}");
                await Task.Delay(policy.Delay, ct).ConfigureAwait(false);
            }
        }
    }
}
