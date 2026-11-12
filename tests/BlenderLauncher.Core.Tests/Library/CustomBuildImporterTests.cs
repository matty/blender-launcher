using BlenderLauncher.Core.Library;

namespace BlenderLauncher.Core.Tests.Library;

public class CustomBuildImporterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ListProgress : IProgress<CopyProgress>
    {
        public List<CopyProgress> Reports { get; } = [];

        public void Report(CopyProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task Copy_leaves_source_and_reports_progress()
    {
        using var temp = new TempDir();
        var lib = temp.Folder("Lib");
        temp.File("src/MyBuild/blender.exe", new string('b', 300));
        temp.File("src/MyBuild/4.2/scripts/x.py", "print()");
        var progress = new ListProgress();

        var target = await CustomBuildImporter.ImportAsync(temp.Combine("src", "MyBuild"), lib, CustomImportMode.Copy, progress, Ct);

        Assert.Equal(Path.Combine(lib, "custom", "MyBuild"), target);
        Assert.True(File.Exists(Path.Combine(target, "blender.exe")));
        Assert.True(File.Exists(Path.Combine(target, "4.2", "scripts", "x.py")));
        Assert.True(Directory.Exists(temp.Combine("src", "MyBuild")));
        var last = progress.Reports[^1];
        Assert.Equal(last.BytesTotal, last.BytesDone);
        Assert.Equal(2, last.FilesDone);
        Assert.Empty(Directory.GetDirectories(Path.Combine(lib, ".temp", "BlenderLauncherNet")));
    }

    [Fact]
    public async Task Name_collisions_get_a_number()
    {
        using var temp = new TempDir();
        var lib = temp.Folder("Lib");
        temp.Folder("Lib", "custom", "MyBuild");
        temp.File("Lib/custom/MyBuild (2)", "a file in the way");
        temp.File("src/MyBuild/blender.exe");

        var target = await CustomBuildImporter.ImportAsync(temp.Combine("src", "MyBuild"), lib, CustomImportMode.Copy, ct: Ct);

        Assert.Equal(Path.Combine(lib, "custom", "MyBuild (3)"), target);
    }

    [Fact]
    public async Task Move_on_same_volume_renames()
    {
        using var temp = new TempDir();
        var lib = temp.Folder("Lib");
        temp.File("src/MyBuild/blender.exe", "exe");

        var target = await CustomBuildImporter.ImportAsync(temp.Combine("src", "MyBuild"), lib, CustomImportMode.Move, ct: Ct);

        Assert.True(File.Exists(Path.Combine(target, "blender.exe")));
        Assert.False(Directory.Exists(temp.Combine("src", "MyBuild")));
    }

    [Fact]
    public async Task Cancelled_copy_leaves_nothing_behind()
    {
        using var temp = new TempDir();
        var lib = temp.Folder("Lib");
        temp.File("src/MyBuild/blender.exe", "exe");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CustomBuildImporter.ImportAsync(temp.Combine("src", "MyBuild"), lib, CustomImportMode.Copy, ct: cts.Token));

        Assert.False(Directory.Exists(Path.Combine(lib, "custom", "MyBuild")));
        var staging = Path.Combine(lib, ".temp", "BlenderLauncherNet");
        Assert.True(!Directory.Exists(staging) || Directory.GetFileSystemEntries(staging).Length == 0);
        Assert.True(Directory.Exists(temp.Combine("src", "MyBuild")));
    }

    [Fact]
    public async Task Rejects_sources_that_would_loop_or_are_already_custom()
    {
        using var temp = new TempDir();
        var lib = temp.Folder("Outer", "Lib");
        temp.Folder("Outer", "Lib", "custom", "Already");

        await Assert.ThrowsAsync<ArgumentException>(() => CustomBuildImporter.ImportAsync(temp.Combine("Outer"), lib, CustomImportMode.Copy, ct: Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => CustomBuildImporter.ImportAsync(Path.Combine(lib, "custom", "Already"), lib, CustomImportMode.Move, ct: Ct));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => CustomBuildImporter.ImportAsync(temp.Combine("nope"), lib, CustomImportMode.Copy, ct: Ct));
    }
}
