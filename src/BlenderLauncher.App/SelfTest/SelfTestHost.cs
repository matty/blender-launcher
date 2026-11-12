using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace BlenderLauncher.App.SelfTest;

/// <summary>
/// Runs the self-test inside a fully initialised Avalonia application that has no main window,
/// so checks can create windows, render and use the UI thread. Shuts the app down when the checks finish.
/// </summary>
internal static class SelfTestHost
{
    /// <summary>Grace period after the results are written before the process is forced to exit.</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(15);

    public static int Run(string[] args)
    {
        var options = SelfTestOptions.Parse(args, out var error);
        if (error is not null)
        {
            WriteFatal(options.OutputPath, "Args", "Command line", error, exitCode: 2);
            return 2;
        }

        var log = new SelfTestLog(options.OutputPath + ".log");
        log.Write("host", $"version {Core.AppIdentity.Version}, args: {string.Join(' ', args)}");

        App.CreateMainWindow = false;
        var exitCode = 1;
        try
        {
            exitCode = Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, lifetime =>
            {
                lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                lifetime.Startup += (_, _) => _ = RunChecksAndShutDownAsync(lifetime, options, log);
            });
        }
        catch (Exception ex)
        {
            log.Write("host", ex.ToString());
            WriteFatal(options.OutputPath, "Avalonia", "Avalonia start-up", $"{ex.GetType().Name}: {ex.Message}", exitCode: 1);
            return 1;
        }

        log.Write("host", $"exit code {exitCode}");
        return exitCode;
    }

    private static async Task RunChecksAndShutDownAsync(
        IClassicDesktopStyleApplicationLifetime lifetime,
        SelfTestOptions options,
        SelfTestLog log)
    {
        int exitCode;
        try
        {
            var runner = new SelfTestRunner(options, log);
            var checks = SelfTestRegistry.CreateChecks();

            // The runner itself lives on the thread pool; UI checks are marshalled back to this thread.
            exitCode = await Task.Run(() => runner.RunAsync(checks));
        }
        catch (Exception ex)
        {
            log.Write("host", ex.ToString());
            WriteFatal(options.OutputPath, "Runner", "Self-test runner", $"{ex.GetType().Name}: {ex.Message}", exitCode: 1);
            exitCode = 1;
        }

        // If a timed-out check has wedged the UI thread, Shutdown never gets processed; force the exit.
        var forced = exitCode;
        _ = Task.Delay(ShutdownGrace).ContinueWith(
            _ =>
            {
                log.Write("host", "UI thread did not shut down in time; forcing exit");
                Environment.Exit(forced);
            },
            TaskScheduler.Default);

        lifetime.Shutdown(exitCode);
    }

    private static void WriteFatal(string path, string id, string name, string detail, int exitCode)
    {
        try
        {
            var text = new StringBuilder()
                .AppendLine(SelfTestRunner.FormatLine(SelfTestStatus.Fail, id, name, 0, detail))
                .AppendLine(SelfTestRunner.FormatSummary(0, 1, 0, 0, exitCode))
                .ToString();
            File.AppendAllText(path, text, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere left to report; the exit code still signals failure.
        }
    }
}
