namespace BlenderLauncher.Core.Settings.Upstream;

/// <summary>What the importer does with an upstream key.</summary>
public enum UpstreamKeyDisposition
{
    /// <summary>Copied to its counterpart unchanged (apart from validation).</summary>
    Imported,

    /// <summary>Converted to a different port setting (legacy forms).</summary>
    Mapped,

    /// <summary>Moved to the credential store (or, for the proxy host and port, into the connection settings).</summary>
    Credential,

    /// <summary>Not carried over.</summary>
    Dropped,
}

/// <summary>One upstream key and what becomes of it.</summary>
public sealed record UpstreamKey(string Key, UpstreamKeyDisposition Disposition, string Target);

/// <summary>
/// Every upstream setting key, as <see cref="QSettingsIni"/> names
/// them, with its fate in the port. <see cref="UpstreamIniImporter"/> handles exactly these.
/// </summary>
public static class UpstreamKeyMap
{
    private const UpstreamKeyDisposition I = UpstreamKeyDisposition.Imported;
    private const UpstreamKeyDisposition M = UpstreamKeyDisposition.Mapped;
    private const UpstreamKeyDisposition C = UpstreamKeyDisposition.Credential;
    private const UpstreamKeyDisposition D = UpstreamKeyDisposition.Dropped;

