using System.ComponentModel;
using System.Runtime.InteropServices;
using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Platform.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace BlenderLauncher.Platform.Windows.Processes;

/// <summary>
/// A process started by <see cref="HiddenConsoleLauncher"/>. Holding the process handle (rather than a
/// process ID) means the exit code stays readable after exit and the ID cannot be reused under us.
/// </summary>
public sealed class LaunchedProcess : ILaunchedProcess
{
    private readonly SafeProcessHandle _handle;

    internal LaunchedProcess(SafeProcessHandle handle, int id)
    {
        _handle = handle;
        Id = id;
    }

    public int Id { get; }

    public bool HasExited => !TryGetExitCode(out var code) || code != ProcessNative.StillActive;

    /// <summary>The exit code, or null while the process is running.</summary>
    public int? ExitCode => TryGetExitCode(out var code) && code != ProcessNative.StillActive ? (int)code : null;

    /// <summary>Completes when the process exits. Waits on the handle through the thread pool, no thread is blocked.</summary>
    public async Task<int> WaitForExitAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var waitHandle = new ProcessWaitHandle(_handle);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle, static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), tcs, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
        }

        if (!TryGetExitCode(out var exitCode))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetExitCodeProcess failed");
        }

        return unchecked((int)exitCode);
    }

    /// <summary>Terminates the process (not its children).</summary>
    public void Kill(uint exitCode = 1)
    {
        if (!ProcessNative.TerminateProcess(_handle, exitCode) && !HasExited)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "TerminateProcess failed");
        }
    }

    public void Dispose() => _handle.Dispose();

    private bool TryGetExitCode(out uint exitCode) => ProcessNative.GetExitCodeProcess(_handle, out exitCode);

    /// <summary>Exposes the process handle as a WaitHandle without taking ownership of it.</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process)
        {
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
        }
    }
}
