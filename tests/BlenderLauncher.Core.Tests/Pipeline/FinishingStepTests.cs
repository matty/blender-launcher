using BlenderLauncher.Core.Pipeline;
using BlenderLauncher.Core.PlatformServices;

namespace BlenderLauncher.Core.Tests.Pipeline;

/// <summary>UPBGE layout, Bforartists notes, template, rename and removal.</summary>
public sealed class FinishingStepTests : IDisposable
{
    private static readonly RetryPolicy Quick = new(10, TimeSpan.FromMilliseconds(50));
    private readonly TempDir _temp = new();
    private readonly FakeRecycleBin _bin = new();

    private string Write(string relative, string text = "x")
    {
        var path = _temp.Combine(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Upbge_bin_release_is_flattened_replacing_same_names()
    {
        Write("b/bin/Release/blender.exe", "new");
        Write("b/bin/Release/0.50/s.py");
        Write("b/blender.exe", "old");
        Write("b/0.50/old.py");

        Assert.Equal(UpbgeLayoutFixer.Change.FlattenedBinRelease, UpbgeLayoutFixer.Fix(_temp.Combine("b")));
        Assert.Equal("new", File.ReadAllText(_temp.Combine("b", "blender.exe")));
        Assert.True(File.Exists(_temp.Combine("b", "0.50", "s.py")));
        Assert.False(File.Exists(_temp.Combine("b", "0.50", "old.py")));
        Assert.False(Directory.Exists(_temp.Combine("b", "bin")));
    }

    [Fact]
    public void Upbge_single_nested_folder_is_lifted()
    {
        Write("b/upbge-0.36/blender.exe", "exe");
        Write("b/upbge-0.36/0.36/s.py");
        Write("b/stray.txt");

        Assert.Equal(UpbgeLayoutFixer.Change.LiftedSingleSubfolder, UpbgeLayoutFixer.Fix(_temp.Combine("b")));
        Assert.Equal("exe", File.ReadAllText(_temp.Combine("b", "blender.exe")));
        Assert.True(File.Exists(_temp.Combine("b", "0.36", "s.py")));
        Assert.False(Directory.Exists(_temp.Combine("b", "upbge-0.36")));
        Assert.False(File.Exists(_temp.Combine("b", "stray.txt"))); // upstream deletes the old root with its loose files
        Assert.False(Directory.Exists(_temp.Combine("b_temp")));
    }

    [Fact]
    public void Upbge_layout_is_left_alone_when_the_root_has_the_executable_or_several_folders()
    {
        Write("a/blender.exe");
        Write("a/sub/blender.exe");
        Assert.Equal(UpbgeLayoutFixer.Change.None, UpbgeLayoutFixer.Fix(_temp.Combine("a")));

        Write("c/one/blender.exe");
        Write("c/two/x.txt");
        Assert.Equal(UpbgeLayoutFixer.Change.None, UpbgeLayoutFixer.Fix(_temp.Combine("c")));

        Write("d/one/readme.txt");
        Assert.Equal(UpbgeLayoutFixer.Change.None, UpbgeLayoutFixer.Fix(_temp.Combine("d")));
    }

    [Fact]
    public void Bforartists_patch_notes_move_into_the_matching_build()
    {
        Directory.CreateDirectory(_temp.Combine("bforartists", "Bforartists-4.3.0-Windows"));
        Directory.CreateDirectory(_temp.Combine("bforartists", "Bforartists-4.2.1-Windows"));
        Write("bforartists/Patchnotes430.txt", "notes");
        Write("bforartists/Unrelated999.txt");

        Assert.Equal(1, BforartistsPatchNotes.Move(_temp.Combine("bforartists")));
        Assert.Equal("notes", File.ReadAllText(_temp.Combine("bforartists", "Bforartists-4.3.0-Windows", "Patchnotes430.txt")));
        Assert.True(File.Exists(_temp.Combine("bforartists", "Unrelated999.txt")));
    }

    [Fact]
    public void Template_merges_into_the_first_version_folder()
    {
        Write("template/config/startup.blend", "template");
        Write("template/scripts/addons/a.py", "addon");
        Write("build/4.2/config/userpref.blend", "keep");
        Write("build/4.2/config/startup.blend", "old");
        Directory.CreateDirectory(_temp.Combine("build", "5.0"));
        Directory.CreateDirectory(_temp.Combine("build", "datafiles"));

        var target = TemplateInstaller.Install(_temp.Combine("template"), _temp.Combine("build"));

        Assert.Equal(_temp.Combine("build", "4.2"), target);
        Assert.Equal("template", File.ReadAllText(_temp.Combine("build", "4.2", "config", "startup.blend")));
        Assert.Equal("keep", File.ReadAllText(_temp.Combine("build", "4.2", "config", "userpref.blend")));
        Assert.True(File.Exists(_temp.Combine("build", "4.2", "scripts", "addons", "a.py")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Combine("build", "5.0")));
    }

    [Fact]
    public void Missing_template_folder_is_not_created_and_a_build_without_version_folder_is_skipped()
    {
        Directory.CreateDirectory(_temp.Combine("build", "datafiles"));
        Assert.Null(TemplateInstaller.Install(_temp.Combine("template"), _temp.Combine("build")));
        Assert.False(Directory.Exists(_temp.Combine("template")));
    }

    [Fact]
    public void Missing_template_folder_copies_nothing_and_is_not_created()
    {
        Directory.CreateDirectory(_temp.Combine("build", "4.2"));
        Assert.Equal(_temp.Combine("build", "4.2"), TemplateInstaller.Install(_temp.Combine("template"), _temp.Combine("build")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Combine("build", "4.2")));
        Assert.False(Directory.Exists(_temp.Combine("template")));
    }

    [Theory]
    [InlineData("blender-4.3.0-alpha+Daily.DDC9F92777CD", "blender-4.3.0-alpha+daily.ddc9f92777cd")]
    [InlineData("blender-4.1.0-stable.abc", "blender-4.1.0-stable.abc")]
    [InlineData("blender-4.4.0-alpha+NPR Prototype.abc", "blender-4.4.0-alpha+npr-prototype.abc")]
    public void Rename_target_is_lower_case_with_hyphens(string name, string expected) =>
        Assert.Equal(expected, Renamer.NormaliseName(name));

    [Fact]
    public void Folder_name_comes_from_the_full_version()
    {
        var info = TestArchives.Build("x", "4.3.0-alpha", "daily") with { BuildHash = "ddc9f92777cd" };
        Assert.Equal("blender-4.3.0-alpha+daily.ddc9f92777cd", Renamer.FolderNameFor(info));
        Assert.Equal("blender-4.1.0-stable.abc", Renamer.FolderNameFor(TestArchives.Build("x", "4.1.0") with { BuildHash = "abc" }));
    }

    [Fact]
    public async Task Rename_replaces_an_existing_folder_through_the_bin()
    {
        Write("stable/blender-4.2.0-windows-x64/blender.exe", "new");
        Write("stable/blender-4.2.0-stable.abc/blender.exe", "old");

        var result = await new Renamer(_bin, Quick).RenameAsync(_temp.Combine("stable", "blender-4.2.0-windows-x64"), "blender-4.2.0-stable.ABC", TestContext.Current.CancellationToken);

        Assert.Equal(_temp.Combine("stable", "blender-4.2.0-stable.abc"), result.BuildFolder);
        Assert.True(result.ReplacedExisting);
        Assert.Equal([_temp.Combine("stable", "blender-4.2.0-stable.abc")], _bin.Binned);
        Assert.Equal("new", File.ReadAllText(Path.Combine(result.BuildFolder, "blender.exe")));
    }

    [Fact]
    public async Task Rename_fails_when_the_bin_refuses_and_nothing_moves()
    {
        Write("stable/src/blender.exe");
        Write("stable/blender-1.0.0-stable/blender.exe");
        var bin = new NoPermanentBin();
        var ex = await Assert.ThrowsAsync<PipelineException>(() => new Renamer(bin, Quick).RenameAsync(_temp.Combine("stable", "src"), "blender-1.0.0-stable", TestContext.Current.CancellationToken));
        Assert.Equal(PipelineErrorKind.RecycleRefused, ex.Kind);
        Assert.True(Directory.Exists(_temp.Combine("stable", "src")));
    }

    [Fact]
    public async Task Rename_waits_for_a_lock_to_clear()
    {
        var file = Write("stable/src/blender.exe");
        var handle = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            await handle.DisposeAsync();
        }, TestContext.Current.CancellationToken);

        var result = await new Renamer(_bin, Quick).RenameAsync(_temp.Combine("stable", "src"), "renamed", TestContext.Current.CancellationToken);
        await release;
        Assert.Equal(_temp.Combine("stable", "renamed"), result.BuildFolder);
        Assert.True(File.Exists(Path.Combine(result.BuildFolder, "blender.exe")));
    }

