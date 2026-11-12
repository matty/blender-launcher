using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Core.Settings.Upstream;

namespace BlenderLauncher.Core.Tests.Settings;

/// <summary>A fake Credential Manager: raw targets with a user name and secret.</summary>
internal sealed class FakeLegacyCredentials : ILegacyCredentialReader
{
    public Dictionary<string, LegacyCredential> Targets { get; } = new(StringComparer.Ordinal);

    public bool Fail { get; init; }

    public List<string> Reads { get; } = [];

    public LegacyCredential? Read(string target)
    {
        Reads.Add(target);
        if (Fail)
        {
            throw new CredentialStoreException("vault locked");
        }

        return Targets.GetValueOrDefault(target);
    }
}

public partial class UpstreamIniImporterTests
{
    private static (AppSettings Settings, UpstreamImportResult Result) Import(string iniText, UpstreamImportOptions? options = null, AppSettings? start = null) =>
        UpstreamIniImporter.Import(start ?? AppSettings.Default, QSettingsIni.Parse(iniText), "test.ini", options ?? new UpstreamImportOptions());

    private static (AppSettings Settings, UpstreamImportResult Result, InMemoryCredentialStore Credentials) ImportFull(FakeLegacyCredentials? legacy = null)
    {
        var credentials = new InMemoryCredentialStore();
        var (settings, result) = UpstreamIniImporter.Import(
            AppSettings.Default,
            QSettingsIni.Load(Fixtures.PathOf("Settings/upstream-full.ini")),
            "upstream-full.ini",
            new UpstreamImportOptions { Credentials = credentials, LegacyCredentials = legacy });
        return (settings, result, credentials);
    }

    // ---- the upstream CI fixture ----

    [Fact]
    public void Ci_fixture()
    {
        var ini = QSettingsIni.Load(Fixtures.PathOf("Settings/upstream-ci.ini"));
        var (s, r) = UpstreamIniImporter.Import(AppSettings.Default, ini, "ci.ini", new UpstreamImportOptions());

        Assert.Equal(UpstreamImportStatus.Imported, r.Status);
        Assert.Null(s.General.LibraryFolder);
        Assert.Equal(".", r.RelativeLibraryFolder);
        Assert.Equal(".", s.Internal.UpstreamRelativeLibraryFolder);
        Assert.Equal(ImportOutcome.Skipped, r.For("library_folder")!.Outcome);
        Assert.False(s.Builds.ScrapeDaily);
        Assert.False(s.Builds.ScrapeExperimental);
        Assert.Equal(ImportOutcome.Mapped, r.For("scrape_automated_builds")!.Outcome);
        Assert.False(s.Builds.ScrapeBforartists);
        Assert.Equal("CachingBot", s.Connection.UserId);
        Assert.True(s.Appearance.UseSystemTitleBar);
        Assert.True(s.General.UsePreReleaseBuilds);
        Assert.True(s.Internal.FirstRunSeen);
        Assert.Equal("4.3", s.Builds.MinimumStableVersion);
        Assert.True(s.Internal.TrayNoticeShown);
        Assert.Equal("installed:stable", s.Appearance.DefaultView);
        Assert.True(s.Internal.UpstreamImportDone);
        Assert.Empty(r.Failed);
        Assert.Equal(ini.Count, r.Entries.Count);
    }

    // ---- the hand-made full file ----

    [Fact]
    public void Full_fixture_general()
    {
        var (s, r, _) = ImportFull();
        Assert.Equal(@"C:\Users\test\Blender Launcher Library", s.General.LibraryFolder);
        Assert.Null(r.RelativeLibraryFolder);
        Assert.Equal("fr", s.General.Language);
        Assert.True(s.General.MinimiseToTray);
        Assert.True(s.General.LaunchMinimisedToTray);
        Assert.Equal(6, s.General.WorkerThreadCount);
        Assert.True(s.General.UsePreReleaseBuilds);
        Assert.Equal(-1, s.General.LaunchTimerSeconds);
        Assert.Equal(ImportOutcome.Mapped, r.For("launch_timer")!.Outcome);
        Assert.Equal(DeleteAction.DeletePermanently, s.General.DefaultDeleteAction);
        Assert.False(s.General.PurgeTempOnStartup);
        Assert.Equal(LogLevel.Debug, s.General.LogLevel);
        Assert.False(s.General.RegisterInAddRemovePrograms);
        Assert.Equal(ImportOutcome.Dropped, r.For("auto_register_winget")!.Outcome);
    }

