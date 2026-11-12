using System.Diagnostics;
using System.Text;

namespace BlenderLauncher.Core.Library;

/// <summary>Executable names inside a build folder (Windows column).</summary>
public static class BuildExecutables
{
    /// <summary>Blender and UPBGE.</summary>
    public const string Blender = "blender.exe";

    /// <summary>Bforartists.</summary>
    public const string Bforartists = "bforartists.exe";
}

/// <summary>Running <c>&lt;exe&gt; -v</c> failed: it could not start, exited with an error, or timed out.</summary>
public sealed class BlenderProbeException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>True when the process was killed because it did not finish in time.</summary>
    public bool TimedOut { get; init; }
}

/// <summary>Runs a build's executable with <c>-v</c> and returns its standard output.</summary>
public interface IBlenderVersionProbe
{
    /// <summary>
    /// Runs <paramref name="executable"/> <c>-v</c> and returns standard output. Throws
    /// <see cref="BlenderProbeException"/> when it cannot start, exits non-zero or exceeds
    /// <paramref name="timeout"/>, and <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    Task<string> RunAsync(string executable, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// The real probe. Upstream's <c>_check_output([exe, "-v"])</c> used <c>shell=True</c> on Windows; this
/// starts the executable directly with an argument list, no window, stdin closed and stderr
/// read and discarded, and kills the process tree on timeout or cancellation.
/// </summary>
public sealed class ProcessBlenderVersionProbe : IBlenderVersionProbe
{
    /// <inheritdoc />
    public async Task<string> RunAsync(string executable, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
        };
        start.ArgumentList.Add("-v");

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new BlenderProbeException($"Could not start {executable}");
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new BlenderProbeException($"Could not start {executable}: {e.Message}", e);
        }

        process.StandardInput.Close();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            var output = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new BlenderProbeException($"{executable} -v exited with code {process.ExitCode}");
            }

            return output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            throw new BlenderProbeException($"{executable} -v did not finish within {timeout.TotalSeconds:0.#} s") { TimedOut = true };
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }
}
