using BlenderLauncher.Core.Library;

namespace BlenderLauncher.Core.Tests.Library;

public class LibraryStateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T1 = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task State_is_keyed_by_library_root_then_relative_folder()
    {
        using var temp = new TempDir();
        var libA = temp.Folder("A");
        var libB = temp.Folder("B");
        await using (var state = await LibraryState.LoadAsync(temp.Combine("state"), ct: Ct))
        {
            state.Update(libA, Path.Combine(libA, "daily", "b1"), s => s with { SizeBytes = 10, ReplaceOnNewBuild = true });
            state.Update(libB, "daily/b1", s => s with { SizeBytes = 20 });
            state.SetDefaultBuild(libA, Path.Combine(libA, "daily", "b1"));
        }

        await using var reloaded = await LibraryState.LoadAsync(temp.Combine("state"), ct: Ct);
        Assert.Equal(10, reloaded.Get(libA, "daily/b1").SizeBytes);
        Assert.True(reloaded.Get(libA, @"daily\b1").ReplaceOnNewBuild);
        Assert.Equal(20, reloaded.Get(libB, Path.Combine(libB, "daily", "b1")).SizeBytes);
        Assert.False(reloaded.Get(libB, "daily/b1").ReplaceOnNewBuild);
        Assert.Equal(Path.Combine(libA, "daily", "b1"), reloaded.GetDefaultBuild(libA));
        Assert.Null(reloaded.GetDefaultBuild(libB));

        var json = await File.ReadAllTextAsync(reloaded.FilePath, Ct);
        Assert.Contains("\"daily/b1\"", json);
        Assert.Contains("\"schemaVersion\": 1", json);
    }

    [Fact]
    public async Task Roots_compare_case_insensitively_and_ignore_trailing_separator_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDir();
        await using var state = await LibraryState.LoadAsync(temp.Path, ct: Ct);
        var lib = temp.Folder("Lib");

        state.Update(lib, "daily/Build", s => s with { PythonVersion = "3.11.9" });

        Assert.Equal("3.11.9", state.Get(lib.ToUpperInvariant() + "\\", "DAILY/build").PythonVersion);
    }

    [Fact]
    public async Task Saves_are_debounced_and_flush_writes()
    {
        using var temp = new TempDir();
        await using var state = await LibraryState.LoadAsync(temp.Path, ct: Ct);
        var lib = temp.Folder("Lib");

        state.Update(lib, "daily/a", s => s with { LastUsed = T1 });
        Assert.False(File.Exists(state.FilePath)); // not yet: the debounce delay has not passed

        await state.FlushAsync(Ct);
        Assert.True(File.Exists(state.FilePath));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public async Task Debounced_save_happens_after_the_delay()
    {
        using var temp = new TempDir();
        var state = await LibraryState.LoadAsync(temp.Path, ct: Ct);
        await using (state)
        {
            var lib = temp.Folder("Lib");
            state.Update(lib, "daily/a", s => s with { LastUsed = T1 });

            for (var i = 0; i < 100 && !File.Exists(state.FilePath); i++)
            {
                await Task.Delay(50, Ct);
            }

            Assert.True(File.Exists(state.FilePath));
        }
    }

    [Fact]
    public async Task Remove_clears_build_and_default_pointer()
    {
        using var temp = new TempDir();
        await using var state = await LibraryState.LoadAsync(temp.Path, ct: Ct);
        var lib = temp.Folder("Lib");
        state.Update(lib, "daily/a", s => s with { SizeBytes = 1 });
        state.SetDefaultBuild(lib, "daily/a");
        var events = new List<LibraryStateChangedEventArgs>();
        state.Changed += (_, e) => events.Add(e);

        state.Remove(lib, "daily/a");

        Assert.Equal(BuildState.Empty, state.Get(lib, "daily/a"));
        Assert.Null(state.GetDefaultBuild(lib));
        Assert.Single(events);
    }

    [Fact]
    public async Task Move_carries_state_and_default()
    {
        using var temp = new TempDir();
        await using var state = await LibraryState.LoadAsync(temp.Path, ct: Ct);
        var lib = temp.Folder("Lib");
        state.Update(lib, "daily/old", s => s with { ReplaceOnNewBuild = true });
        state.SetDefaultBuild(lib, "daily/old");

        state.Move(lib, "daily/old", "daily/new");

        Assert.True(state.Get(lib, "daily/new").ReplaceOnNewBuild);
        Assert.False(state.Get(lib, "daily/old").ReplaceOnNewBuild);
        Assert.Equal(Path.Combine(lib, "daily", "new"), state.GetDefaultBuild(lib));
    }

    [Fact]
    public async Task Installed_on_falls_back_to_folder_creation_time()
    {
        using var temp = new TempDir();
        await using var state = await LibraryState.LoadAsync(temp.Path, ct: Ct);
        var lib = temp.Folder("Lib");
        var build = temp.Folder("Lib", "daily", "a");
        var created = Directory.GetCreationTimeUtc(build);

        Assert.Equal(created, state.GetInstalledOn(lib, "daily/a")!.Value.UtcDateTime);
        Assert.Null(state.GetInstalledOn(lib, "daily/missing"));

        state.Update(lib, "daily/a", s => s with { InstalledOn = T1 });
        Assert.Equal(T1, state.GetInstalledOn(lib, "daily/a"));
    }

    [Fact]
    public async Task Corrupt_file_loads_empty()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, LibraryState.FileName), "{ nope");

        await using var state = await LibraryState.LoadAsync(temp.Path, ct: Ct);

        Assert.Empty(state.FoldersOf(temp.Path));
    }

    [Fact]
    public async Task Unchanged_update_does_not_raise()
    {
        using var temp = new TempDir();
        await using var state = await LibraryState.LoadAsync(temp.Path, ct: Ct);
        var raised = 0;
        state.Changed += (_, _) => raised++;

        state.Update(temp.Path, "daily/a", s => s);

        Assert.Equal(0, raised);
        Assert.Empty(state.FoldersOf(temp.Path));
    }
}
