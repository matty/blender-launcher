namespace BlenderLauncher.App.SelfTest;

/// <summary>
/// One check run by <c>--self-test</c>. Implementations are registered by hand in <see cref="SelfTestRegistry"/>.
/// </summary>
public interface ISelfTestCheck
{
    /// <summary>Short stable identifier, for example "S05". Used by <c>--only</c> and in the result file.</summary>
    string Id { get; }

    /// <summary>Human-readable name for the result file.</summary>
    string Name { get; }

    /// <summary>
    /// When true, <see cref="RunAsync"/> is called on the Avalonia UI thread. When false it runs on a
    /// thread-pool thread and can still reach the UI thread through <see cref="SelfTestContext.RunOnUiThreadAsync(Action)"/>.
    /// </summary>
    bool NeedsUiThread { get; }

    /// <summary>When true, the check is skipped under <c>--offline</c>.</summary>
    bool NeedsNetwork { get; }

    /// <summary>How long the check may run before it is reported as failed. The token is cancelled at this point.</summary>
    TimeSpan Timeout => SelfTestRunner.DefaultTimeout;

    /// <summary>Runs the check. Exceptions are caught by the runner and reported as Fail.</summary>
    Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct);
}
