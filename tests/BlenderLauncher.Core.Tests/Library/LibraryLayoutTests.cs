using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;

namespace BlenderLauncher.Core.Tests.Library;

public class LibraryLayoutTests
{
    [Fact]
    public void Staging_folder_is_inside_temp()
    {
        using var temp = new TempDir();
        Assert.Equal(Path.Combine(temp.Path, ".temp", "BlenderLauncherNet"), new LibraryLayout(temp.Path).StagingFolder);
    }

    [Fact]
    public void Validate_of_an_empty_folder_leaves_it_empty()
    {
        using var temp = new TempDir();

        var result = LibraryLayout.Validate(temp.Path, "C:\\", protectedRoots: []);

        Assert.True(result.IsValid);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public void Validate_accepts_writable_folder_and_probes_in_staging_only()
    {
        using var temp = new TempDir();
        var upstreamFile = temp.File(".temp/upstream-download.zip", "x");

        var result = LibraryLayout.Validate(temp.Path, "C:\\", protectedRoots: []);

        Assert.True(result.IsValid);
        Assert.Empty(result.Warnings);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, ".temp", "BlenderLauncherNet")));
        Assert.False(File.Exists(Path.Combine(temp.Path, ".temp", LibraryLayout.ProbeFileName)));
        Assert.True(File.Exists(upstreamFile));
    }

    [Fact]
    public void Validate_keeps_an_existing_staging_folder()
    {
        using var temp = new TempDir();
        var staging = temp.Folder(".temp", "BlenderLauncherNet");

        Assert.True(LibraryLayout.Validate(temp.Path, "C:\\", protectedRoots: []).IsValid);

        Assert.True(Directory.Exists(staging));
        Assert.False(File.Exists(Path.Combine(staging, LibraryLayout.ProbeFileName)));
    }

    [Fact]
    public void Validate_reports_missing_empty_and_file()
    {
        using var temp = new TempDir();
        var file = temp.File("not-a-folder.txt");

        Assert.Equal(LibraryFolderProblem.Empty, LibraryLayout.Validate("  ", temp.Path).Problem);
        Assert.Equal(LibraryFolderProblem.DoesNotExist, LibraryLayout.Validate(temp.Combine("missing"), temp.Path).Problem);
        Assert.Equal(LibraryFolderProblem.IsAFile, LibraryLayout.Validate(file, temp.Path).Problem);
        Assert.False(Directory.Exists(temp.Combine("missing")));
    }

    [Fact]
    public void Validate_resolves_relative_paths_against_base()
    {
        using var temp = new TempDir();
        temp.Folder("lib");

        var result = LibraryLayout.Validate("lib", temp.Path, protectedRoots: []);

        Assert.True(result.IsValid);
        Assert.Equal(temp.Combine("lib"), result.FullPath);
    }

    [Fact]
    public void Validate_warns_for_protected_location()
    {
        using var temp = new TempDir();
        var lib = temp.Folder("Program Files", "Library");

        var result = LibraryLayout.Validate(lib, temp.Path, protectedRoots: [temp.Combine("Program Files")]);

        Assert.True(result.IsValid);
        Assert.Contains(LibraryFolderWarning.ProtectedLocation, result.Warnings);
    }

    [Fact]
    public void Protected_root_match_is_by_whole_segment()
    {
        Assert.True(LibraryLayout.IsUnderAny(@"C:\Program Files\Lib", [@"C:\Program Files"]));
        Assert.True(LibraryLayout.IsUnderAny(@"C:\Program Files", [@"C:\Program Files\"]));
        Assert.False(LibraryLayout.IsUnderAny(@"C:\Program Files Extra\Lib", [@"C:\Program Files"]));
        Assert.True(LibraryLayout.IsUnderAny(@"c:\program files\lib", [@"C:\Program Files"]));
    }

    [Fact]
    public void Default_protected_roots_include_program_files_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var roots = LibraryLayout.DefaultProtectedRoots();
        Assert.Contains(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), roots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_warns_for_version_manager_root()
    {
        using var temp = new TempDir();
        temp.File("blender-2.90.0-windows64/blender.exe");

        var result = LibraryLayout.Validate(temp.Path, temp.Path, protectedRoots: []);

        Assert.True(result.IsValid);
        Assert.Contains(LibraryFolderWarning.BlenderVersionManagerRoot, result.Warnings);
    }

    [Fact]
    public void Builds_in_branch_folders_are_not_a_version_manager_root()
    {
        using var temp = new TempDir();
        temp.File("daily/blender-4.3/blender.exe");
        temp.File("stable/blender-4.2/blender.exe");

        Assert.False(LibraryLayout.LooksLikeVersionManagerRoot(temp.Path));
    }

    [Fact]
    public void Resolve_normalises_and_trims()
    {
        var baseDir = Path.GetTempPath();
        var resolved = LibraryLayout.Resolve(@"a\..\b\", baseDir);
        Assert.Equal(Path.Combine(Path.GetFullPath(baseDir), "b"), resolved);
        Assert.Equal(@"D:\Lib", LibraryLayout.Resolve(@"D:\Lib\", baseDir));
        Assert.Equal(@"D:\", LibraryLayout.NormaliseRoot(@"D:\"));
    }

    [Fact]
    public void Accepted_parents_follow_upstream()
    {
        Assert.True(LibraryLayout.IsAcceptedParent("lts"));
        Assert.True(LibraryLayout.IsAcceptedParent("custom"));
        Assert.False(LibraryLayout.IsAcceptedParent("template"));
        Assert.False(LibraryLayout.IsAcceptedParent(".temp"));
        Assert.False(LibraryLayout.IsAcceptedParent("Daily"));
        Assert.Equal(Channels.LibraryFolders.Take(7), LibraryLayout.BuildFolders);
    }
}
