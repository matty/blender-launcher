using BlenderLauncher.Core.Updates;

namespace BlenderLauncher.Core.Tests.Updates;

public class ReleaseAssetsTests
{
    private const string Repo = "owner/blender-launcher";

    // ---- Ported from upstream tests/backend/test_self_update.py (Windows rows only) ----

    /// <summary>
    /// <c>test_get_launcher_name_per_platform</c>, win32 row. Upstream's second name ("Blender Launcher Updater.exe")
    /// has no counterpart: Velopack's Update.exe applies updates.
    /// </summary>
    [Fact]
    public void Launcher_name_on_windows() => Assert.Equal("Blender Launcher.exe", AppIdentity.ExecutableName);

    // (test_release_asset_url_* are not ported: Velopack's feed, not a fixed zip URL, says what to download.)

    // ---- Own ----

    [Fact]
    public void Download_names_follow_upstreams_stem()
    {
        Assert.Equal("Blender_Launcher_v1.0.0_Windows_x64_Setup.exe", ReleaseAssets.SetupName("v1.0.0"));
        Assert.Equal("Blender_Launcher_v1.0.0-rc.1_Windows_x64_Portable.zip", ReleaseAssets.PortableZipName("v1.0.0-rc.1"));
        Assert.Equal("https://github.com/owner/blender-launcher/releases/tag/v1.0.0", ReleaseAssets.ReleasePageUrl(Repo, "v1.0.0").AbsoluteUri);
        Assert.Equal("https://github.com/owner/blender-launcher", ReleaseAssets.RepositoryUrl(Repo));
    }

    [Fact]
    public void The_portable_zip_counts_as_a_windows_zip_for_the_update_check()
    {
        // AppReleaseService's pre-release channel looks for a ".zip" asset whose name contains "windows".
        var name = ReleaseAssets.PortableZipName("v2.8.2");
        Assert.EndsWith(".zip", name, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("windows", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_velopack_install_folder_holds_the_data_in_a_subfolder_and_is_not_upstreams()
    {
        // Velopack installs to %LOCALAPPDATA%\<pack id>, replaces its "current" folder on update and deletes it all on
        // uninstall. The data lives in its "data" subfolder, so settings.json is never beside the stub (portable mode).
        Assert.Equal(AppIdentity.DataFolderName, ReleaseAssets.VelopackPackId);
        Assert.Equal(Path.Combine(@"C:\L", ReleaseAssets.VelopackPackId, "data"), DataDirectories.DefaultRoot(@"C:\L"));
        Assert.NotEqual("current", AppIdentity.DataSubfolderName, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual("packages", AppIdentity.DataSubfolderName, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(BlenderLauncher.Core.Settings.Upstream.UpstreamPaths.FolderName, ReleaseAssets.VelopackPackId, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("v1/../x")]
    [InlineData(@"v1\x")]
    public void Unusable_tags_are_rejected(string tag) => Assert.ThrowsAny<ArgumentException>(() => ReleaseAssets.SetupName(tag));

    [Theory]
    [InlineData("Victor-IX/Blender-Launcher-V2", true)]
    [InlineData("a/b.c_d-e", true)]
    [InlineData("owner", false)]
    [InlineData("a/b/c", false)]
    [InlineData("-owner/name", false)]
    [InlineData(null, false)]
    public void Repository_names(string? repo, bool valid) => Assert.Equal(valid, ReleaseAssets.IsValidRepo(repo));
}
