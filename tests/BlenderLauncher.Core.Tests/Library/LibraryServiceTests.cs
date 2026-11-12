using System.Collections.Concurrent;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;

namespace BlenderLauncher.Core.Tests.Library;

public sealed class LibraryServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
    private readonly TempDir _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    private async Task<(LibraryService Service, LibraryState State, ConcurrentQueue<LibraryChangedEventArgs> Events)> CreateAsync(IBlenderVersionProbe? probe = null)
    {
        var state = await LibraryState.LoadAsync(_temp.Combine("state"), ct: Ct);
        var clock = new FixedClock(Now);
        var service = new LibraryService(new BuildInfoReader(probe ?? new FakeProbe(Samples.BlenderDaily), time: clock), state, time: clock);
        var events = new ConcurrentQueue<LibraryChangedEventArgs>();
        service.Changed += (_, e) => events.Enqueue(e);
        return (service, state, events);
    }

    private string Lib(string name = "Lib")
    {
        var root = _temp.Folder(name);
        return root;
    }

    [Fact]
    public async Task Scan_streams_entries_then_completes()
    {
        var lib = Lib();
        var folder = _temp.Folder("Lib", "daily", "a");
        await Samples.WriteBlinfoAsync(folder, Samples.Info());
        _temp.File("Lib/custom/unknown/readme.txt");
        var (service, state, events) = await CreateAsync();
        await using var _ = state;
        using var __ = service;

        await service.ScanAsync(lib, ct: Ct);

        var kinds = events.Select(e => e.Kind).ToList();
        Assert.Equal(LibraryChangeKind.Reset, kinds[0]);
        Assert.Equal(2, kinds.Count(k => k == LibraryChangeKind.Added));
        Assert.Equal(LibraryChangeKind.ScanCompleted, kinds[^1]);
        Assert.Equal(2, service.Current.Entries.Length);
        Assert.Equal(lib, service.LibraryRoot);
        Assert.Single(service.Current.Builds);
        Assert.False(service.IsScanning);
    }

    [Fact]
    public async Task Rescan_removes_folders_that_went_away_and_keeps_snapshots_immutable()
    {
        var lib = Lib();
        var a = _temp.Folder("Lib", "daily", "a");
        var b = _temp.Folder("Lib", "daily", "b");
        await Samples.WriteBlinfoAsync(a, Samples.Info());
        await Samples.WriteBlinfoAsync(b, Samples.Info("4.3.1"));
        var (service, state, events) = await CreateAsync();
        await using var _ = state;
        using var __ = service;
        await service.ScanAsync(lib, ct: Ct);
        var before = service.Current;

        Directory.Delete(b, recursive: true);
        await service.ScanAsync(lib, ct: Ct);

        Assert.Equal(2, before.Entries.Length);
        Assert.Single(service.Current.Entries);
        Assert.Contains(events, e => e.Kind == LibraryChangeKind.Removed && e.Path == b);
    }

    [Fact]
    public async Task Edits_persist_to_blinfo_and_raise_updates()
    {
        var lib = Lib();
        var folder = _temp.Folder("Lib", "daily", "a");
        await Samples.WriteBlinfoAsync(folder, Samples.Info());
        var (service, state, events) = await CreateAsync();
        await using var _ = state;
        using var __ = service;
        await service.ScanAsync(lib, ct: Ct);

        await service.SetFavouriteAsync(folder, true, Ct);
        await service.SetCustomNameAsync(folder, "My daily", Ct);
        await service.SetFrozenAsync(folder, true, Ct);

        var entry = (await BlinfoCodec.ReadFileAsync(folder, Ct)).Blinfo[0];
        Assert.True(entry.IsFavorite);
        Assert.Equal("My daily", entry.CustomName);
        Assert.True(entry.IsFrozen);
        var build = service.Current.Find(folder)!.Build!;
        Assert.True(build.IsFavorite && build.IsFrozen);
        Assert.Equal(3, events.Count(e => e.Kind == LibraryChangeKind.Updated));
    }

    [Fact]
    public async Task Edit_of_unreadable_entry_throws()
    {
        var lib = Lib();
        _temp.File("Lib/custom/unknown/readme.txt");
        var (service, state, _) = await CreateAsync();
        await using var s = state;
        using var __ = service;
        await service.ScanAsync(lib, ct: Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetFavouriteAsync(_temp.Combine("Lib", "custom", "unknown"), true, Ct));
    }

    [Fact]
    public async Task Add_stamps_installed_on_and_removed_forgets_state()
    {
        var lib = Lib();
        var (service, state, events) = await CreateAsync();
        await using var _ = state;
        using var __ = service;
        await service.ScanAsync(lib, ct: Ct);
        _temp.File("Lib/daily/new/blender.exe");
        var folder = _temp.Combine("Lib", "daily", "new");

        var entry = await service.AddAsync(folder, Samples.Info(), ct: Ct);
        service.SetDefaultBuild(folder);
        service.MarkLaunched(folder);

        Assert.Equal(LibraryEntryKind.Build, entry.Kind);
        Assert.Equal(Now, service.GetState(folder).InstalledOn);
        Assert.Equal(Now, service.GetState(folder).LastUsed);
        Assert.Equal(folder, service.DefaultBuild);
        Assert.Contains(events, e => e.Kind == LibraryChangeKind.StateChanged && e.Path == folder);

        Directory.Delete(folder, recursive: true);
        service.Removed(folder);

        Assert.Null(service.Current.Find(folder));
        Assert.Null(service.DefaultBuild);
        Assert.Equal(BuildState.Empty, service.GetState(folder));
        Assert.Contains(events, e => e.Kind == LibraryChangeKind.Removed && e.Path == folder);
    }

    [Fact]
    public async Task Initialise_turns_unrecognised_into_build_and_reload_custom_sees_it()
    {
        var lib = Lib();
        var folder = _temp.Folder("Lib", "custom", "fork");
        _temp.File("Lib/custom/fork/fork.exe");
        var (service, state, _) = await CreateAsync();
        await using var s = state;
        using var __ = service;
        await service.ScanAsync(lib, ct: Ct);
        Assert.Equal(LibraryEntryKind.Unrecognised, service.Current.Find(folder)!.Kind);

        var fields = Samples.Info("3.6.0", "fork", null) with { CustomExecutable = "fork.exe", CustomName = "Fork" };
        var entry = await service.InitialiseAsync(folder, fields, Ct);
        Assert.Equal(LibraryEntryKind.Build, entry.Kind);

        await service.ReloadCustomAsync(Ct);
        var reread = service.Current.Find(folder)!;
        Assert.Equal(LibraryEntryKind.Build, reread.Kind);
        Assert.Equal("Fork", reread.Build!.CustomName);
        Assert.Equal("fork.exe", reread.Build.CustomExecutable);
    }

    [Fact]
    public async Task Switching_library_resets_and_keeps_state_per_library()
    {
        var libA = Lib("A");
        var libB = Lib("B");
        var a = _temp.Folder("A", "daily", "x");
        var b = _temp.Folder("B", "daily", "x");
        await Samples.WriteBlinfoAsync(a, Samples.Info());
        await Samples.WriteBlinfoAsync(b, Samples.Info());
        var (service, state, events) = await CreateAsync();
        await using var _ = state;
        using var __ = service;

        await service.ScanAsync(libA, ct: Ct);
        service.UpdateState(a, st => st with { ReplaceOnNewBuild = true });
        await service.ScanAsync(libB, ct: Ct);

        Assert.Equal(libB, service.LibraryRoot);
        Assert.Equal(2, events.Count(e => e.Kind == LibraryChangeKind.Reset));
        Assert.False(service.GetState(b).ReplaceOnNewBuild);
        Assert.True(state.Get(libA, "daily/x").ReplaceOnNewBuild);
    }

    [Fact]
    public async Task Measure_fills_size_and_python_version()
    {
        var lib = Lib();
        var folder = _temp.Folder("Lib", "daily", "a");
        await Samples.WriteBlinfoAsync(folder, Samples.Info());
        _temp.File("Lib/daily/a/4.3/python/bin/python311.dll", new string('p', 100));
        var (service, state, _) = await CreateAsync();
        await using var s = state;
        using var __ = service;
        await service.ScanAsync(lib, ct: Ct);

        await service.MeasureAsync(ct: Ct);

        var st = service.GetState(folder);
        Assert.Equal("3.11", st.PythonVersion);
        Assert.Equal(100 + new FileInfo(Path.Combine(folder, ".blinfo")).Length, st.SizeBytes);
        Assert.Equal(Now, st.SizeMeasuredAt);
    }

    [Fact]
    public async Task Operations_before_scan_throw()
    {
        var (service, state, _) = await CreateAsync();
        await using var s = state;
        using var __ = service;

        Assert.Null(service.DefaultBuild);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReloadCustomAsync(Ct));
    }
}
