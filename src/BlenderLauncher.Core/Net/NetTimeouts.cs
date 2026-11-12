namespace BlenderLauncher.Core.Net;

/// <summary>
/// Per-call time limits. The shared client has no overall timeout (see <see cref="HttpClientProvider"/>), so every
/// call that is not a streaming download bounds itself:
/// <code>
/// using var cts = NetTimeouts.Linked(ct, NetTimeouts.Request);
/// using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
/// var body = await response.Content.ReadAsStringAsync(cts.Token);
/// </code>
/// A timeout then surfaces as <see cref="OperationCanceledException"/> while <c>ct</c> itself is not cancelled;
/// <see cref="IsTimeout"/> tells the two apart.
/// </summary>
public static class NetTimeouts
{
    /// <summary>Default for an API or index request, including reading its body.</summary>
    public static readonly TimeSpan Request = TimeSpan.FromSeconds(30);

    /// <summary>For a <c>HEAD</c> request or other tiny probe.</summary>
    public static readonly TimeSpan Probe = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A download aborts when no bytes arrive for this long (upstream's download used <c>timeout=10</c> as a read
    /// timeout); the download code restarts the timer as data arrives.
    /// </summary>
    public static readonly TimeSpan DownloadStall = TimeSpan.FromSeconds(30);

    /// <summary>A source linked to <paramref name="cancellationToken"/> that also cancels after <paramref name="timeout"/>.</summary>
    public static CancellationTokenSource Linked(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return cts;
    }

    /// <summary>True when <paramref name="exception"/> is a cancellation that <paramref name="callerToken"/> did not cause.</summary>
    public static bool IsTimeout(Exception exception, CancellationToken callerToken) =>
        exception is OperationCanceledException && !callerToken.IsCancellationRequested;
}
