using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.Core.Tests.Settings;

public class DataDirectoriesAndStateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Resolves_to_local_app_data_without_a_portable_settings_file()
    {
        using var exe = new TempFolder();
        using var local = new TempFolder();
        var d = DataDirectories.Resolve(exe.Path, local.Path);
        Assert.False(d.IsPortable);
        Assert.Equal(Path.Combine(local.Path, "BlenderLauncherNet", "data"), d.Root);
        Assert.Equal(Path.Combine(local.Path, "BlenderLauncherNet", "data", "settings.json"), d.SettingsFile);
        Assert.Equal(Path.Combine(d.Root, "state"), d.State);
        Assert.False(Directory.Exists(d.Root));
    }

    [Fact]
    public void The_installed_folder_is_never_portable_and_its_old_layout_moves_into_data()
    {
        // The old layout kept the data directly in %LOCALAPPDATA%\BlenderLauncherNet, which is now Velopack's install root.
        using var local = new TempFolder();
        var app = Path.Combine(local.Path, "BlenderLauncherNet");
        Directory.CreateDirectory(Path.Combine(app, "logs"));
        Directory.CreateDirectory(Path.Combine(app, "current"));
        File.WriteAllText(Path.Combine(app, "settings.json"), "{\"a\":1}");
        File.WriteAllText(Path.Combine(app, "logs", "x.log"), "log");
        File.WriteAllText(Path.Combine(app, "Update.exe"), "");

        var d = DataDirectories.Resolve(app, local.Path);

        Assert.False(d.IsPortable);
        Assert.Equal(Path.Combine(app, "data"), d.Root);
        Assert.Equal("{\"a\":1}", File.ReadAllText(d.SettingsFile));
        Assert.True(File.Exists(Path.Combine(d.Logs, "x.log")));
        Assert.False(File.Exists(Path.Combine(app, "settings.json")));
        Assert.True(File.Exists(Path.Combine(app, "Update.exe")));
        Assert.True(Directory.Exists(Path.Combine(app, "current")));

        // Only once: a settings file left in the old place later does not replace the moved one.
        File.WriteAllText(Path.Combine(app, "settings.json"), "{}");
        Assert.False(DataDirectories.MigrateLegacyLayout(local.Path));
        Assert.Equal("{\"a\":1}", File.ReadAllText(d.SettingsFile));
    }

    [Fact]
    public void A_library_anywhere_in_the_launchers_own_folder_is_refused()
    {
        using var local = new TempFolder();
        var app = Path.Combine(local.Path, "BlenderLauncherNet");
        Assert.True(BlenderLauncher.Core.Library.LibraryLayout.IsInsideAppFolder(Path.Combine(app, "Library"), app));
        Assert.True(BlenderLauncher.Core.Library.LibraryLayout.IsInsideAppFolder(Path.Combine(app, "data", "Library"), app));
        Assert.False(BlenderLauncher.Core.Library.LibraryLayout.IsInsideAppFolder(Path.Combine(local.Path, "Library"), app));
    }

    [Fact]
    public void Settings_beside_the_exe_mean_portable_mode()
    {
        using var exe = new TempFolder();
        using var local = new TempFolder();
        File.WriteAllText(Path.Combine(exe.Path, "settings.json"), "{}");
        var d = DataDirectories.Resolve(exe.Path, local.Path);
        Assert.True(d.IsPortable);
        Assert.Equal(Path.Combine(exe.Path, "data"), d.Root);
        Assert.Equal(Path.Combine(exe.Path, "settings.json"), d.SettingsFile);
        Assert.Equal(Path.Combine(exe.Path, "data", "cache"), d.Cache);
        Assert.Equal(Path.Combine(exe.Path, "data", "logs"), d.Logs);
    }

    [Fact]
    public void A_settings_folder_beside_the_exe_is_not_portable_mode()
    {
        using var exe = new TempFolder();
        Directory.CreateDirectory(Path.Combine(exe.Path, "settings.json"));
        Assert.False(DataDirectories.Resolve(exe.Path, exe.Path).IsPortable);
    }

    [Fact]
    public void Plain_constructor_keeps_its_old_meaning()
    {
        var d = new DataDirectories(@"C:\data");
        Assert.Equal(@"C:\data\settings.json", d.SettingsFile);
        Assert.False(d.IsPortable);
        Assert.Equal(@"C:\data\api", d.Api);
    }

    [Fact]
    public async Task Settings_backed_scrape_state_round_trips()
    {
        var store = SettingsStore.InMemory();
        var state = new SettingsScrapeStateStore(store, null);
        Assert.Null(await state.LoadLastCheckedAsync(Ct));

        var time = new DateTimeOffset(2026, 11, 11, 13, 5, 0, TimeSpan.FromHours(1));
        await state.SaveLastCheckedAsync(time, Ct);
        Assert.Equal(time, await state.LoadLastCheckedAsync(Ct));
        Assert.Equal(time, store.Current.Internal.LastTimeChecked);
    }

    [Fact]
    public async Task Legacy_scrape_state_file_moves_into_the_settings()
    {
        using var temp = new TempFolder();
        var dirs = new DataDirectories(temp.Path);
        var time = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        var legacy = new FileScrapeStateStore(dirs);
        await legacy.SaveLastCheckedAsync(time, Ct);
        Assert.True(File.Exists(legacy.FilePath));

        await using var store = await SettingsStore.LoadAsync(new SettingsStoreOptions { FilePath = dirs.SettingsFile }, Ct);
        var state = new SettingsScrapeStateStore(store, dirs);
        Assert.Equal(time, await state.LoadLastCheckedAsync(Ct));
        Assert.False(File.Exists(legacy.FilePath));
        Assert.Contains("lastTimeChecked", File.ReadAllText(dirs.SettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_file_does_not_override_a_newer_settings_value()
    {
        using var temp = new TempFolder();
        var dirs = new DataDirectories(temp.Path);
        await new FileScrapeStateStore(dirs).SaveLastCheckedAsync(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), Ct);
        var current = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = SettingsStore.InMemory(AppSettings.Default with { Internal = new InternalSettings { LastTimeChecked = current } });

        var state = new SettingsScrapeStateStore(store, dirs);
        await state.SaveLastCheckedAsync(current.AddHours(1), Ct);
        Assert.Equal(current.AddHours(1), await state.LoadLastCheckedAsync(Ct));
        Assert.False(File.Exists(Path.Combine(dirs.State, FileScrapeStateStore.FileName)));
    }
}
