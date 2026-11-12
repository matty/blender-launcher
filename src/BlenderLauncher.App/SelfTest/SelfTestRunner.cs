using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Threading;

namespace BlenderLauncher.App.SelfTest;

/// <summary>
/// Runs the registered checks one after another and writes the result file.
/// Each line: <c>PASS|FAIL|SKIP &lt;TAB&gt; Id &lt;TAB&gt; Name &lt;TAB&gt; ms &lt;TAB&gt; detail</c>, then a final
/// <c>SUMMARY</c> line. Lines are written as each check finishes, so a hang still leaves partial results.
/// </summary>
internal sealed class SelfTestRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SelfTestOptions _options;
    private readonly SelfTestLog _log;

    public SelfTestRunner(SelfTestOptions options, SelfTestLog log)
    {
        _options = options;
        _log = log;
    }

    /// <summary>Runs every selected check. Returns the process exit code: 0 when nothing failed, otherwise 1.</summary>
    public async Task<int> RunAsync(IReadOnlyList<ISelfTestCheck> checks)
    {
        var runDirectory = Path.Combine(
            Path.GetTempPath(),
            "BlenderLauncherNet-selftest",
            string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}"));
        Directory.CreateDirectory(runDirectory);
        _log.Write("runner", $"run directory {runDirectory}");

        File.WriteAllText(_options.OutputPath, string.Empty, Utf8NoBom);

        int pass = 0, fail = 0, skip = 0;
        var total = Stopwatch.StartNew();

        foreach (var check in checks)
        {
            if (_options.Only is { } only && !only.Contains(check.Id))
            {
                continue;
            }

            var watch = Stopwatch.StartNew();
            var result = await RunOneAsync(check, runDirectory).ConfigureAwait(false);
            watch.Stop();

            switch (result.Status)
            {
                case SelfTestStatus.Pass: pass++; break;
                case SelfTestStatus.Fail: fail++; break;
                default: skip++; break;
            }

            WriteLine(FormatLine(result.Status, check.Id, check.Name, watch.ElapsedMilliseconds, result.Detail));
            _log.Write(check.Id, $"{result.Status} after {watch.ElapsedMilliseconds} ms: {result.Detail}");
        }

        var exitCode = fail == 0 ? 0 : 1;
        WriteLine(FormatSummary(pass, fail, skip, total.ElapsedMilliseconds, exitCode));

        if (!_options.KeepTemp)
        {
            TryDelete(runDirectory);
        }

        return exitCode;
    }

    private async Task<SelfTestResult> RunOneAsync(ISelfTestCheck check, string runDirectory)
    {
        if (check.NeedsNetwork && _options.Offline)
        {
            return SelfTestResult.Skip("offline");
        }

        var timeout = check.Timeout;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var ctx = new SelfTestContext(check.Id, _options.Offline, runDirectory, _log);
            var task = check.NeedsUiThread
                ? Dispatcher.UIThread.InvokeAsync(() => check.RunAsync(ctx, cts.Token))
                : Task.Run(() => check.RunAsync(ctx, cts.Token));

            // WaitAsync also covers a check that ignores its token.
            return await task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return SelfTestResult.Fail(Timed(timeout));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return SelfTestResult.Fail(Timed(timeout));
        }
        catch (Exception ex)
        {
            _log.Write(check.Id, ex.ToString());
            return SelfTestResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Timed(TimeSpan timeout) =>
        string.Create(CultureInfo.InvariantCulture, $"timed out after {timeout.TotalSeconds:0.#} s");

    private void WriteLine(string line)
    {
        File.AppendAllText(_options.OutputPath, line + Environment.NewLine, Utf8NoBom);
    }

    internal static string FormatLine(SelfTestStatus status, string id, string name, long ms, string detail) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{StatusText(status)}\t{Flatten(id)}\t{Flatten(name)}\t{ms}\t{Flatten(detail)}");

    internal static string FormatSummary(int pass, int fail, int skip, long ms, int exitCode) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"SUMMARY\tpass={pass}\tfail={fail}\tskip={skip}\t{ms}\texit={exitCode}");

    private static string StatusText(SelfTestStatus status) => status switch
    {
        SelfTestStatus.Pass => "PASS",
        SelfTestStatus.Fail => "FAIL",
        _ => "SKIP",
    };

    private static string Flatten(string text) =>
        text.Replace('\t', ' ').Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');

    private void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write("runner", $"could not delete {directory}: {ex.Message}");
        }
    }
}