    [Fact]
    public async Task Rename_gives_up_after_the_retries()
    {
        var file = Write("stable/src/blender.exe");
        using var handle = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var ex = await Assert.ThrowsAsync<PipelineException>(() =>
            new Renamer(_bin, new RetryPolicy(3, TimeSpan.FromMilliseconds(10))).RenameAsync(_temp.Combine("stable", "src"), "renamed", TestContext.Current.CancellationToken));
        Assert.Equal(PipelineErrorKind.FileInUse, ex.Kind);
    }

    [Fact]
    public async Task Remove_of_a_missing_path_is_success()
    {
        var result = await new Remover(_bin, Quick).RemoveAsync(_temp.Combine("nope"), toRecycleBin: true, TestContext.Current.CancellationToken);
        Assert.Equal(RecycleOutcome.Missing, result.Outcome);
        Assert.True(result.IsGone);
        Assert.Equal(0, _bin.Calls);
    }

    [Fact]
    public async Task Remove_sends_to_the_bin_or_deletes_permanently()
    {
        Write("stable/a/blender.exe");
        Write("stable/b/blender.exe");
        var remover = new Remover(_bin, Quick);

        Assert.Equal(RecycleOutcome.Recycled, (await remover.RemoveAsync(_temp.Combine("stable", "a"), true, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal([_temp.Combine("stable", "a")], _bin.Binned);

        var readOnly = _temp.Combine("stable", "b", "blender.exe");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        Assert.Equal(RecycleOutcome.DeletedPermanently, (await remover.RemoveAsync(_temp.Combine("stable", "b"), false, TestContext.Current.CancellationToken)).Outcome);
        Assert.False(Directory.Exists(_temp.Combine("stable", "b")));
        Assert.Single(_bin.Binned);
    }

    [Fact]
    public async Task Remove_reports_a_refusal_without_deleting()
    {
        Write("stable/a/blender.exe");
        _bin.RefuseReason = "too big";
        var result = await new Remover(_bin, Quick).RemoveAsync(_temp.Combine("stable", "a"), true, TestContext.Current.CancellationToken);
        Assert.Equal(new RecycleResult(RecycleOutcome.NotRecycled, "too big"), result);
        Assert.True(Directory.Exists(_temp.Combine("stable", "a")));
    }

    [Fact]
    public async Task Remove_retries_on_lock()
    {
        Write("stable/a/blender.exe");
        _bin.LockFailures = 3;
        var result = await new Remover(_bin, Quick).RemoveAsync(_temp.Combine("stable", "a"), true, TestContext.Current.CancellationToken);
        Assert.Equal(RecycleOutcome.Recycled, result.Outcome);
        Assert.Equal(4, _bin.Calls);
    }

    [Fact]
    public async Task Remove_gives_up_after_the_retries()
    {
        Write("stable/a/blender.exe");
        _bin.LockFailures = 100;
        var ex = await Assert.ThrowsAsync<PipelineException>(() =>
            new Remover(_bin, new RetryPolicy(4, TimeSpan.FromMilliseconds(5))).RemoveAsync(_temp.Combine("stable", "a"), false, TestContext.Current.CancellationToken));
        Assert.Equal(PipelineErrorKind.FileInUse, ex.Kind);
        Assert.Equal(4, _bin.Calls);
    }

    [Fact]
    public async Task Purge_empties_only_the_ports_staging_folder()
    {
        Write(".temp/BlenderLauncherNet/x.zip.part");
        Write(".temp/BlenderLauncherNet/b.extract/top/a.txt");
        Write(".temp/upstream-download.zip");
        var remover = new Remover(_bin, Quick);

        Assert.True(await remover.PurgeStagingAsync(_temp.Staging, TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(_temp.Staging));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Staging));
        Assert.True(File.Exists(_temp.Combine(".temp", "upstream-download.zip")));
        await Assert.ThrowsAsync<ArgumentException>(() => remover.PurgeStagingAsync(_temp.Combine(".temp"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => remover.PurgeStagingAsync(_temp.Path, TestContext.Current.CancellationToken));
        Assert.True(await remover.PurgeStagingAsync(_temp.Combine("elsewhere", ".temp", "BlenderLauncherNet"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void File_retry_recognises_locks()
    {
        Assert.True(FileRetry.IsLock(new UnauthorizedAccessException()));
        Assert.True(FileRetry.IsLock(new IOException("x") { HResult = unchecked((int)0x80070020) }));
        Assert.True(FileRetry.IsLock(new IOException("x") { HResult = unchecked((int)0x80070021) }));
        Assert.False(FileRetry.IsLock(new IOException("x") { HResult = unchecked((int)0x80070070) }));
        Assert.False(FileRetry.IsLock(new FileNotFoundException()));
    }

    [Fact]
    public void Null_bin_never_recycles()
    {
        Write("a/x.txt");
        var bin = NullRecycleBin.Instance;
        Assert.False(bin.IsAvailable);
        Assert.Equal(RecycleOutcome.NotRecycled, bin.SendToBin(_temp.Combine("a"), false).Outcome);
        Assert.True(Directory.Exists(_temp.Combine("a")));
        Assert.Equal(RecycleOutcome.DeletedPermanently, bin.SendToBin(_temp.Combine("a"), true).Outcome);
        Assert.False(Directory.Exists(_temp.Combine("a")));
        Assert.Equal(RecycleOutcome.Missing, bin.SendToBin(_temp.Combine("a"), true).Outcome);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>A bin that cannot take anything and is never allowed to delete.</summary>
    private sealed class NoPermanentBin : IRecycleBin
    {
        public bool IsAvailable => true;

        public RecycleResult SendToBin(string path, bool allowPermanentDelete) => new(RecycleOutcome.NotRecycled, "declined");

        public void DeletePermanently(string path) => throw new InvalidOperationException();
    }
}
