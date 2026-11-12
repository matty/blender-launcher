using BlenderLauncher.Core.Library;

namespace BlenderLauncher.Core.Tests.Library;

public class LibraryScannerTests
{
    private static async Task<List<LibraryEntry>> ScanAsync(string root, IBlenderVersionProbe probe)
    {
        var list = new List<LibraryEntry>();
        await foreach (var entry in LibraryScanner.ScanAsync(root, new BuildInfoReader(probe), ct: TestContext.Current.CancellationToken))
        {
            list.Add(entry);
        }

        return list;
    }

    [Fact]
    public async Task Classifies_recognised_unrecognised_and_damaged()
    {
        using var temp = new TempDir();
        var good = temp.Folder("daily", "blender-4.3.0-alpha+daily.ddc9f92777cd");
        await Samples.WriteBlinfoAsync(good, Samples.Info());
        temp.File("stable/blender-4.2.3-windows-x64/blender.exe");
        temp.File("bforartists/Bforartists-5.3.0/bforartists.exe");
        temp.File("custom/some-folder/readme.txt");
        temp.File("experimental/broken/.blinfo", "{ not json");
        temp.File("experimental/empty-entries/.blinfo", """{"file_version": "1.5", "blinfo": []}""");

        var probe = new FakeProbe(exe => exe.EndsWith("bforartists.exe", StringComparison.OrdinalIgnoreCase) ? Samples.Bforartists : Samples.Blender42Lts);
        var entries = await ScanAsync(temp.Path, probe);

        var byName = entries.ToDictionary(e => e.Name);
        Assert.Equal(6, entries.Count);
        Assert.Equal(LibraryEntryKind.Build, byName["blender-4.3.0-alpha+daily.ddc9f92777cd"].Kind);
        Assert.Equal(LibraryEntryKind.Build, byName["blender-4.2.3-windows-x64"].Kind);
        Assert.Equal("stable", byName["blender-4.2.3-windows-x64"].Build!.Branch);
        Assert.Equal(LibraryEntryKind.Build, byName["Bforartists-5.3.0"].Kind);
        Assert.Equal("bforartists", byName["Bforartists-5.3.0"].Build!.Branch);
        Assert.Equal(LibraryEntryKind.Unrecognised, byName["some-folder"].Kind);
        Assert.Equal(LibraryEntryKind.Damaged, byName["broken"].Kind);
        Assert.NotNull(byName["broken"].Error);
        Assert.Equal(LibraryEntryKind.Damaged, byName["empty-entries"].Kind);
        Assert.Equal("custom", byName["some-folder"].ParentFolder);
        Assert.Equal(2, probe.Calls.Count); // only the two builds without .blinfo ran -v
    }

    [Fact]
    public async Task Executable_that_fails_without_info_is_damaged()
    {
        using var temp = new TempDir();
        temp.File("daily/blender-crashes/blender.exe");

        var entries = await ScanAsync(temp.Path, FakeProbe.Failing());

        Assert.Equal(LibraryEntryKind.Damaged, Assert.Single(entries).Kind);
    }

    [Fact]
    public void Ignores_files_template_temp_and_nested_builds()
    {
        using var temp = new TempDir();
        temp.File("daily/stray.zip");
        temp.File("template/tpl/blender.exe");
        temp.File(".temp/blender-x/blender.exe");
        temp.File("lts/blender-4.2/blender.exe"); // lts is accepted but not scanned by default, as upstream
        temp.File("daily/wrapper/blender-4.3/blender.exe"); // a build one level too deep
        temp.File("blender-root/blender.exe");

        var scanned = LibraryScanner.Enumerate(temp.Path, ct: TestContext.Current.CancellationToken).ToList();

        var wrapper = Assert.Single(scanned);
        Assert.Equal(temp.Combine("daily", "wrapper"), wrapper.Path);
        Assert.False(wrapper.IsRecognised);

        var withLts = LibraryScanner.Enumerate(temp.Path, ["lts", "template"], TestContext.Current.CancellationToken).ToList();
        Assert.Equal("lts", Assert.Single(withLts).ParentFolder);
    }

    [Fact]
    public void Missing_branch_folders_are_skipped()
    {
        using var temp = new TempDir();
        Assert.Empty(LibraryScanner.Enumerate(temp.Path, ct: TestContext.Current.CancellationToken));
        Assert.Empty(LibraryScanner.Enumerate(temp.Combine("does-not-exist"), ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_stops_the_scan()
    {
        using var temp = new TempDir();
        temp.File("daily/a/blender.exe");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in LibraryScanner.ScanAsync(temp.Path, new BuildInfoReader(new FakeProbe(Samples.BlenderDaily)), ct: cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task Fifty_builds_with_blinfo_scan_quickly()
    {
        using var temp = new TempDir();
        for (var i = 0; i < 50; i++)
        {
            var folder = temp.Folder("daily", $"blender-4.3.{i}");
            await Samples.WriteBlinfoAsync(folder, Samples.Info($"4.3.{i}"));
        }

        var probe = new FakeProbe(Samples.BlenderDaily);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var entries = await ScanAsync(temp.Path, probe);
        watch.Stop();

        Assert.Equal(50, entries.Count(e => e.Kind == LibraryEntryKind.Build));
        Assert.Empty(probe.Calls);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");
    }
}
