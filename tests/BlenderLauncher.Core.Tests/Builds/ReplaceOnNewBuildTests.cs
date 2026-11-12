using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Pipeline;
using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Core.Processes;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Core.Tests.Library;
using BlenderLauncher.Core.Tests.Processes;

namespace BlenderLauncher.Core.Tests.Builds;

/// <summary>Replace on new build with fakes: a fake install callback, a fake process launcher, temp folders for the library.</summary>
public sealed class ReplaceOnNewBuildTests : IAsyncLifetime
{
    private static readonly IsoTimestamp T1 = IsoTimestamp.Parse("2026-01-01T00:00:00+00:00");
    private static readonly IsoTimestamp T2 = IsoTimestamp.Parse("2026-02-01T00:00:00+00:00");
    private readonly TempDir _temp = new();
    private readonly FakeLauncher _launcher = new() { ExitOnClose = null };
    private string _root = "";
    private LibraryState _state = null!;
    private LibraryService _library = null!;
    private LaunchService _launches = null!;
    private BuildUpdateService _updates = null!;
    private int _installs;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _root = _temp.Folder("Lib");
        _state = await LibraryState.LoadAsync(_temp.Combine("state"), ct: Ct);
        _library = new LibraryService(new BuildInfoReader(FakeProbe.Failing()), _state);
        _launches = new LaunchService(_launcher, _library, () => new LaunchingSettings());
        _updates = new BuildUpdateService(() => new BuildsSettings(), _library);
    }

    public async ValueTask DisposeAsync()
    {
        await _launches.DisposeAsync();
        _library.Dispose();
        await _state.DisposeAsync();
        _temp.Dispose();
    }

    private static BuildInfo Daily(string hash, IsoTimestamp time, string link = "x") => new()
    {
        Link = link,
        Subversion = "4.3.0-alpha",
        BuildHash = hash,
        CommitTime = time,
        Branch = "daily",
    };

    private static AvailableBuild Download(string hash, IsoTimestamp time) =>
        new(Daily(hash, time, $"https://builder.blender.org/download/daily/blender-4.3.0-alpha+main.{hash}-windows.amd64-release.zip"));

    private async Task<string> InstallOldAsync(Func<BuildInfo, BuildInfo>? change = null, bool flag = true)
    {
        var folder = _temp.Folder("Lib", "daily", "blender-4.3.0-alpha+daily.aaaa");
        await Samples.WriteBlinfoAsync(folder, (change ?? (b => b))(Daily("aaaa", T1)));
        await _library.ScanAsync(_root, ct: Ct);
        var path = _library.Current.Builds.Single().Link;
        _library.UpdateState(path, s => s with { ReplaceOnNewBuild = flag });
        return path;
    }

    // The install callback: creates the new build's folder and adds it, as AppServices.InstallAsync does.
    private async Task<LibraryEntry?> FakeInstallAsync(AvailableBuild build, CancellationToken ct)
    {
        Interlocked.Increment(ref _installs);
        var folder = _temp.Folder("Lib", "daily", $"blender-4.3.0-alpha+daily.{build.Build.BuildHash}");
        await Samples.WriteBlinfoAsync(folder, build.Build);
        File.WriteAllText(Path.Combine(folder, "blender.exe"), "");
        return await _library.AddAsync(folder, build.Build with { Link = folder }, ct: ct);
    }

    private ReplaceOnNewBuild Create(Func<AvailableBuild, CancellationToken, Task<LibraryEntry?>>? install = null, int attempts = 5) =>
        new(_library, _updates, _launches, install ?? FakeInstallAsync, new Remover(NullRecycleBin.Instance, RetryPolicy.None), () => false)
        {
            RetryDelay = TimeSpan.FromMilliseconds(10),
            RemovalAttempts = attempts,
        };

    [Fact]
    public async Task Replaces_a_flagged_build_and_moves_favourite_name_default_and_flag()
    {
        var old = await InstallOldAsync(b => b with { IsFavorite = true, CustomName = "My daily" });
        _library.SetDefaultBuild(old);
        _temp.File("Lib/daily/blender-4.3.0-alpha+daily.aaaa/portable/config/userpref.blend", "prefs");
        await using var replace = Create();
        BuildReplacement? raised = null;
        string? removed = null;
        replace.Replaced += (_, r) => raised = r;
        replace.OldBuildRemoved += (_, p) => removed = p;

        var results = await replace.RunAsync([Download("bbbb", T2)], Ct);
        await replace.WhenIdleAsync();

        var result = Assert.Single(results);
        Assert.Equal(result, raised);
        Assert.Equal(old, result.OldFolder);
        Assert.Equal(old, removed);
        Assert.False(Directory.Exists(old));
        var created = _library.Current.Find(result.NewFolder)!.Build!;
        Assert.Equal("bbbb", created.BuildHash);
        Assert.True(created.IsFavorite);
        Assert.Equal("My daily", created.CustomName);
        Assert.Equal(result.NewFolder, _library.DefaultBuild);
        Assert.True(_library.GetState(result.NewFolder).ReplaceOnNewBuild);
        Assert.Null(_library.Current.Find(old));
        Assert.Single(_library.Current.Builds);
        Assert.Equal("prefs", File.ReadAllText(Path.Combine(result.NewFolder, "portable", "config", "userpref.blend")));
    }

    [Fact]
    public async Task Waits_for_running_processes_before_removing_the_old_build()
    {
        var old = await InstallOldAsync();
        var running = _launches.Launch(_library.Current.Find(old)!.Build!);
        await using var replace = Create();

        var results = await replace.RunAsync([Download("bbbb", T2)], Ct);

        Assert.Single(results);
        Assert.Contains(old, replace.PendingRemovals);
        await Task.Delay(50, Ct);
        Assert.True(Directory.Exists(old));

        // A second run while the removal waits does not pick the old build again.
        Assert.Empty(await replace.RunAsync([Download("cccc", T2)], Ct));

        _launcher.Processes[running.ProcessId].Exit();
        await replace.WhenIdleAsync();
        Assert.False(Directory.Exists(old));
        Assert.Empty(replace.PendingRemovals);
        Assert.Equal(1, _installs);
    }

    [Fact]
    public async Task Retries_a_removal_while_files_are_in_use()
    {
        var old = await InstallOldAsync();
        var locked = new FileStream(Path.Combine(old, "blender.exe"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        await using var replace = Create(attempts: 1000);

        await replace.RunAsync([Download("bbbb", T2)], Ct);
        await Task.Delay(30, Ct);
        Assert.True(Directory.Exists(old));
        await locked.DisposeAsync();

        await replace.WhenIdleAsync();
        Assert.False(Directory.Exists(old));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Skips_frozen_and_unflagged_builds(bool frozen, bool flag)
    {
        var old = await InstallOldAsync(b => b with { IsFrozen = frozen }, flag);
        await using var replace = Create();

        Assert.Empty(await replace.RunAsync([Download("bbbb", T2)], Ct));
        Assert.Equal(0, _installs);
        Assert.True(Directory.Exists(old));
    }

    [Fact]
    public async Task No_newer_build_installs_nothing()
    {
        await InstallOldAsync();
        await using var replace = Create();

        Assert.Empty(await replace.RunAsync([Download("aaaa", T1), Download("zzzz", T1)], Ct));
        Assert.Equal(0, _installs);
    }

    [Fact]
    public async Task A_failed_install_keeps_the_old_build_and_its_flag()
    {
        var old = await InstallOldAsync();
        await using var replace = Create((_, _) => throw new PipelineException(PipelineErrorKind.Network, "offline"));

        Assert.Empty(await replace.RunAsync([Download("bbbb", T2)], Ct));
        Assert.True(Directory.Exists(old));
        Assert.True(_library.GetState(old).ReplaceOnNewBuild);
        Assert.Empty(replace.PendingRemovals);
    }

    [Fact]
    public async Task Dispose_drops_a_waiting_removal()
    {
        var old = await InstallOldAsync();
        _launches.Launch(_library.Current.Find(old)!.Build!);
        var replace = Create();
        await replace.RunAsync([Download("bbbb", T2)], Ct);

        await replace.DisposeAsync();

        Assert.True(Directory.Exists(old));
        Assert.Empty(replace.PendingRemovals);
    }
}
