using System.Security.Cryptography;
using BlenderLauncher.Core.Pipeline;

namespace BlenderLauncher.Core.Tests.Pipeline;

public sealed class ExtractorTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly FakeRecycleBin _bin = new();

    private string Destination => _temp.Combine("stable");

    private Extractor NewExtractor() => new(_bin, new RetryPolicy(3, TimeSpan.FromMilliseconds(10)), progressInterval: TimeSpan.Zero);

    [Fact]
    public async Task Zip_with_one_top_folder_lands_in_the_channel_folder()
    {
        var archive = TestArchives.WriteZip(Path.Combine(_temp.Staging, "blender-4.2.0-windows-x64.zip"),
            ("blender-4.2.0-windows-x64/", ""),
            ("blender-4.2.0-windows-x64/blender.exe", "exe"),
            ("blender-4.2.0-windows-x64/4.2/scripts/startup.py", "print(1)"),
            ("notes.txt", "loose"));
        var reports = new List<TransferProgress>();

        var result = await NewExtractor().ExtractAsync(new ExtractRequest(archive, _temp.Staging), Destination, isUpbge: false,
            new SyncProgress<TransferProgress>(reports.Add), CancellationToken.None);

        Assert.Equal(Path.Combine(Destination, "blender-4.2.0-windows-x64"), result.BuildFolder);
        Assert.False(result.ReplacedExisting);
        Assert.Equal("exe", File.ReadAllText(Path.Combine(result.BuildFolder, "blender.exe")));
        Assert.True(File.Exists(Path.Combine(result.BuildFolder, "4.2", "scripts", "startup.py")));
        Assert.Equal("loose", File.ReadAllText(Path.Combine(Destination, "notes.txt")));
        Assert.True(File.Exists(archive)); // the install service deletes it at the end
        Assert.Equal([Path.GetFileName(archive)], Directory.EnumerateFileSystemEntries(_temp.Staging).Select(Path.GetFileName));
        Assert.True(reports[0].IsIndeterminate);
        Assert.Equal(new TransferProgress(16, 16), reports[^1]);
    }

    [Fact]
    public async Task Zip_without_a_single_top_folder_is_wrapped_in_the_archive_name()
    {
        var archive = TestArchives.WriteZip(Path.Combine(_temp.Staging, "flat-build.zip"),
            ("blender.exe", "exe"), ("a/x.txt", "x"), ("b/y.txt", "y"));
        var result = await NewExtractor().ExtractAsync(new ExtractRequest(archive, _temp.Staging), Destination, false, null, CancellationToken.None);

        Assert.Equal(Path.Combine(Destination, "flat-build"), result.BuildFolder);
        Assert.True(File.Exists(Path.Combine(result.BuildFolder, "blender.exe")));
        Assert.True(File.Exists(Path.Combine(result.BuildFolder, "b", "y.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(Destination));
    }

    [Fact]
    public async Task Existing_target_folder_goes_to_the_bin_first()
    {
        var old = Path.Combine(Destination, "blender-4.2.0-windows-x64");
        Directory.CreateDirectory(old);
        await File.WriteAllTextAsync(Path.Combine(old, "stale.txt"), "old", TestContext.Current.CancellationToken);
        var archive = TestArchives.WriteZip(Path.Combine(_temp.Staging, "blender-4.2.0-windows-x64.zip"), ("blender-4.2.0-windows-x64/blender.exe", "new"));

        var result = await NewExtractor().ExtractAsync(new ExtractRequest(archive, _temp.Staging), Destination, false, null, CancellationToken.None);

        Assert.True(result.ReplacedExisting);
        Assert.Equal([old], _bin.Binned);
        Assert.False(File.Exists(Path.Combine(old, "stale.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(old, "blender.exe")));
    }

    [Theory]
    [InlineData("zip-slip-parent.zip")]
    [InlineData("zip-slip-leading-parent.zip")]
    [InlineData("zip-slip-absolute.zip")]
    [InlineData("zip-slip-drive.zip")]
    public async Task Zip_slip_is_rejected_and_the_archive_deleted(string fixture)
    {
        Directory.CreateDirectory(_temp.Staging);
        var archive = Path.Combine(_temp.Staging, fixture);
        File.Copy(Fixtures.PathOf(Path.Combine("Pipeline", fixture)), archive);

        var ex = await Assert.ThrowsAsync<PipelineException>(() =>
            NewExtractor().ExtractAsync(new ExtractRequest(archive, _temp.Staging), Destination, false, null, CancellationToken.None));

        Assert.Equal(PipelineErrorKind.UnsafeArchive, ex.Kind);
        Assert.False(File.Exists(archive));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Staging));
        Assert.False(Directory.Exists(Destination));
        Assert.Empty(Directory.EnumerateFiles(_temp.Path, "evil.txt", SearchOption.AllDirectories));
        Assert.False(File.Exists(@"C:\evil.txt") && File.GetLastWriteTimeUtc(@"C:\evil.txt") > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Backslash_traversal_written_by_dotnet_is_rejected()
    {
        var archive = TestArchives.WriteZip(Path.Combine(_temp.Staging, "bs.zip"), ("top/a.txt", "a"), (@"top\..\..\evil.txt", "x"));
        var ex = await Assert.ThrowsAsync<PipelineException>(() =>
            NewExtractor().ExtractAsync(new ExtractRequest(archive, _temp.Staging), Destination, false, null, CancellationToken.None));
        Assert.Equal(PipelineErrorKind.UnsafeArchive, ex.Kind);
    }

    [Theory]
    [InlineData("a/b.txt", true)]
    [InlineData("./a/b.txt", true)]
    [InlineData("a..b/c", true)]
    [InlineData("../a", false)]
    [InlineData("a/../../b", false)]
    [InlineData("a/../b", false)]
    [InlineData(@"a\..\..\b", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData(@"\\server\share\x", false)]
    [InlineData("C:/Windows/x", false)]
    [InlineData("C:x", false)]
    [InlineData("a/file.txt:stream", false)]
    [InlineData("a/.../b", false)]
    [InlineData("a/ . /b", false)]
    [InlineData("", false)]
    public void Entry_paths_are_checked(string name, bool safe)
    {
        var root = _temp.Combine("root");
        if (safe)
        {
            Assert.StartsWith(root + Path.DirectorySeparatorChar, ArchivePaths.Resolve(root, name), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal(PipelineErrorKind.UnsafeArchive, Assert.Throws<PipelineException>(() => ArchivePaths.Resolve(root, name)).Kind);
        }
    }

    [Theory]
    [InlineData("blender-4.2/,blender-4.2/blender.exe,blender-4.2/4.2/x", "blender-4.2")]
    [InlineData("blender-4.2/blender.exe,readme.txt", "blender-4.2")]
    [InlineData("a/x,b/y", null)]
    [InlineData("blender.exe,readme.txt", null)]
    [InlineData(@"top\a,top\b", "top")]
    [InlineData("bin/Release/blender.exe", "bin")]
    public void Top_folder_detection_matches_upstream(string names, string? expected) =>
        Assert.Equal(expected, ArchivePaths.FindTopFolder(names.Split(',')));

    [Fact]
    public void Upbge_flat_layout_is_detected()
    {
        string[] names = ["bin/Release/blender.exe", "bin/Release/4.2/x"];
        Assert.True(ArchivePaths.IsUpbgeFlat(ArchivePaths.FindTopFolder(names), names));
        Assert.False(ArchivePaths.IsUpbgeFlat("bin", ["bin/Debug/x"]));
    }

    [Fact]
    public async Task Upbge_flat_zip_is_extracted_under_the_archive_name_and_flattened()
    {
        var archive = TestArchives.WriteZip(Path.Combine(_temp.Staging, "upbge-0.50-windows-x64.zip"),
            ("bin/Release/blender.exe", "exe"), ("bin/Release/0.50/scripts/s.py", "s"));
        var result = await NewExtractor().ExtractAsync(new ExtractRequest(archive, _temp.Staging), _temp.Combine("upbge-stable"), isUpbge: true,
            null, CancellationToken.None);

        Assert.Equal(_temp.Combine("upbge-stable", "upbge-0.50-windows-x64"), result.BuildFolder);
        Assert.True(File.Exists(Path.Combine(result.BuildFolder, "blender.exe")));
        Assert.True(File.Exists(Path.Combine(result.BuildFolder, "0.50", "scripts", "s.py")));
        Assert.False(Directory.Exists(Path.Combine(result.BuildFolder, "bin")));
    }

    [Theory]
    [InlineData("upbge-bcj2-sample.7z")]
    [InlineData("upbge-bcj-sample.7z")]
    public async Task Seven_zip_with_x86_filters_extracts_and_is_flattened(string fixture)
    {
        Directory.CreateDirectory(_temp.Staging);
        var archive = Path.Combine(_temp.Staging, fixture);
        File.Copy(Fixtures.PathOf(Path.Combine("Pipeline", fixture)), archive);
        var reports = new List<TransferProgress>();

        var result = await NewExtractor().ExtractAsync(new ExtractRequest(archive, _temp.Staging), _temp.Combine("upbge-stable"), isUpbge: true,
            new SyncProgress<TransferProgress>(reports.Add), CancellationToken.None);

        Assert.Equal(_temp.Combine("upbge-stable", Path.GetFileNameWithoutExtension(fixture)), result.BuildFolder);
        var exe = Path.Combine(result.BuildFolder, "blender.exe");
        Assert.Equal("4c67b1700d03c10cacafa514e6e146f3240f3788cb3253b3b612539c635a7083",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(exe))));
        Assert.Equal(800, new FileInfo(Path.Combine(result.BuildFolder, "0.50", "scripts", "startup.py")).Length);
        Assert.Equal(new TransferProgress(11351, 11351), reports[^1]);
    }

    [Fact]
    public async Task Solid_7z_fixture_extracts_through_the_extractor()
    {
        Directory.CreateDirectory(_temp.Staging);
        var archive = Path.Combine(_temp.Staging, "solid-sample.7z");
        File.Copy(Fixtures.PathOf("solid-sample.7z"), archive);
        var result = await NewExtractor().ExtractAsync(new ExtractRequest(archive, _temp.Staging), Destination, false, null, CancellationToken.None);
        Assert.Equal(Path.Combine(Destination, "upbge-sample"), result.BuildFolder);
        Assert.Equal(1000, new FileInfo(Path.Combine(result.BuildFolder, "blender.exe")).Length);
    }

    [Theory]
    [InlineData("broken.zip")]
    [InlineData("broken.7z")]
    public async Task Corrupt_archive_is_deleted(string name)
    {
        Directory.CreateDirectory(_temp.Staging);
        var archive = Path.Combine(_temp.Staging, name);
        await File.WriteAllBytesAsync(archive, Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray(), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<PipelineException>(() =>
            NewExtractor().ExtractAsync(new ExtractRequest(archive, _temp.Staging), Destination, false, null, CancellationToken.None));
        Assert.True(ex.Kind is PipelineErrorKind.CorruptArchive or PipelineErrorKind.UnsupportedArchive, ex.ToString());
        Assert.False(File.Exists(archive));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Staging));
    }

    [Fact]
    public async Task Truncated_zip_is_deleted()
    {
        var bytes = TestArchives.Zip(("top/a.txt", new string('a', 10000)), ("top/b.txt", new string('b', 10000)));
        Directory.CreateDirectory(_temp.Staging);
        var archive = Path.Combine(_temp.Staging, "cut.zip");
        await File.WriteAllBytesAsync(archive, bytes[..(bytes.Length / 2)], TestContext.Current.CancellationToken);
        var ex = await Assert.ThrowsAsync<PipelineException>(() =>
            NewExtractor().ExtractAsync(new ExtractRequest(archive, _temp.Staging), Destination, false, null, CancellationToken.None));
        Assert.Equal(PipelineErrorKind.CorruptArchive, ex.Kind);
        Assert.False(File.Exists(archive));
    }

    [Fact]
    public async Task Unsupported_format_is_refused()
    {
        Directory.CreateDirectory(_temp.Staging);
        var archive = Path.Combine(_temp.Staging, "blender-4.2.0-linux-x64.tar.xz");
        await File.WriteAllTextAsync(archive, "x", TestContext.Current.CancellationToken);
        var ex = await Assert.ThrowsAsync<PipelineException>(() =>
            NewExtractor().ExtractAsync(new ExtractRequest(archive, _temp.Staging), Destination, false, null, CancellationToken.None));
        Assert.Equal(PipelineErrorKind.UnsupportedArchive, ex.Kind);
    }

    [Fact]
    public async Task Missing_archive_reports_vanished_staging()
    {
        var ex = await Assert.ThrowsAsync<PipelineException>(() => NewExtractor().ExtractAsync(
            new ExtractRequest(Path.Combine(_temp.Staging, "gone.zip"), _temp.Staging), Destination, false, null, CancellationToken.None));
        Assert.Equal(PipelineErrorKind.StagingVanished, ex.Kind);
        Assert.Contains("cleared by another program", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staged_build_that_vanished_before_commit_is_reported()
    {
        var archive = TestArchives.WriteZip(Path.Combine(_temp.Staging, "b.zip"), ("top/a.txt", "a"));
        var extractor = NewExtractor();
        var staged = await extractor.ExtractToStagingAsync(new ExtractRequest(archive, _temp.Staging), null, CancellationToken.None);
        PermanentDeleteFolder(_temp.Combine(".temp"));

        var ex = await Assert.ThrowsAsync<PipelineException>(() => extractor.CommitAsync(staged, Destination, CancellationToken.None));
        Assert.Equal(PipelineErrorKind.StagingVanished, ex.Kind);
    }

    [Fact]
    public async Task Cancel_removes_the_extraction_folder_and_keeps_the_archive()
    {
        var entries = Enumerable.Range(0, 200).Select(i => ($"top/f{i}.txt", new string('x', 1000))).ToArray();
        var archive = TestArchives.WriteZip(Path.Combine(_temp.Staging, "many.zip"), entries);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<TransferProgress>(p =>
        {
            if (p.Done > 5000)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewExtractor().ExtractToStagingAsync(new ExtractRequest(archive, _temp.Staging), progress, cts.Token));
        Assert.Equal([archive], Directory.EnumerateFileSystemEntries(_temp.Staging));
    }

    private static void PermanentDeleteFolder(string path) => BlenderLauncher.Core.PlatformServices.PermanentDelete.Run(path);

    public void Dispose() => _temp.Dispose();
}
