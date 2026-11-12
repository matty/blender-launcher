using System.Diagnostics;
using System.Text;

namespace BlenderLauncher.Platform.Windows.Processes;

/// <summary>The outcome of <see cref="CapturedProcessRunner.RunAsync"/>.</summary>
public sealed record CapturedProcessResult(int? ExitCode, string StandardOutput, string StandardError, bool TimedOut, TimeSpan Elapsed);

/// <summary>
/// Runs a short-lived program with no window and captures its output, for example <c>blender -v</c>.
/// System.Diagnostics.Process is right here: no console is wanted and the pipes must be inherited.
/// On timeout the whole process tree is killed, so a grandchild holding the pipe cannot hang the read.
/// </summary>
public static class CapturedProcessRunner
{
    public static async Task<CapturedProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        Encoding? outputEncoding = null,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding ?? Encoding.UTF8,
            StandardErrorEncoding = outputEncoding ?? Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}");
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited between the timeout and the kill.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        return new CapturedProcessResult(
            timedOut ? null : process.ExitCode, output, error, timedOut, stopwatch.Elapsed);
    }
}
