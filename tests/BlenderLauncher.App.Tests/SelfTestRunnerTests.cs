using BlenderLauncher.App.SelfTest;

namespace BlenderLauncher.App.Tests;

public class SelfTestRunnerTests
{
    [Fact]
    public async Task Exceptions_and_timeouts_fail_and_offline_skips_network_checks()
    {
        var output = Path.Combine(Path.GetTempPath(), $"bl-selftest-{Guid.NewGuid():N}.txt");
        try
        {
            var options = new SelfTestOptions(output, Offline: true, Only: null, KeepTemp: false);
            var runner = new SelfTestRunner(options, new SelfTestLog(null));
            ISelfTestCheck[] checks =
            [
                new FakeCheck("ok", (_, _) => Task.FromResult(SelfTestResult.Pass("fine"))),
                new FakeCheck("throws", (_, _) => throw new InvalidOperationException("boom")),
                new FakeCheck("hangs", (_, _) => Task.Delay(Timeout.Infinite).ContinueWith(_ => SelfTestResult.Pass())),
                new FakeCheck("net", (_, _) => Task.FromResult(SelfTestResult.Pass()), needsNetwork: true),
                new FakeCheck("dir", (ctx, _) => Task.FromResult(
                    Directory.Exists(ctx.WorkDirectory) ? SelfTestResult.Pass() : SelfTestResult.Fail("no dir"))),
            ];

            var exitCode = await runner.RunAsync(checks);

            var lines = await File.ReadAllLinesAsync(output, TestContext.Current.CancellationToken);
            Assert.Equal(1, exitCode);
            Assert.StartsWith("PASS\tok\t", lines[0], StringComparison.Ordinal);
            Assert.StartsWith("FAIL\tthrows\t", lines[1], StringComparison.Ordinal);
            Assert.EndsWith("InvalidOperationException: boom", lines[1], StringComparison.Ordinal);
            Assert.StartsWith("FAIL\thangs\t", lines[2], StringComparison.Ordinal);
            Assert.Contains("timed out", lines[2], StringComparison.Ordinal);
            Assert.Equal("SKIP\tnet\tnet\t0\toffline", lines[3]);
            Assert.StartsWith("PASS\tdir\t", lines[4], StringComparison.Ordinal);
            Assert.StartsWith("SUMMARY\tpass=2\tfail=2\tskip=1\t", lines[5], StringComparison.Ordinal);
            Assert.EndsWith("exit=1", lines[5], StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(output);
        }
    }

    private sealed class FakeCheck(
        string id,
        Func<SelfTestContext, CancellationToken, Task<SelfTestResult>> run,
        bool needsNetwork = false) : ISelfTestCheck
    {
        public string Id => id;
        public string Name => id;
        public bool NeedsUiThread => false;
        public bool NeedsNetwork => needsNetwork;
        public TimeSpan Timeout => TimeSpan.FromMilliseconds(300);

        public Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct) => run(ctx, ct);
    }
}
