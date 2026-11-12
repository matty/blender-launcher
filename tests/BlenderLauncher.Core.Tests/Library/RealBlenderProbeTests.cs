using BlenderLauncher.Core.Library;

namespace BlenderLauncher.Core.Tests.Library;

/// <summary>Runs a real Blender only when <c>BL_TEST_BLENDER_EXE</c> points at one; otherwise passes without doing anything.</summary>
public class RealBlenderProbeTests
{
    private static string? Exe => Environment.GetEnvironmentVariable("BL_TEST_BLENDER_EXE") is { Length: > 0 } p && File.Exists(p) ? p : null;

    [Fact]
    public async Task Real_executable_reports_a_parsable_version()
    {
        if (Exe is not { } exe)
        {
            return;
        }

        var output = await new ProcessBlenderVersionProbe().RunAsync(exe, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var parsed = new BuildInfoReader(new ProcessBlenderVersionProbe()).ParseVersionOutput(output);

        Assert.NotNull(parsed.Subversion);
        Assert.NotEqual("", parsed.BuildHash);
        Assert.NotNull(parsed.CommitTime);
    }

    [Fact]
    public async Task Probe_reports_missing_executable()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "blender.exe");

        await Assert.ThrowsAsync<BlenderProbeException>(() =>
            new ProcessBlenderVersionProbe().RunAsync(missing, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Probe_times_out_and_kills_a_hanging_process()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A batch file that waits; cmd treats "-v" as its first argument and ignores it.
        var dir = Path.Combine(Path.GetTempPath(), "bl-lib-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var script = Path.Combine(dir, "hang.cmd");
            await File.WriteAllTextAsync(script, "@ping -n 30 127.0.0.1 >nul\r\n", TestContext.Current.CancellationToken);
            var watch = System.Diagnostics.Stopwatch.StartNew();

            var e = await Assert.ThrowsAsync<BlenderProbeException>(() =>
                new ProcessBlenderVersionProbe().RunAsync(script, TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken));

            Assert.True(e.TimedOut);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
