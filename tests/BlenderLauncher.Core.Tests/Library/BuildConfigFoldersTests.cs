using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Tests.Library;

public class BuildConfigFoldersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Newest first, as the API file lists them.
    private static readonly BlenderSeries[] Series =
    [
        new(5, 1, false), new(5, 0, false), new(4, 5, true), new(4, 4, false), new(4, 3, false), new(4, 2, true), new(4, 1, false),
    ];

    private static BuildInfo Build(string folder, string subversion, string branch) =>
        Samples.Info(subversion, branch) with { Link = folder };

    [Theory]
    [InlineData("4.2.0", "stable", "portable")]
    [InlineData("10.0.0", "daily", "portable")] // upstream's text comparison would say "config" here
    [InlineData("4.1.1", "stable", @"4.1\config")]
    [InlineData("2.79.0-b", "stable", @"2.79\config")]
    [InlineData("4.1.0", "bforartists", "portable")]
    [InlineData("4.0.0", "bforartists", @"4.0\config")]
    [InlineData("0.42.0", "upbge-stable", "portable")]
    [InlineData("0.36.1", "upbge-weekly", @"0.36\config")]
    public void Portable_path_follows_upstream_version_rules(string subversion, string branch, string expected)
    {
        var path = BuildConfigFolders.PortablePath(Build(@"C:\Lib\x\build", subversion, branch));
        Assert.Equal(Path.Combine(@"C:\Lib\x\build", expected), path);
    }

    [Fact]
    public async Task Toggle_creates_disables_and_restores_portable_folder()
    {
        using var temp = new TempDir();
        var folder = temp.Folder("stable", "blender-4.2.3");
        var build = Build(folder, "4.2.3", "stable");
        var portable = Path.Combine(folder, "portable");

        Assert.Equal(PortableState.None, BuildConfigFolders.GetPortableState(build));

        Assert.True(await BuildConfigFolders.TogglePortableAsync(build, Ct));
        Assert.True(Directory.Exists(portable));
        File.WriteAllText(Path.Combine(portable, "userpref.blend"), "prefs");
        Assert.Equal(PortableState.Portable, BuildConfigFolders.GetPortableState(build));

        Assert.False(await BuildConfigFolders.TogglePortableAsync(build, Ct));
        Assert.False(Directory.Exists(portable));
        Assert.True(File.Exists(Path.Combine(folder, "_portable", "userpref.blend")));
        Assert.Equal(PortableState.Disabled, BuildConfigFolders.GetPortableState(build));

        Assert.True(await BuildConfigFolders.TogglePortableAsync(build, Ct));
        Assert.True(File.Exists(Path.Combine(portable, "userpref.blend")));
        Assert.False(Directory.Exists(Path.Combine(folder, "_portable")));
    }

    [Fact]
    public async Task Old_config_form_uses_version_folder_and_needs_it_to_exist()
    {
        using var temp = new TempDir();
        var folder = temp.Folder("stable", "blender-3.6.5");
        var build = Build(folder, "3.6.5", "stable");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => BuildConfigFolders.TogglePortableAsync(build, Ct));

        Directory.CreateDirectory(Path.Combine(folder, "3.6"));
        Assert.True(await BuildConfigFolders.TogglePortableAsync(build, Ct));
        Assert.True(Directory.Exists(Path.Combine(folder, "3.6", "config")));
        Assert.False(await BuildConfigFolders.TogglePortableAsync(build, Ct));
        Assert.True(Directory.Exists(Path.Combine(folder, "3.6", "_config")));
    }

    [Theory]
    [InlineData("stable", @"Blender Foundation\blender")]
    [InlineData("bforartists", @"bforartists\bforartists")]
    [InlineData("upbge-stable", @"UPBGE\Blender")]
    public void General_config_folder_with_fork_mapping(string branch, string expected) =>
        Assert.Equal(Path.Combine(@"C:\AppData", expected), BuildConfigFolders.GeneralConfigFolder(branch, @"C:\AppData"));

    [Fact]
    public void Config_version_mapping_for_forks()
    {
        Assert.Equal("4.2", BuildConfigFolders.ConfigVersionOf(Build("x", "4.2.3", "lts"), Series));
        // Bforartists 4.4 uses the next Blender series up in the list.
        Assert.Equal("4.5", BuildConfigFolders.ConfigVersionOf(Build("x", "4.4.0", "bforartists"), Series));
        // Newest and oldest entries are excluded by upstream's bounds check.
        Assert.Equal("", BuildConfigFolders.ConfigVersionOf(Build("x", "5.1.0", "bforartists"), Series));
        Assert.Equal("", BuildConfigFolders.ConfigVersionOf(Build("x", "4.1.0", "bforartists"), Series));
        // UPBGE 0.45 is Blender 4.5; 0.50 is 5.0; an unknown series gives "".
        Assert.Equal("4.5", BuildConfigFolders.ConfigVersionOf(Build("x", "0.45.0", "upbge-stable"), Series));
        Assert.Equal("5.0", BuildConfigFolders.ConfigVersionOf(Build("x", "0.50.0-alpha", "upbge-weekly"), Series));
        Assert.Equal("", BuildConfigFolders.ConfigVersionOf(Build("x", "0.30.0", "upbge-stable"), Series));
        Assert.Null(BuildConfigFolders.UpbgeBlenderVersion(new SemVer(0, 450), Series)); // sliced as 45.0, as upstream
    }

    [Fact]
    public void Resolve_prefers_portable_unless_shift_and_falls_back_to_general()
    {
        using var temp = new TempDir();
        var appData = temp.Folder("AppData");
        var folder = temp.Folder("stable", "blender-4.2.3");
        var build = Build(folder, "4.2.3", "stable");

        var none = BuildConfigFolders.ResolveConfigFolder(build, appData, Series);
        Assert.Equal(ConfigFolderKind.GeneralFallback, none.Kind);
        Assert.Equal(Path.Combine(appData, "Blender Foundation", "blender"), none.Path);
        Assert.Equal(Path.Combine(appData, "Blender Foundation", "blender", "4.2"), none.MissingVersionPath);

        var version = temp.Folder("AppData", "Blender Foundation", "blender", "4.2");
        Assert.Equal(new ConfigFolderTarget(ConfigFolderKind.Version, version), BuildConfigFolders.ResolveConfigFolder(build, appData, Series));

        Directory.CreateDirectory(Path.Combine(folder, "portable"));
        Assert.Equal(ConfigFolderKind.Portable, BuildConfigFolders.ResolveConfigFolder(build, appData, Series).Kind);
        Assert.Equal(ConfigFolderKind.Version, BuildConfigFolders.ResolveConfigFolder(build, appData, Series, preferUserFolder: true).Kind);
    }

    [Fact]
    public async Task Copy_portable_settings_to_replacement_build()
    {
        using var temp = new TempDir();
        var oldFolder = temp.Folder("daily", "old");
        var newFolder = temp.Folder("daily", "new");
        temp.File("daily/old/portable/config/userpref.blend", "old prefs");
        temp.File("daily/old/portable/scripts/addons/a.py", "addon");
        temp.File("daily/new/portable/config/userpref.blend", "new default");
        var oldBuild = Build(oldFolder, "4.3.0-alpha", "daily");
        var newBuild = Build(newFolder, "4.3.0-alpha", "daily");

        Assert.Equal(PortableCopyResult.Copied, await BuildConfigFolders.CopyPortableSettingsAsync(oldBuild, newBuild, Ct));

        Assert.Equal("old prefs", File.ReadAllText(Path.Combine(newFolder, "portable", "config", "userpref.blend")));
        Assert.True(File.Exists(Path.Combine(newFolder, "portable", "scripts", "addons", "a.py")));
        Assert.True(Directory.Exists(Path.Combine(oldFolder, "portable")));
    }

    [Fact]
    public async Task Copy_portable_settings_reports_missing_parts()
    {
        using var temp = new TempDir();
        var oldBuild = Build(temp.Folder("stable", "old"), "3.6.5", "stable");
        var newBuild = Build(temp.Folder("stable", "new"), "3.6.8", "stable");

        Assert.Equal(PortableCopyResult.NoSettings, await BuildConfigFolders.CopyPortableSettingsAsync(oldBuild, newBuild, Ct));

        temp.Folder("stable", "old", "3.6", "config");
        Assert.Equal(PortableCopyResult.NoBuildFolder, await BuildConfigFolders.CopyPortableSettingsAsync(oldBuild, newBuild, Ct));
    }
}