    public static IReadOnlyList<UpstreamKey> All { get; } =
    [
        // [General]
        new("library_folder", I, "general.libraryFolder (absolute only; a relative path goes to internal.upstreamRelativeLibraryFolder for the user)"),
        new("first_time_setup_seen", I, "internal.firstRunSeen"),
        new("launch_minimized_to_tray", I, "general.launchMinimisedToTray"),
        new("on_blender_launch_action", I, "launching.onLaunchAction"),
        new("dpi_scale_factor", D, "dropped (Avalonia scales per monitor)"),
        new("sync_library_and_downloads_pages", M, "appearance.defaultView"),
        new("default_library_page", M, "appearance.defaultView"),
        new("default_downloads_page", M, "appearance.defaultView"),
        new("default_tab", M, "appearance.defaultView"),
        new("mark_as_favorite", M, "builds.markNewDownloadsAsDefault"),
        new("default_delete_action", I, "general.defaultDeleteAction"),
        new("enable_new_builds_notifications", I, "appearance.notifyNewBuilds"),
        new("enable_download_notifications", I, "appearance.notifyDownloadFinished"),
        new("error_popup", I, "appearance.notifyErrors"),
        new("language", I, "general.language"),
        new("blender_startup_arguments", I, "launching.blenderStartupArguments"),
        new("bash_arguments", I, "launching.bashArguments"),
        new("use_nohup", I, "launching.useNohup"),
        new("launch_blender_no_console", I, "launching.hideConsole"),
        new("show_update_button", I, "builds.showUpdateButton"),
        new("use_advanced_update_button", I, "builds.useAdvancedUpdateButton"),
        new("show_stable_update_button", I, "builds.showStableUpdateButton"),
        new("show_daily_update_button", I, "builds.showDailyUpdateButton"),
        new("show_experimental_update_button", I, "builds.showExperimentalUpdateButton"),
        new("show_bfa_update_button", I, "builds.showBforartistsUpdateButton"),
        new("show_upbge_stable_update_button", I, "builds.showUpbgeStableUpdateButton"),
        new("show_upbge_weekly_update_button", I, "builds.showUpbgeWeeklyUpdateButton"),
        new("update_behavior", I, "builds.updateBehaviour"),
        new("stable_update_behavior", I, "builds.stableUpdateBehaviour"),
        new("daily_update_behavior", I, "builds.dailyUpdateBehaviour"),
        new("experimental_update_behavior", I, "builds.experimentalUpdateBehaviour"),
        new("bfa_update_behavior", I, "builds.bforartistsUpdateBehaviour"),
        new("upbge_stable_update_behavior", I, "builds.upbgeStableUpdateBehaviour"),
        new("upbge_weekly_update_behavior", I, "builds.upbgeWeeklyUpdateBehaviour"),
        new("install_template", I, "builds.installTemplate"),
        new("show_tray_icon", I, "general.minimiseToTray"),
        new("quick_launch_key_seq", I, "launching.quickLaunchHotkey"),
        new("enable_quick_launch_key_seq", I, "launching.quickLaunchHotkeyEnabled"),
        new("use_custom_tls_certificates", I, "connection.useCustomTlsCertificates"),
        new("user_id", I, "connection.userId (a UUID, which upstream generated itself, is not imported)"),
        new("check_for_new_builds_automatically", I, "builds.checkAutomatically"),
        new("new_builds_check_frequency", I, "builds.checkIntervalHours"),
        new("buildcheck_on_startup", I, "builds.checkOnStartup"),
        new("minimum_blender_stable_version", I, "builds.minimumStableVersion (a legacy index is mapped through the API data)"),
        new("scrape_stable_builds", I, "builds.scrapeStable"),
        new("scrape_daily_builds", I, "builds.scrapeDaily"),
        new("scrape_experimental_builds", I, "builds.scrapeExperimental"),
        new("scrape_automated_builds", M, "builds.scrapeDaily and builds.scrapeExperimental when those are absent"),
        new("scrape_bfa_builds", I, "builds.scrapeBforartists"),
        new("scrape_upbge_builds", I, "builds.scrapeUpbge"),
        new("scrape_upbge_weekly_builds", I, "builds.scrapeUpbgeWeekly"),
        new("show_stable_builds", I, "builds.showStable"),
        new("show_daily_builds", I, "builds.showDaily"),
        new("show_experimental_and_patch_builds", I, "builds.showExperimentalAndPatch"),
        new("show_bfa_builds", I, "builds.showBforartists"),
        new("show_upbge_builds", I, "builds.showUpbge"),
        new("show_upbge_weekly_builds", I, "builds.showUpbgeWeekly"),
        new("show_daily_archive_builds", I, "builds.showDailyArchive"),
        new("show_experimental_archive_builds", I, "builds.showExperimentalArchive"),
        new("show_patch_archive_builds", I, "builds.showPatchArchive"),
        new("pr_names_fetch_during_scrape", I, "builds.fetchPrTitles"),
        new("prepend_pr_number_on_label", I, "builds.prependPrNumber"),
        new("worker_thread_count", I, "general.workerThreadCount (0 means automatic)"),
        new("use_pre_release_builds", I, "general.usePreReleaseBuilds"),
        new("log_level", I, "general.logLevel"),
        new("use_system_title_bar", I, "appearance.useSystemTitleBar"),
        new("version_specific_queries", I, "builds.versionSpecificQueries"),
        new("launch_timer", I, "general.launchTimerSeconds"),
        new("purge_temp_on_startup", I, "general.purgeTempOnStartup"),
        new("auto_register_winget", D, "not imported; general.registerInAddRemovePrograms is opt-in"),
        new("github_token", C, "credential github_token"),

        // [Internal]
        new("Internal/quick_launch_paths", M, "builds.defaultBuild (first entry)"),
        new("Internal/favorite_path", M, "builds.defaultBuild (legacy single path)"),
        new("Internal/dont_show_resource_err_again", D, "dropped (no external resources)"),
        new("Internal/last_time_checked_utc", I, "internal.lastTimeChecked"),
        new("Internal/LibraryPage_sorting_type", M, "internal.viewSorts.installed"),
        new("Internal/DownloadsPage_sorting_type", M, "internal.viewSorts.download"),
        new("Internal/FavoritesPage_sorting_type", M, "internal.viewSorts.favorites"),
        new("Internal/tray_icon_notified", I, "internal.trayNoticeShown"),
        new("Internal/global_column_widths", D, "dropped (fixed columns)"),
        new("Internal/window_geometry", D, "dropped (the port stores its own placement)"),
        new("Internal/window_maximized", I, "internal.window.maximised"),

        // [proxy] and keyring
        new("proxy/type", I, "connection.proxyType"),
        new("proxy/host", C, "connection.proxyHost (keyring, then INI)"),
        new("proxy/port", C, "connection.proxyPort (keyring, then INI)"),
        new("proxy/user", C, "credential proxy/user"),
        new("proxy/password", C, "credential proxy/password"),
    ];

    private static readonly Dictionary<string, UpstreamKey> ByKey = All.ToDictionary(k => k.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The entry for <paramref name="key"/>, or null for a key upstream never had.</summary>
    public static UpstreamKey? Find(string key) => ByKey.GetValueOrDefault(key);
}
