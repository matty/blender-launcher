using BlenderLauncher.Core.Builds;
using BlenderLauncher.Platform.Windows.Processes;

namespace BlenderLauncher.App.Tests.Processes;

/// <summary>The Windows <c>IProcessLauncher</c> against real processes.</summary>
public sealed class WindowsProcessLauncherTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("bl-launch-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Batch_executable_runs_through_comspec_in_the_build_folder()
    {
        // Writes its working directory and first argument, then exits 5.
        File.WriteAllText(Path.Combine(_folder, "run.cmd"), "@echo off\r\necho %CD%>where.txt\r\necho %~1>arg.txt\r\nexit /b 5\r\n");
        var command = LaunchArgsBuilder.Build(
            new LaunchRequest { BuildFolder = _folder, CustomExecutable = "run.cmd", StartupArguments = "\"two words\"", HideConsole = true },
            File.Exists);
        Assert.True(command.UsesCmd);

        var launcher = new WindowsProcessLauncher();
        using var process = launcher.Start(command, _folder);
        var code = await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(5, code);
        Assert.Equal(_folder, File.ReadAllText(Path.Combine(_folder, "where.txt")).Trim(), ignoreCase: true);
        Assert.Equal("two words", File.ReadAllText(Path.Combine(_folder, "arg.txt")).Trim());
    }

    [Fact]
    public async Task Close_request_skips_processes_without_windows_and_console_windows()
    {
        var command = LaunchArgsBuilder.Build(
            new LaunchRequest { BuildFolder = _folder, CustomExecutable = "wait.cmd", HideConsole = false },
            File.Exists);
        File.WriteAllText(Path.Combine(_folder, "wait.cmd"), "@ping -n 4 127.0.0.1 >nul\r\n");
        var launcher = new WindowsProcessLauncher();
        using var process = launcher.Start(command, _folder);
        try
        {
            // A visible console belongs to the console host, and console windows are never sent WM_CLOSE.
            await Task.Delay(500, Ct);
            Assert.Equal(0, launcher.RequestClose([process.Id]));
            Assert.Empty(WindowsProcessLauncher.FindTopLevelWindows([process.Id]));
            Assert.Equal(0, launcher.RequestClose([]));
        }
        finally
        {
            ((LaunchedProcess)process).Kill();
        }
    }

    [Fact]
    public void ComSpec_is_a_full_path_to_cmd()
    {
        var comSpec = WindowsProcessLauncher.ComSpec();
        Assert.True(Path.IsPathFullyQualified(comSpec));
        Assert.EndsWith("cmd.exe", comSpec, StringComparison.OrdinalIgnoreCase);
    }
}
