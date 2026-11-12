using System.Collections.Concurrent;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Core.Processes;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Core.Tests.Library;

namespace BlenderLauncher.Core.Tests.Processes;

/// <summary>A process whose exit the test controls.</summary>
internal sealed class FakeProcess(int id) : ILaunchedProcess
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Id { get; } = id;

    public bool Disposed { get; private set; }

    public void Exit(int code = 0) => _exit.TrySetResult(code);

    public Task<int> WaitForExitAsync(CancellationToken ct = default) => _exit.Task.WaitAsync(ct);

    public void Dispose() => Disposed = true;
}

/// <summary>An <see cref="IProcessLauncher"/> that records commands and hands out <see cref="FakeProcess"/>es.</summary>
internal sealed class FakeLauncher : IProcessLauncher
{
    private int _nextId = 1000;

    public ConcurrentQueue<(LaunchCommand Command, string WorkingDirectory)> Started { get; } = new();

    public ConcurrentDictionary<int, FakeProcess> Processes { get; } = new();

    public ConcurrentQueue<int[]> CloseRequests { get; } = new();

    /// <summary>When set, a close request makes the processes exit with this code.</summary>
    public int? ExitOnClose { get; set; } = 0;

    public bool IsAvailable => true;

    public ILaunchedProcess Start(LaunchCommand command, string workingDirectory)
    {
        Started.Enqueue((command, workingDirectory));
        var process = new FakeProcess(Interlocked.Increment(ref _nextId));
        Processes[process.Id] = process;
        return process;
    }

    public int RequestClose(IReadOnlyCollection<int> processIds)
    {
        CloseRequests.Enqueue([.. processIds]);
        if (ExitOnClose is { } code)
        {
            foreach (var id in processIds)
            {
                Processes[id].Exit(code);
            }
        }

        return processIds.Count;
    }
}

public sealed class LaunchServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
    private readonly TempDir _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    private async Task<(LibraryService Library, LibraryState State, BuildInfo Build)> LibraryAsync()
    {
        var root = _temp.Folder("Lib");
        var folder = _temp.Folder("Lib", "daily", "blender-4.3.0");
        await Samples.WriteBlinfoAsync(folder, Samples.Info());
        File.WriteAllText(Path.Combine(folder, "blender.exe"), "");
        var state = await LibraryState.LoadAsync(_temp.Combine("state"), ct: Ct);
        var library = new LibraryService(new BuildInfoReader(FakeProbe.Failing()), state, time: new FixedClock(Now));
        await library.ScanAsync(root, ct: Ct);
        return (library, state, library.Current.Builds.Single());
    }

    [Fact]
    public async Task Launch_counts_processes_stamps_last_used_and_observes_exits()
    {
        var (library, state, build) = await LibraryAsync();
        await using var _ = state;
        using var __ = library;
        var launcher = new FakeLauncher();
        var settings = new LaunchingSettings { OnLaunchAction = OnLaunchAction.Minimise, HideConsole = true, BlenderStartupArguments = "--factory-startup -noaudio" };
        await using var service = new LaunchService(launcher, library, () => settings, time: new FixedClock(Now));
        var launched = new ConcurrentQueue<BuildLaunchedEventArgs>();
        var counts = new ConcurrentQueue<RunningCountChangedEventArgs>();
        service.Launched += (_, e) => launched.Enqueue(e);
        service.RunningCountChanged += (_, e) => counts.Enqueue(e);

        var first = service.Launch(build);
        var second = service.Launch(build, LaunchMode.OpenLast);

        Assert.Equal(2, service.GetRunningCount(build.Link));
        Assert.Equal(2, service.TotalRunning);
        Assert.Equal([OnLaunchAction.Minimise, OnLaunchAction.DoNothing], launched.Select(e => e.Action));
        Assert.Equal(Now, library.GetState(build.Link).LastUsed);

        var (command, directory) = launcher.Started.First();
        Assert.Equal(build.Link, directory);
        Assert.Equal(Path.Combine(build.Link, "blender.exe"), command.ApplicationName);
        Assert.Equal([Path.Combine(build.Link, "blender.exe"), "--factory-startup", "-noaudio"], command.Argv);
        Assert.True(command.HideConsole);
        Assert.Equal("--open-last", launcher.Started.Last().Command.Argv[^1]);

        launcher.Processes[first.ProcessId].Exit(3);
        await WaitUntil(() => service.GetRunningCount(build.Link) == 1);
        launcher.Processes[second.ProcessId].Exit(0);
        await service.WhenNotRunningAsync(build.Link, Ct);

        Assert.Equal(0, service.GetRunningCount(build.Link));
        Assert.Equal([1, 2, 1, 0], counts.Select(c => c.Count));
        Assert.Equal(3, counts.Single(c => c.ProcessId == first.ProcessId && c.ExitCode is not null).ExitCode);
        Assert.True(launcher.Processes[first.ProcessId].Disposed);
    }

    [Fact]
    public async Task Quit_all_asks_every_running_process_of_the_build()
    {
        var (library, state, build) = await LibraryAsync();
        await using var _ = state;
        using var __ = library;
        var launcher = new FakeLauncher();
        await using var service = new LaunchService(launcher, library, () => new LaunchingSettings());

        Assert.Equal(new QuitAllResult(0, 0), service.QuitAll(build.Link));

        var a = service.Launch(build);
        var b = service.Launch(build);
        var result = service.QuitAll(build.Link);

        Assert.Equal(new QuitAllResult(2, 2), result);
        Assert.Equal([a.ProcessId, b.ProcessId], launcher.CloseRequests.Single().Order());
        await service.WhenNotRunningAsync(build.Link, Ct);
        Assert.Empty(service.GetRunning());
    }

    [Fact]
    public async Task Batch_executables_and_scripts_are_tracked_as_cmd_with_a_console()
    {
        var (library, state, build) = await LibraryAsync();
        await using var _ = state;
        using var __ = library;
        var launcher = new FakeLauncher();
        await using var service = new LaunchService(launcher, library, () => new LaunchingSettings { HideConsole = true });

        var script = service.Launch(build, script: "blender_debug_log.cmd");
        var command = launcher.Started.Single().Command;

        Assert.True(script.ThroughCmd);
        Assert.True(command.UsesCmd);
        Assert.True(command.RequiresConsole);
        Assert.False(command.HideConsole);
        Assert.Equal("cmd", command.ApplicationName);
    }

    [Fact]
    public async Task Builds_outside_the_library_launch_without_a_stamp_and_dispose_leaves_them_running()
    {
        var launcher = new FakeLauncher();
        var folder = _temp.Folder("elsewhere", "blender");
        var build = Samples.Info() with { Link = folder };
        var service = new LaunchService(launcher, library: null, () => new LaunchingSettings());

        var process = service.Launch(build);
        Assert.Equal(1, service.GetRunningCount(folder));

        await service.DisposeAsync();
        Assert.True(launcher.Processes[process.ProcessId].Disposed);
        Assert.Throws<ObjectDisposedException>(() => service.Launch(build));
    }

    [Fact]
    public async Task Null_launcher_reports_unavailable_and_throws()
    {
        await using var service = new LaunchService(NullProcessLauncher.Instance, null, () => new LaunchingSettings());
        Assert.False(service.IsAvailable);
        Assert.Throws<PlatformNotSupportedException>(() => service.Launch(Samples.Info() with { Link = _temp.Path }));
        Assert.Equal(0, service.TotalRunning);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(condition());
    }
}