    [Fact]
    public void Full_fixture_appearance_and_default_view()
    {
        var (s, r, _) = ImportFull();
        Assert.True(s.Appearance.UseSystemTitleBar);
        Assert.False(s.Appearance.NotifyNewBuilds);
        Assert.False(s.Appearance.NotifyDownloadFinished);
        Assert.False(s.Appearance.NotifyErrors);

        // default_tab=1 (Downloads), default_downloads_page=2 (legacy index: experimental).
        Assert.Equal("download:experimental", s.Appearance.DefaultView);
        Assert.Equal(ImportOutcome.Mapped, r.For("default_tab")!.Outcome);
        Assert.Equal(ImportOutcome.Mapped, r.For("default_downloads_page")!.Outcome);
        Assert.Equal(ImportOutcome.Skipped, r.For("default_library_page")!.Outcome);
        Assert.Equal(ImportOutcome.Dropped, r.For("sync_library_and_downloads_pages")!.Outcome);
        Assert.Equal(ImportOutcome.Dropped, r.For("dpi_scale_factor")!.Outcome);
    }

    [Fact]
    public void Full_fixture_connection_and_secrets_from_ini_plain_text()
    {
        var (s, r, creds) = ImportFull();
        Assert.Equal("my-custom-id-123", s.Connection.UserId);
        Assert.False(s.Connection.UseCustomTlsCertificates);
        Assert.Equal(ProxyType.Socks5, s.Connection.ProxyType);
        Assert.Equal("10.0.0.1", s.Connection.ProxyHost);
        Assert.Equal(8080, s.Connection.ProxyPort);
        Assert.Equal("ghp_plaintextlegacytoken", creds.Get(CredentialKeys.GitHubToken));
        Assert.Equal("proxyuser", creds.Get(CredentialKeys.ProxyUser));
        Assert.Equal("proxypass", creds.Get(CredentialKeys.ProxyPassword));
        Assert.Contains("INI", r.For("github_token")!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(r.Entries, e => e.Message.Contains("ghp_plaintext", StringComparison.Ordinal) || e.Message.Contains("proxypass", StringComparison.Ordinal));
    }

    [Fact]
    public void Full_fixture_builds()
    {
        var (s, r, _) = ImportFull();
        var b = s.Builds;
        Assert.False(b.ShowStable);
        Assert.False(b.ShowDaily);
        Assert.False(b.ShowExperimentalAndPatch);
        Assert.False(b.ShowBforartists);
        Assert.False(b.ShowUpbge);
        Assert.True(b.ShowUpbgeWeekly);
        Assert.False(b.ScrapeStable);

        // scrape_daily_builds is present, so the legacy key only sets experimental.
        Assert.True(b.ScrapeDaily);
        Assert.False(b.ScrapeExperimental);
        Assert.False(b.ScrapeBforartists);
        Assert.False(b.ScrapeUpbge);
        Assert.True(b.ScrapeUpbgeWeekly);
        Assert.True(b.ShowDailyArchive && b.ShowExperimentalArchive && b.ShowPatchArchive);
        Assert.Equal("2.93", b.MinimumStableVersion);
        Assert.True(b.CheckAutomatically);
        Assert.Equal(596, b.CheckIntervalHours);
        Assert.Equal(ImportOutcome.Mapped, r.For("new_builds_check_frequency")!.Outcome);
        Assert.False(b.CheckOnStartup);
        Assert.False(b.ShowUpdateButton);
        Assert.True(b.UseAdvancedUpdateButton);
        Assert.False(b.ShowStableUpdateButton || b.ShowDailyUpdateButton || b.ShowExperimentalUpdateButton
                     || b.ShowBforartistsUpdateButton || b.ShowUpbgeStableUpdateButton || b.ShowUpbgeWeeklyUpdateButton);
        Assert.Equal(UpdateBehaviour.Major, b.UpdateBehaviour);
        Assert.Equal(UpdateBehaviour.Minor, b.StableUpdateBehaviour);
        Assert.Equal(UpdateBehaviour.Major, b.DailyUpdateBehaviour);
        Assert.Equal(UpdateBehaviour.Minor, b.ExperimentalUpdateBehaviour);
        Assert.Equal(UpdateBehaviour.Patch, b.BforartistsUpdateBehaviour);
        Assert.Equal(UpdateBehaviour.Major, b.UpbgeStableUpdateBehaviour);
        Assert.Equal(UpdateBehaviour.Minor, b.UpbgeWeeklyUpdateBehaviour);
        Assert.True(b.InstallTemplate);
        Assert.Equal(NewDownloadDefault.UpbgeWeekly, b.MarkNewDownloadsAsDefault);
        Assert.True(b.FetchPrTitles);
        Assert.False(b.PrependPrNumber);
        Assert.Equal(new Dictionary<string, string> { ["4.2.0"] = "4.^.^-daily@^", ["2.80.0"] = "2.^.^@^" }, b.VersionSpecificQueries);

        // First quick-launch path, relative to the imported library root; favorite_path is superseded.
        Assert.Equal(@"daily\blender-4.4.0-alpha+main.abc123", b.DefaultBuild);
        Assert.Equal(ImportOutcome.Mapped, r.For("Internal/quick_launch_paths")!.Outcome);
        Assert.Equal(ImportOutcome.Skipped, r.For("Internal/favorite_path")!.Outcome);
    }

    [Fact]
    public void Full_fixture_launching_and_internal()
    {
        var (s, r, _) = ImportFull();
        Assert.Equal(OnLaunchAction.Minimise, s.Launching.OnLaunchAction);
        Assert.False(s.Launching.HideConsole);
        Assert.Equal("--python-use-system-env", s.Launching.BlenderStartupArguments);
        Assert.Equal("nice -n 5", s.Launching.BashArguments);
        Assert.True(s.Launching.UseNohup);
        Assert.True(s.Launching.QuickLaunchHotkeyEnabled);
        Assert.Equal("ctrl+alt+b", s.Launching.QuickLaunchHotkey);

        Assert.Equal(new DateTimeOffset(2026, 11, 10, 9, 30, 15, 123, TimeSpan.FromHours(1)).AddTicks(4560), s.Internal.LastTimeChecked);
        Assert.Equal(TimeSpan.FromHours(1), s.Internal.LastTimeChecked!.Value.Offset);
        Assert.True(s.Internal.TrayNoticeShown);
        Assert.True(s.Internal.Window.Maximised);
        Assert.Equal(SortColumn.Version, s.Internal.SortFor(SortViews.Installed).Column);
        Assert.Equal(SortColumn.Built, s.Internal.SortFor(SortViews.Download).Column);
        Assert.Equal(SortColumn.Version, s.Internal.SortFor(SortViews.Favorites).Column);
        Assert.Equal(ImportOutcome.Dropped, r.For("Internal/window_geometry")!.Outcome);
        Assert.Equal(ImportOutcome.Dropped, r.For("Internal/global_column_widths")!.Outcome);
        Assert.Equal(ImportOutcome.Dropped, r.For("Internal/dont_show_resource_err_again")!.Outcome);
        Assert.Equal(ImportOutcome.Skipped, r.For("some_future_key")!.Outcome);
    }

    [Fact]
    public void Full_fixture_reports_every_key_once_and_nothing_fails()
    {
        var (_, r, _) = ImportFull();
        var ini = QSettingsIni.Load(Fixtures.PathOf("Settings/upstream-full.ini"));
        Assert.Empty(r.Failed);
        Assert.Equal(r.Entries.Count, r.Entries.Select(e => e.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var key in ini.Keys)
        {
            Assert.NotNull(r.For(key));
        }
    }

    // ---- the mapping table against upstream's keys ----

    [Fact]
    public void Every_upstream_key_is_accounted_for()
    {
        var upstreamKeys = UpstreamKeys();
        Assert.True(upstreamKeys.Count > 80, $"only {upstreamKeys.Count} upstream keys listed");
        var missing = upstreamKeys.Where(k => UpstreamKeyMap.Find(k) is null).ToList();
        Assert.True(missing.Count == 0, "Not in UpstreamKeyMap: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_mapped_key_is_handled_by_the_importer()
    {
        // One INI with every key in the map set to a plausible value: each must produce exactly one report line
        // and none may be reported as an unknown key.
        var lines = new List<string>();
        foreach (var group in UpstreamKeyMap.All.GroupBy(k => k.Key.Contains('/') ? k.Key[..k.Key.IndexOf('/')] : ""))
        {
            lines.Add(group.Key.Length == 0 ? "[General]" : $"[{group.Key}]");
            foreach (var key in group)
            {
                var name = key.Key.Contains('/') ? key.Key[(key.Key.IndexOf('/') + 1)..] : key.Key;
                lines.Add($"{name}={SampleValue(key.Key)}");
            }
        }

        var (_, r) = Import(string.Join('\n', lines), new UpstreamImportOptions { Credentials = new InMemoryCredentialStore() });
        foreach (var key in UpstreamKeyMap.All)
        {
            var entry = r.For(key.Key);
            Assert.True(entry is not null, $"no report line for {key.Key}");
            Assert.True(entry.Message != "not an upstream setting the port knows", $"{key.Key} treated as unknown");
            Assert.True(entry.Outcome != ImportOutcome.Failed, $"{key.Key} failed: {entry.Message}");
            if (key.Disposition == UpstreamKeyDisposition.Dropped)
            {
                Assert.Equal(ImportOutcome.Dropped, entry.Outcome);
            }
        }
    }

    private static string SampleValue(string key) => key switch
    {
        "library_folder" => "C:/lib",
        "on_blender_launch_action" or "default_delete_action" or "default_tab" or "proxy/type" => "1",
        "dpi_scale_factor" => "1.5",
        "default_library_page" or "default_downloads_page" => "daily",
        "mark_as_favorite" => "daily",
        "language" => "en",
        "log_level" => "WARNING",
        "quick_launch_key_seq" => "alt+f12",
        "user_id" => "abcdefgh",
        "minimum_blender_stable_version" => "3.6",
        "version_specific_queries" => "{}",
        "Internal/quick_launch_paths" => "\"[\\\"C:/lib/daily/x\\\"]\"",
        "Internal/favorite_path" => "C:/lib/daily/y",
        "Internal/last_time_checked_utc" => "2025-01-01T00:00:00+00:00",
        "Internal/global_column_widths" => "\"[1, 2, 3]\"",
        "Internal/window_geometry" => "@ByteArray(abc)",
        "proxy/host" => "proxy.local",
        "proxy/port" => "3128",
        "github_token" or "proxy/user" or "proxy/password" => "secret",
        _ when key.EndsWith("_behavior", StringComparison.Ordinal) => "2",
        _ when key.EndsWith("_sorting_type", StringComparison.Ordinal) => "2",
        "new_builds_check_frequency" or "launch_timer" or "worker_thread_count" => "8",
        _ => "true",
    };

    /// <summary>Every setting key Blender Launcher V2 v2.8.2 writes to its INI, plus the legacy keys it still reads.</summary>
    private static List<string> UpstreamKeys() =>
    [
        "library_folder",
        "first_time_setup_seen",
        "launch_minimized_to_tray",
        "on_blender_launch_action",
        "dpi_scale_factor",
        "sync_library_and_downloads_pages",
        "default_library_page",
        "default_downloads_page",
        "mark_as_favorite",
        "default_tab",
        "default_delete_action",
        "enable_new_builds_notifications",
        "enable_download_notifications",
        "error_popup",
        "language",
        "blender_startup_arguments",
        "bash_arguments",
        "use_nohup",
        "launch_blender_no_console",
        "show_update_button",
        "use_advanced_update_button",
        "show_stable_update_button",
        "show_daily_update_button",
        "show_experimental_update_button",
        "show_bfa_update_button",
        "show_upbge_stable_update_button",
        "show_upbge_weekly_update_button",
        "update_behavior",
        "stable_update_behavior",
        "daily_update_behavior",
        "experimental_update_behavior",
        "bfa_update_behavior",
        "upbge_stable_update_behavior",
        "upbge_weekly_update_behavior",
        "install_template",
        "show_tray_icon",
        "quick_launch_key_seq",
        "enable_quick_launch_key_seq",
        "use_custom_tls_certificates",
        "user_id",
        "check_for_new_builds_automatically",
        "new_builds_check_frequency",
        "buildcheck_on_startup",
        "minimum_blender_stable_version",
        "scrape_stable_builds",
        "scrape_daily_builds",
        "scrape_experimental_builds",
        "scrape_bfa_builds",
        "scrape_upbge_builds",
        "scrape_upbge_weekly_builds",
        "show_stable_builds",
        "show_daily_builds",
        "show_experimental_and_patch_builds",
        "show_bfa_builds",
        "show_upbge_builds",
        "show_upbge_weekly_builds",
        "show_daily_archive_builds",
        "show_experimental_archive_builds",
        "show_patch_archive_builds",
        "pr_names_fetch_during_scrape",
        "prepend_pr_number_on_label",
        "worker_thread_count",
        "use_pre_release_builds",
        "log_level",
        "use_system_title_bar",
        "version_specific_queries",
        "launch_timer",
        "purge_temp_on_startup",
        "auto_register_winget",
        "github_token",
        "Internal/quick_launch_paths",
        "Internal/dont_show_resource_err_again",
        "Internal/last_time_checked_utc",
        "Internal/LibraryPage_sorting_type",
        "Internal/DownloadsPage_sorting_type",
        "Internal/FavoritesPage_sorting_type",
        "Internal/tray_icon_notified",
        "Internal/global_column_widths",
        "Internal/window_geometry",
        "Internal/window_maximized",
        "proxy/type",
        "proxy/host",
        "proxy/port",
        "proxy/user",
        "proxy/password",
        "Internal/favorite_path",
        "scrape_automated_builds",
    ];

    // ---- individual rules ----

    [Theory]
    [InlineData("disable", NewDownloadDefault.None)]
    [InlineData("experimental", NewDownloadDefault.Experimental)]
    [InlineData("upbge-weekly", NewDownloadDefault.UpbgeWeekly)]
    [InlineData("2", NewDownloadDefault.Daily)]
    public void Mark_as_favorite_values_and_legacy_indices(string value, NewDownloadDefault expected)
    {
        var (s, _) = Import($"mark_as_favorite={value}");
        Assert.Equal(expected, s.Builds.MarkNewDownloadsAsDefault);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("nonsense")]
    public void Mark_as_favorite_unknown_fails(string value)
    {
        var (s, r) = Import($"mark_as_favorite={value}");
        Assert.Equal(NewDownloadDefault.None, s.Builds.MarkNewDownloadsAsDefault);
        Assert.Equal(ImportOutcome.Failed, r.For("mark_as_favorite")!.Outcome);
    }

    [Theory]
    [InlineData("default_tab=2\ndefault_library_page=daily", "installed:favorites")]
    [InlineData("default_tab=0\ndefault_library_page=6", "installed:custom")]
    [InlineData("default_library_page=upbge-weekly", "installed:upbge-weekly")]
    [InlineData("default_tab=0", "installed:all")]
    [InlineData("default_tab=1", "download:stable")]
    [InlineData("default_tab=1\ndefault_downloads_page=upbge-stable", "download:upbge-stable")]
    public void Default_view_collapses_tab_and_pages(string ini, string expected)
    {
        var (s, _) = Import(ini);
        Assert.Equal(expected, s.Appearance.DefaultView);
    }

    [Fact]
    public void Default_view_unknown_page_keeps_default()
    {
        var (s, r) = Import("default_tab=0\ndefault_library_page=bogus");
        Assert.Equal("installed:all", s.Appearance.DefaultView);
        Assert.Equal(ImportOutcome.Failed, r.For("default_library_page")!.Outcome);
    }

    [Fact]
    public void Scrape_automated_fallback_applies_to_both_when_absent()
    {
        var (s, r) = Import("scrape_automated_builds=false");
        Assert.False(s.Builds.ScrapeDaily);
        Assert.False(s.Builds.ScrapeExperimental);
        Assert.Contains("scrapeDaily", r.For("scrape_automated_builds")!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scrape_automated_is_superseded_when_both_present()
    {
        var (s, r) = Import("scrape_automated_builds=false\nscrape_daily_builds=true\nscrape_experimental_builds=true");
        Assert.True(s.Builds.ScrapeDaily);
        Assert.True(s.Builds.ScrapeExperimental);
        Assert.Equal(ImportOutcome.Skipped, r.For("scrape_automated_builds")!.Outcome);
    }

    [Fact]
    public void Favorite_path_used_when_quick_launch_paths_absent_or_not_json()
    {
        var (s, r) = Import("library_folder=D:/Lib\n[Internal]\nfavorite_path=D:/Lib/stable/blender-4.2.3-lts");
        Assert.Equal(@"stable\blender-4.2.3-lts", s.Builds.DefaultBuild);
        Assert.Equal(ImportOutcome.Mapped, r.For("Internal/favorite_path")!.Outcome);

        (s, _) = Import("[Internal]\nquick_launch_paths=garbage\nfavorite_path=E:/x/y");
        Assert.Equal(@"E:\x\y", s.Builds.DefaultBuild);
    }

    [Fact]
    public void Empty_quick_launch_list_wins_over_favorite_path_as_upstream()
    {
        var (s, r) = Import("[Internal]\nquick_launch_paths=[]\nfavorite_path=E:/x/y");
        Assert.Null(s.Builds.DefaultBuild);
        Assert.Equal(ImportOutcome.Skipped, r.For("Internal/quick_launch_paths")!.Outcome);
    }

    [Fact]
    public void Default_build_outside_library_or_without_library_stays_absolute()
    {
        var (s, _) = Import("[Internal]\nquick_launch_paths=\"[\\\"C:/Lib/daily/b\\\"]\"");
        Assert.Equal(@"C:\Lib\daily\b", s.Builds.DefaultBuild);

        Assert.Equal(@"D:\Other\b", UpstreamIniImporter.ToDefaultBuild("D:/Other/b", @"C:\Lib"));
        Assert.Equal(@"daily\b", UpstreamIniImporter.ToDefaultBuild("C:/Lib/daily/b", @"C:\Lib\"));
        Assert.Equal(@"C:\Lib", UpstreamIniImporter.ToDefaultBuild("C:/Lib", @"C:\Lib"));
        Assert.Equal(@"C:\Libraries\b", UpstreamIniImporter.ToDefaultBuild("C:/Libraries/b", @"C:\Lib"));
    }

    [Theory]
    [InlineData("None", "3.0", ImportOutcome.Mapped)]
    [InlineData("4.2", "4.2", ImportOutcome.Imported)]
    [InlineData("1", "4.3", ImportOutcome.Mapped)]
    public void Minimum_stable_version(string value, string expected, ImportOutcome outcome)
    {
        var api = ApiData.Parse("{\"api_file_version\":\"1.0\",\"blender_versions\":{\"4.4\":\"\",\"4.3\":\"\",\"4.2\":\"LTS\"}}");
        var (s, r) = Import($"minimum_blender_stable_version={value}", new UpstreamImportOptions { ApiData = api });
        Assert.Equal(expected, s.Builds.MinimumStableVersion);
        Assert.Equal(outcome, r.For("minimum_blender_stable_version")!.Outcome);
    }

    [Fact]
    public void Minimum_stable_version_legacy_index_without_api_data_fails()
    {
        var (s, r) = Import("minimum_blender_stable_version=3");
        Assert.Equal("3.0", s.Builds.MinimumStableVersion);
        Assert.Equal(ImportOutcome.Failed, r.For("minimum_blender_stable_version")!.Outcome);
    }

    [Fact]
    public void Generated_uuid_user_id_is_skipped_and_invalid_one_fails()
    {
        var (s, r) = Import("user_id=0f8fad5b-d9cb-469f-a165-70867728950e");
        Assert.Null(s.Connection.UserId);
        Assert.Equal(ImportOutcome.Skipped, r.For("user_id")!.Outcome);

        (s, r) = Import("user_id=bad id!");
        Assert.Null(s.Connection.UserId);
        Assert.Equal(ImportOutcome.Failed, r.For("user_id")!.Outcome);
    }

    [Fact]
    public void Worker_count_zero_means_automatic()
    {
        var (s, r) = Import("worker_thread_count=0");
        Assert.Null(s.General.WorkerThreadCount);
        Assert.Equal(ImportOutcome.Mapped, r.For("worker_thread_count")!.Outcome);
    }

    [Fact]
    public void Bad_values_fail_and_keep_defaults()
    {
        var (s, r) = Import("on_blender_launch_action=9\nupdate_behavior=x\nlanguage=de\nlog_level=TRACE\nnew_builds_check_frequency=often\nproxy/type=7\n[Internal]\nlast_time_checked_utc=yesterday\nLibraryPage_sorting_type=5\nwindow_geometry=@ByteArray(x)");
        Assert.Equal(AppSettings.Default.Launching.OnLaunchAction, s.Launching.OnLaunchAction);
        Assert.Equal(UpdateBehaviour.Patch, s.Builds.UpdateBehaviour);
        Assert.Equal("auto", s.General.Language);
        Assert.Equal(LogLevel.Info, s.General.LogLevel);
        Assert.Equal(12, s.Builds.CheckIntervalHours);
        Assert.Null(s.Internal.LastTimeChecked);
        Assert.Empty(s.Internal.ViewSorts);
        foreach (var key in new[] { "on_blender_launch_action", "update_behavior", "language", "log_level", "new_builds_check_frequency", "Internal/last_time_checked_utc", "Internal/LibraryPage_sorting_type" })
        {
            Assert.Equal(ImportOutcome.Failed, r.For(key)!.Outcome);
        }

        Assert.Equal(ImportOutcome.Dropped, r.For("Internal/window_geometry")!.Outcome);
    }

    [Fact]
    public void Epoch_last_check_is_never_checked()
    {
        var (s, r) = Import("[Internal]\nlast_time_checked_utc=1970-01-01T00:00:00+00:00");
        Assert.Null(s.Internal.LastTimeChecked);
        Assert.Equal(ImportOutcome.Skipped, r.For("Internal/last_time_checked_utc")!.Outcome);
    }

    [Fact]
    public void Import_does_not_touch_settings_it_does_not_find()
    {
        var start = AppSettings.Default with { Appearance = new AppearanceSettings { Accent = Accent.Orange } };
        var (s, _) = Import("use_system_title_bar=true", start: start);
        Assert.Equal(Accent.Orange, s.Appearance.Accent);
        Assert.True(s.Appearance.UseSystemTitleBar);
    }

    // ---- keyring lookup ----

    [Fact]
    public void Keyring_main_target_with_matching_user_wins()
    {
        var legacy = new FakeLegacyCredentials();
        legacy.Targets["BlenderLauncher"] = new LegacyCredential("github_token", "  tok-main  ");
        legacy.Targets["github_token@BlenderLauncher"] = new LegacyCredential("github_token", "tok-compound");
        var found = UpstreamCredentialLookup.Find("github_token", legacy, QSettingsIni.Parse("github_token=tok-ini"), out var error);
        Assert.Equal(new UpstreamSecret("tok-main", UpstreamSecretSource.Keyring), found);
        Assert.Null(error);
    }

    [Fact]
    public void Keyring_compound_target_when_main_belongs_to_another_key()
    {
        var legacy = new FakeLegacyCredentials();
        legacy.Targets["BlenderLauncher"] = new LegacyCredential("proxy/host", "1.2.3.4");
        legacy.Targets["github_token@BlenderLauncher"] = new LegacyCredential("github_token", "tok-compound");
        var found = UpstreamCredentialLookup.Find("github_token", legacy, null, out _);
        Assert.Equal(new UpstreamSecret("tok-compound", UpstreamSecretSource.KeyringCompound), found);
        Assert.Equal(["BlenderLauncher", "github_token@BlenderLauncher"], legacy.Reads);
    }

    [Fact]
    public void Ini_plain_text_is_the_last_resort_and_blank_is_absent()
    {
        var legacy = new FakeLegacyCredentials();
        legacy.Targets["github_token@BlenderLauncher"] = new LegacyCredential("github_token", "   ");
        Assert.Equal(UpstreamSecretSource.Ini, UpstreamCredentialLookup.Find("github_token", legacy, QSettingsIni.Parse("github_token=x"), out _)!.Source);
        Assert.Null(UpstreamCredentialLookup.Find("github_token", legacy, QSettingsIni.Parse("github_token=  "), out _));
        Assert.Equal("h", UpstreamCredentialLookup.Find("proxy/host", null, QSettingsIni.Parse("[proxy]\nhost=h"), out _)!.Value);
    }

    [Fact]
    public void Keyring_failure_falls_back_to_ini_and_reports()
    {
        var legacy = new FakeLegacyCredentials { Fail = true };
        var found = UpstreamCredentialLookup.Find("github_token", legacy, QSettingsIni.Parse("github_token=x"), out var error);
        Assert.Equal(UpstreamSecretSource.Ini, found!.Source);
        Assert.Equal("vault locked", error);

        var (_, r) = Import("", new UpstreamImportOptions { LegacyCredentials = legacy, Credentials = new InMemoryCredentialStore() });
        Assert.Equal(ImportOutcome.Failed, r.For("github_token")!.Outcome);
    }

    [Fact]
    public void Importer_reads_keyring_entries_without_ini_keys()
    {
        var legacy = new FakeLegacyCredentials();
        legacy.Targets["BlenderLauncher"] = new LegacyCredential("github_token", "tok");
        legacy.Targets["proxy/host@BlenderLauncher"] = new LegacyCredential("proxy/host", "proxy.example");
        legacy.Targets["proxy/port@BlenderLauncher"] = new LegacyCredential("proxy/port", "3128");
        legacy.Targets["proxy/password@BlenderLauncher"] = new LegacyCredential("proxy/password", "pw");
        var creds = new InMemoryCredentialStore();
        var (s, r) = Import("", new UpstreamImportOptions { LegacyCredentials = legacy, Credentials = creds });
        Assert.Equal("tok", creds.Get(CredentialKeys.GitHubToken));
        Assert.Equal("pw", creds.Get(CredentialKeys.ProxyPassword));
        Assert.Null(creds.Get(CredentialKeys.ProxyUser));
        Assert.Equal("proxy.example", s.Connection.ProxyHost);
        Assert.Equal(3128, s.Connection.ProxyPort);
        Assert.Null(r.For("proxy/user"));
    }

    [Fact]
    public void Existing_port_secret_is_not_overwritten()
    {
        var creds = new InMemoryCredentialStore();
        creds.Set(CredentialKeys.GitHubToken, "mine");
        var (_, r) = Import("github_token=theirs", new UpstreamImportOptions { Credentials = creds });
        Assert.Equal("mine", creds.Get(CredentialKeys.GitHubToken));
        Assert.Equal(ImportOutcome.Skipped, r.For("github_token")!.Outcome);
    }

    [Fact]
    public void Secret_without_a_store_fails()
    {
        var (_, r) = Import("github_token=x");
        Assert.Equal(ImportOutcome.Failed, r.For("github_token")!.Outcome);
    }

    // ---- run once ----

    [Fact]
    public async Task Run_once_imports_then_does_nothing()
    {
        using var temp = new TempFolder();
        var localAppData = temp.Path;
        var iniPath = UpstreamPaths.UserIni(localAppData);
        Assert.Equal(Path.Combine(localAppData, "Blender Launcher", "Blender Launcher.ini"), iniPath);
        Directory.CreateDirectory(Path.GetDirectoryName(iniPath)!);
        File.WriteAllText(iniPath, "use_system_title_bar=true\n");

        await using var store = await SettingsStore.LoadAsync(new SettingsStoreOptions { FilePath = Path.Combine(temp.Path, "settings.json") }, TestContext.Current.CancellationToken);
        var options = new UpstreamImportOptions { LocalAppData = localAppData };
        var first = await UpstreamIniImporter.RunOnceAsync(store, options, ct: TestContext.Current.CancellationToken);
        Assert.Equal(UpstreamImportStatus.Imported, first.Status);
        Assert.True(store.Current.Appearance.UseSystemTitleBar);
        Assert.True(store.Current.Internal.UpstreamImportDone);
        Assert.True(File.Exists(Path.Combine(temp.Path, "settings.json")));

        File.WriteAllText(iniPath, "use_system_title_bar=false\n");
        var second = await UpstreamIniImporter.RunOnceAsync(store, options, ct: TestContext.Current.CancellationToken);
        Assert.Equal(UpstreamImportStatus.AlreadyDone, second.Status);
        Assert.True(store.Current.Appearance.UseSystemTitleBar);

        // A file picked by hand imports regardless.
        var picked = await UpstreamIniImporter.ImportFileAsync(store, iniPath, options, TestContext.Current.CancellationToken);
        Assert.Equal(UpstreamImportStatus.Imported, picked.Status);
        Assert.False(store.Current.Appearance.UseSystemTitleBar);
    }

    [Fact]
    public async Task Run_once_without_an_ini_is_not_found_and_not_marked_done()
    {
        using var temp = new TempFolder();
        var store = SettingsStore.InMemory();
        var r = await UpstreamIniImporter.RunOnceAsync(store, new UpstreamImportOptions { LocalAppData = temp.Path }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(UpstreamImportStatus.NotFound, r.Status);
        Assert.False(store.Current.Internal.UpstreamImportDone);
    }

    [Fact]
    public async Task Import_result_is_logged()
    {
        using var temp = new TempFolder();
        var iniPath = Path.Combine(temp.Path, "portable.ini");
        File.WriteAllText(iniPath, "use_nohup=true\nlanguage=xx\n");
        var log = Log.Open(Path.Combine(temp.Path, "logs"));
        await using (log)
        {
            await UpstreamIniImporter.ImportFileAsync(SettingsStore.InMemory(), iniPath, new UpstreamImportOptions { Log = log }, TestContext.Current.CancellationToken);
            await log.FlushAsync();
        }

        var text = File.ReadAllText(Path.Combine(temp.Path, "logs", Log.FileName));
        Assert.Contains("1 imported, 0 skipped, 1 failed", text, StringComparison.Ordinal);
        Assert.Contains("language: Failed", text, StringComparison.Ordinal);
    }
}

/// <summary>A temp folder deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bl-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
