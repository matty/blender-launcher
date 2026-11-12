using System.Diagnostics;
using BlenderLauncher.Core.Library;

namespace BlenderLauncher.Core.Tests.Library;

public class BuildFolderInspectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Measures_nested_files()
    {
        using var temp = new TempDir();
        temp.File("b/a.txt", new string('a', 100));
        temp.File("b/sub/deeper/c.bin", new string('c', 250));
        temp.Folder("b", "empty");

        Assert.Equal(350, BuildFolderInspector.MeasureSize(temp.Combine("b"), Ct));
    }

    [Fact]
    public void Junctions_are_not_followed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDir();
        temp.File("outside/big.bin", new string('x', 1000));
        temp.File("b/a.txt", new string('a', 10));
        var link = temp.Combine("b", "link");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe")
               {
                   ArgumentList = { "/c", "mklink", "/J", link, temp.Combine("outside") },
                   CreateNoWindow = true,
                   UseShellExecute = false,
                   RedirectStandardOutput = true,
               })!)
        {
            mklink.WaitForExit();
            if (mklink.ExitCode != 0)
            {
                return; // junctions unavailable here
            }
        }

        Assert.Equal(10, BuildFolderInspector.MeasureSize(temp.Combine("b"), Ct));
        Directory.Delete(link); // removes the junction only
    }

    [Fact]
    public void Measure_is_cancellable_and_requires_folder()
    {
        using var temp = new TempDir();
        temp.File("b/a.txt", "a");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => BuildFolderInspector.MeasureSize(temp.Combine("b"), cts.Token));
        Assert.Throws<DirectoryNotFoundException>(() => BuildFolderInspector.MeasureSize(temp.Combine("missing"), Ct));
    }

    [Fact]
    public async Task Measurer_runs_queued_requests_and_honours_cancellation()
    {
        using var temp = new TempDir();
        temp.File("a/x.bin", new string('x', 42));
        temp.File("b/y.bin", new string('y', 7));
        using var measurer = new BuildSizeMeasurer();

        var a = measurer.MeasureAsync(temp.Combine("a"), Ct);
        var b = measurer.MeasureAsync(temp.Combine("b"), Ct);
        Assert.Equal(42, await a);
        Assert.Equal(7, await b);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => measurer.MeasureAsync(temp.Combine("a"), cts.Token));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => measurer.MeasureAsync(temp.Combine("missing"), Ct));
    }

    [Fact]
    public void Python_version_from_dll_name_when_no_version_resource()
    {
        using var temp = new TempDir();
        temp.File("b/4.1/python/bin/python310.dll", "not a real dll");
        temp.File("b/4.2/python/bin/python311.dll", "not a real dll");
        temp.File("b/4.2/python/bin/python3.dll", "stable ABI shim");

        Assert.Equal("3.11", BuildFolderInspector.ReadPythonVersion(temp.Combine("b")));
    }

    [Fact]
    public void Python_version_from_lib_folder()
    {
        using var temp = new TempDir();
        temp.Folder("b", "3.6", "python", "lib", "python3.10");

        Assert.Equal("3.10", BuildFolderInspector.ReadPythonVersion(temp.Combine("b")));
    }

    [Fact]
    public void No_python_gives_null()
    {
        using var temp = new TempDir();
        temp.Folder("b", "datafiles");
        temp.Folder("b", "4.2", "scripts");

        Assert.Null(BuildFolderInspector.ReadPythonVersion(temp.Combine("b")));
        Assert.Null(BuildFolderInspector.ReadPythonVersion(temp.Combine("missing")));
    }

    [Fact]
    public void Python_version_from_real_version_resource_when_available()
    {
        // Uses any real CPython DLL on the machine; the layout is built around a copy of it.
        var python = Environment.GetEnvironmentVariable("BL_TEST_PYTHON_DLL");
        if (string.IsNullOrEmpty(python) || !File.Exists(python))
        {
            return;
        }

        using var temp = new TempDir();
        var bin = temp.Folder("b", "4.2", "python", "bin");
        File.Copy(python, Path.Combine(bin, Path.GetFileName(python)));
        var expected = FileVersionInfo.GetVersionInfo(python).ProductVersion!;

        Assert.StartsWith(BuildFolderInspector.ReadPythonVersion(temp.Combine("b"))!, expected);
    }
}
