using System.Diagnostics;
using System.Runtime.InteropServices;
using BlenderLauncher.Platform.Windows.Interop;
using BlenderLauncher.Platform.Windows.Processes;

namespace BlenderLauncher.App.SelfTest.Checks;

/// <summary>
/// Spike S10: a console child started hidden through CreateProcessW (CREATE_NEW_CONSOLE, SW_HIDE, no
/// handle inheritance) with its exit code observed; a child with stdout captured; a hanging child killed
/// by a timeout.
/// </summary>
internal sealed class S10ChildProcessCheck : ISelfTestCheck
{
    public string Id => "S10";
    public string Name => "Child processes";
    public bool NeedsUiThread => false;
    public bool NeedsNetwork => false;
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public async Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct)
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var parts = new List<string>();

        // 1. Hidden console child, exit code observed through the process handle.
        using (var child = HiddenConsoleLauncher.Start(cmd, "cmd.exe /c exit 7", ctx.WorkDirectory))
        {
            var code = await child.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (code != 7)
            {
                return SelfTestResult.Fail($"hidden child exit code {code}, expected 7");
            }

            parts.Add($"hidden CreateProcessW exit=7 (pid {child.Id})");
        }

        // 2. Is the hidden child's console window really invisible? Attach to it briefly and ask.
        // ping directly, not through cmd, so that Kill (which does not kill children) leaves nothing behind.
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        using (var child = HiddenConsoleLauncher.Start(ping, CommandLine.Build(ping, ["-n", "10", "127.0.0.1"]), ctx.WorkDirectory))
        {
            await Task.Delay(500, ct);
            var visibility = ProbeConsoleWindow(child.Id);
            ctx.Log($"console window probe: {visibility}");
            child.Kill();
            await child.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (visibility.StartsWith("visible", StringComparison.Ordinal))
            {
                return SelfTestResult.Fail($"hidden child's console window is {visibility}");
            }

            parts.Add($"console window {visibility}");
        }

        // 3. Captured stdout.
        var echo = await CapturedProcessRunner.RunAsync(cmd, ["/c", "echo", "hello"], TimeSpan.FromSeconds(10), ct: ct);
        if (echo.TimedOut || echo.ExitCode != 0 || echo.StandardOutput.Trim() != "hello")
        {
            return SelfTestResult.Fail(
                $"captured run: exit={echo.ExitCode} timedOut={echo.TimedOut} stdout='{echo.StandardOutput.Trim()}' stderr='{echo.StandardError.Trim()}'");
        }

        parts.Add($"captured 'hello' exit=0 in {echo.Elapsed.TotalMilliseconds:F0} ms");

        // 4. Hanging child (cmd -> ping) killed as a tree by the timeout; the reads must not hang on the grandchild.
        var stopwatch = Stopwatch.StartNew();
        var hang = await CapturedProcessRunner.RunAsync(
            cmd, ["/c", "ping", "-n", "30", "127.0.0.1"], TimeSpan.FromSeconds(1), ct: ct);
        stopwatch.Stop();
        if (!hang.TimedOut || stopwatch.Elapsed > TimeSpan.FromSeconds(10))
        {
            return SelfTestResult.Fail($"hanging run: timedOut={hang.TimedOut} after {stopwatch.Elapsed.TotalSeconds:F1} s");
        }

        parts.Add($"hanging child killed by 1 s timeout, returned after {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        return SelfTestResult.Pass(string.Join("; ", parts));
    }

    /// <summary>Attaches to the child's console, asks whether its window is visible, and detaches.</summary>
    private static string ProbeConsoleWindow(int processId)
    {
        if (!ConsoleNative.AttachConsole((uint)processId))
        {
            return $"not probed (AttachConsole error {Marshal.GetLastPInvokeError()})";
        }

        try
        {
            var hwnd = ConsoleNative.GetConsoleWindow();
            return hwnd == 0
                ? "has no window"
                : ConsoleNative.IsWindowVisible(hwnd) ? "visible" : "hidden";
        }
        finally
        {
            ConsoleNative.FreeConsole();
        }
    }
}
