using System.Text.Json;
using System.Text.Json.Serialization;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Net;

namespace BlenderLauncher.Core.Settings;

/// <summary>
/// Everything the launcher keeps in <c>settings.json</c>, grouped as the settings window is.
/// Every upstream setting has a counterpart with upstream's default,
/// except those the upstream importer remaps or drops; the doc comment on each property names its upstream key.
/// Secrets (GitHub token, proxy user and password) are not here: they live in <see cref="ICredentialStore"/>.
/// <para>
/// Immutable: change a copy with <c>with</c> through <see cref="SettingsStore.Update"/>. Each group keeps the
/// JSON members it does not know in <c>Extra</c>, so a newer version's fields survive a round trip.
/// </para>
/// </summary>
public sealed record AppSettings
{
    /// <summary>The schema version this build writes. Each bump adds one step to <see cref="SettingsMigrations"/>.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The settings with every default.</summary>
    public static AppSettings Default { get; } = new();

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public GeneralSettings General { get; init; } = new();

    public AppearanceSettings Appearance { get; init; } = new();

    public ConnectionSettings Connection { get; init; } = new();

    public BuildsSettings Builds { get; init; } = new();

    public LaunchingSettings Launching { get; init; } = new();

    public InternalSettings Internal { get; init; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>
    /// Returns a copy with every value inside its allowed range (see <see cref="SettingsValidation"/>): missing
    /// groups restored, numbers clamped, unknown enum values and malformed strings reset to their defaults.
    /// </summary>
    public AppSettings Normalised() => SettingsValidation.Normalise(this);
}

/// <summary>Settings → General.</summary>
public sealed record GeneralSettings
{
    /// <summary>
    /// <c>library_folder</c>: the library root, absolute. Null until chosen (first-run wizard). Upstream also
    /// accepted a path relative to its exe folder; the importer does not guess those.
    /// </summary>
    public string? LibraryFolder { get; init; }

    /// <summary><c>language</c>: <c>auto</c>, <c>en</c>, <c>es</c>, <c>fr</c>, <c>ja</c> or <c>zh</c>. Needs a restart.</summary>
    public string Language { get; init; } = "auto";

    /// <summary><c>show_tray_icon</c>: closing the window hides it to the tray instead of quitting.</summary>
    public bool MinimiseToTray { get; init; }

    /// <summary><c>launch_minimized_to_tray</c>: start hidden (only when <see cref="MinimiseToTray"/> is on).</summary>
    public bool LaunchMinimisedToTray { get; init; }

    /// <summary>
    /// <c>worker_thread_count</c>: task queue workers, at least 1. Null means automatic (upstream's 0 or missing:
    /// three quarters of the cores, see <see cref="EffectiveWorkerThreadCount"/>). Needs a restart.
    /// </summary>
    public int? WorkerThreadCount { get; init; }

    /// <summary><c>use_pre_release_builds</c>: the launcher's own update channel.</summary>
    public bool UsePreReleaseBuilds { get; init; }

    /// <summary><c>launch_timer</c>: launch picker countdown in seconds; -1 disabled, 0 immediate.</summary>
    public int LaunchTimerSeconds { get; init; } = 3;

    /// <summary><c>default_delete_action</c>. Shift inverts it in the context menu.</summary>
    public DeleteAction DefaultDeleteAction { get; init; } = DeleteAction.SendToTrash;

    /// <summary><c>purge_temp_on_startup</c>: empty the port's own staging folder at start.</summary>
    public bool PurgeTempOnStartup { get; init; } = true;

    /// <summary><c>log_level</c>. Needs a restart.</summary>
    public LogLevel LogLevel { get; init; } = LogLevel.Info;

    /// <summary>
    /// Add/Remove Programs registration. Opt-in; upstream's <c>auto_register_winget</c> (default on) is not
    /// imported.
    /// </summary>
    public bool RegisterInAddRemovePrograms { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>
    /// <see cref="WorkerThreadCount"/>, or upstream's automatic value <c>round(max(cpu * 3 / 4, 1))</c> when unset.
    /// Python rounds half to even, as <see cref="MidpointRounding.ToEven"/> does.
    /// </summary>
    public int EffectiveWorkerThreadCount(int? processorCount = null)
    {
        if (WorkerThreadCount is { } count)
        {
            return Math.Max(1, count);
        }

        var cpu = processorCount ?? Environment.ProcessorCount;
        return cpu <= 0 ? 4 : (int)Math.Round(Math.Max(cpu * 3 / 4.0, 1), MidpointRounding.ToEven);
    }
}

/// <summary>Settings → Appearance, plus the design's own options.</summary>
public sealed record AppearanceSettings
{
    /// <summary><c>use_system_title_bar</c>.</summary>
    public bool UseSystemTitleBar { get; init; }

    /// <summary><c>enable_new_builds_notifications</c>.</summary>
    public bool NotifyNewBuilds { get; init; } = true;

    /// <summary><c>enable_download_notifications</c>.</summary>
    public bool NotifyDownloadFinished { get; init; } = true;

    /// <summary><c>error_popup</c>.</summary>
    public bool NotifyErrors { get; init; } = true;

    /// <summary>
    /// The sidebar entry shown at start (<see cref="SidebarView"/> text, for example <c>installed:all</c>). Replaces
    /// upstream's <c>default_tab</c>, <c>default_library_page</c>, <c>default_downloads_page</c> and
    /// <c>sync_library_and_downloads_pages</c>.
    /// </summary>
    public string DefaultView { get; init; } = SidebarView.Default.ToString();

    /// <summary>Design: accent colour.</summary>
    public Accent Accent { get; init; } = Accent.Blue;

    /// <summary>Design: row density.</summary>
    public Density Density { get; init; } = Density.Comfortable;

    /// <summary>Design: whether the detail panel is shown.</summary>
    public bool ShowDetailsPanel { get; init; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// Settings → Connection, without the secrets. Upstream kept <c>proxy/host</c> and <c>proxy/port</c> in the
/// keyring too; they are not secret, so the port keeps them here and only the user, password and token go to
/// <see cref="ICredentialStore"/> (<see cref="CredentialKeys"/>).
/// </summary>
public sealed record ConnectionSettings
{
    /// <summary>
    /// <c>user_id</c>, optional: sent in the User-Agent only when set. Must match <c>^[a-zA-Z0-9-]{8,64}$</c>;
    /// anything else reads as unset. Upstream generated a UUID on first read; the port does not.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary><c>use_custom_tls_certificates</c>. Kept for parity; it has no effect (see <see cref="ConnectionOptions"/>). Needs a restart.</summary>
    public bool UseCustomTlsCertificates { get; init; } = true;

    /// <summary><c>proxy/type</c>. Needs a restart.</summary>
    public ProxyType ProxyType { get; init; } = ProxyType.None;

    /// <summary><c>proxy/host</c> (upstream: keyring). Needs a restart.</summary>
    public string ProxyHost { get; init; } = ConnectionOptions.DefaultProxyHost;

    /// <summary><c>proxy/port</c> (upstream: keyring, as text), 1 to 65535. Needs a restart.</summary>
    public int ProxyPort { get; init; } = ConnectionOptions.DefaultProxyPort;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Settings → Blender Builds, minus the launching group.</summary>
public sealed record BuildsSettings
{
    /// <summary>
    /// The lowest <see cref="CheckIntervalHours"/>. The highest keeps the interval in milliseconds inside a signed
    /// 32-bit timer, as upstream.
    /// </summary>
    public const int MinimumCheckIntervalHours = 6;

    /// <summary>The highest <see cref="CheckIntervalHours"/>.</summary>
    public const int MaximumCheckIntervalHours = 596;

    /// <summary>Upstream's default <c>minimum_blender_stable_version</c>.</summary>
    public const string DefaultMinimumStableVersion = "3.0";

    /// <summary><c>show_stable_builds</c>.</summary>
    public bool ShowStable { get; init; } = true;

    /// <summary><c>show_daily_builds</c>.</summary>
    public bool ShowDaily { get; init; } = true;

    /// <summary><c>show_experimental_and_patch_builds</c>.</summary>
    public bool ShowExperimentalAndPatch { get; init; } = true;

    /// <summary><c>show_bfa_builds</c>.</summary>
    public bool ShowBforartists { get; init; } = true;

    /// <summary><c>show_upbge_builds</c>.</summary>
    public bool ShowUpbge { get; init; } = true;

    /// <summary><c>show_upbge_weekly_builds</c>. Off by default.</summary>
    public bool ShowUpbgeWeekly { get; init; }

    /// <summary><c>scrape_stable_builds</c>.</summary>
    public bool ScrapeStable { get; init; } = true;

    /// <summary><c>scrape_daily_builds</c> (legacy fallback <c>scrape_automated_builds</c>).</summary>
    public bool ScrapeDaily { get; init; } = true;

    /// <summary><c>scrape_experimental_builds</c> (legacy fallback <c>scrape_automated_builds</c>): experimental and patch.</summary>
    public bool ScrapeExperimental { get; init; } = true;

    /// <summary><c>scrape_bfa_builds</c>.</summary>
    public bool ScrapeBforartists { get; init; } = true;

    /// <summary><c>scrape_upbge_builds</c>.</summary>
    public bool ScrapeUpbge { get; init; } = true;

    /// <summary><c>scrape_upbge_weekly_builds</c>. Off by default.</summary>
    public bool ScrapeUpbgeWeekly { get; init; }

    /// <summary><c>show_daily_archive_builds</c>.</summary>
    public bool ShowDailyArchive { get; init; }

    /// <summary><c>show_experimental_archive_builds</c>.</summary>
    public bool ShowExperimentalArchive { get; init; }

    /// <summary><c>show_patch_archive_builds</c>.</summary>
    public bool ShowPatchArchive { get; init; }

    /// <summary><c>minimum_blender_stable_version</c>, <c>"major.minor"</c>.</summary>
    public string MinimumStableVersion { get; init; } = DefaultMinimumStableVersion;

    /// <summary><c>check_for_new_builds_automatically</c>.</summary>
    public bool CheckAutomatically { get; init; }

    /// <summary><c>new_builds_check_frequency</c> in hours, 6 to 596.</summary>
    public int CheckIntervalHours { get; init; } = 12;

    /// <summary><c>buildcheck_on_startup</c>.</summary>
    public bool CheckOnStartup { get; init; } = true;

    /// <summary><c>show_update_button</c>.</summary>
    public bool ShowUpdateButton { get; init; } = true;

    /// <summary><c>update_behavior</c>.</summary>
    public UpdateBehaviour UpdateBehaviour { get; init; } = UpdateBehaviour.Patch;

    /// <summary><c>use_advanced_update_button</c>: per-channel buttons and behaviours below.</summary>
    public bool UseAdvancedUpdateButton { get; init; }

    /// <summary><c>show_stable_update_button</c>.</summary>
    public bool ShowStableUpdateButton { get; init; } = true;

    /// <summary><c>show_daily_update_button</c>.</summary>
    public bool ShowDailyUpdateButton { get; init; } = true;

    /// <summary><c>show_experimental_update_button</c>.</summary>
    public bool ShowExperimentalUpdateButton { get; init; } = true;

    /// <summary><c>show_bfa_update_button</c>.</summary>
    public bool ShowBforartistsUpdateButton { get; init; } = true;

    /// <summary><c>show_upbge_stable_update_button</c>.</summary>
    public bool ShowUpbgeStableUpdateButton { get; init; } = true;

    /// <summary><c>show_upbge_weekly_update_button</c>.</summary>
    public bool ShowUpbgeWeeklyUpdateButton { get; init; } = true;

    /// <summary><c>stable_update_behavior</c>.</summary>
    public UpdateBehaviour StableUpdateBehaviour { get; init; } = UpdateBehaviour.Patch;

    /// <summary><c>daily_update_behavior</c>.</summary>
    public UpdateBehaviour DailyUpdateBehaviour { get; init; } = UpdateBehaviour.Patch;

    /// <summary><c>experimental_update_behavior</c>.</summary>
    public UpdateBehaviour ExperimentalUpdateBehaviour { get; init; } = UpdateBehaviour.Patch;

    /// <summary><c>bfa_update_behavior</c>.</summary>
    public UpdateBehaviour BforartistsUpdateBehaviour { get; init; } = UpdateBehaviour.Patch;

    /// <summary><c>upbge_stable_update_behavior</c>.</summary>
    public UpdateBehaviour UpbgeStableUpdateBehaviour { get; init; } = UpdateBehaviour.Patch;

    /// <summary><c>upbge_weekly_update_behavior</c>.</summary>
    public UpdateBehaviour UpbgeWeeklyUpdateBehaviour { get; init; } = UpdateBehaviour.Patch;

    /// <summary><c>install_template</c>: copy <c>library\template</c> into new builds.</summary>
    public bool InstallTemplate { get; init; }

    /// <summary>
    /// Upstream's <c>mark_as_favorite</c>, under its real meaning: a new download of this channel
    /// becomes the default build. Upstream's UPBGE Weekly entry tested the UPBGE stable branch; the port's
    /// value means UPBGE Weekly.
    /// </summary>
    public NewDownloadDefault MarkNewDownloadsAsDefault { get; init; } = NewDownloadDefault.None;

    /// <summary><c>pr_names_fetch_during_scrape</c>.</summary>
    public bool FetchPrTitles { get; init; }

    /// <summary><c>prepend_pr_number_on_label</c>.</summary>
    public bool PrependPrNumber { get; init; } = true;

    /// <summary>
    /// The default build (replaces upstream's <c>Internal/quick_launch_paths</c>, first entry): the build folder
    /// relative to the library root (for example <c>daily\blender-4.4.0-alpha+main.abc123</c>), or an absolute
    /// path when it lies outside the library. Null when none is chosen.
    /// <para>
    /// Only the import carrier: the authoritative default build is the per-library pointer in
    /// <see cref="Library.LibraryState"/>. On the first library load <see cref="DefaultBuildImport"/>
    /// moves this value there (when the library has none) and clears it.
    /// </para>
    /// </summary>
    public string? DefaultBuild { get; init; }

    /// <summary>
    /// <c>version_specific_queries</c>: blend-file version (<c>"4.2.0"</c>) to a launch query text
    /// (<c>"4.^.^-daily@^"</c>). Typed access through <see cref="VersionSpecificQueries"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> VersionSpecificQueries { get; init; } = new Dictionary<string, string>();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Settings → Blender Builds → Launching Builds.</summary>
public sealed record LaunchingSettings
{
    /// <summary>Upstream's default <c>quick_launch_key_seq</c>.</summary>
    public const string DefaultQuickLaunchHotkey = "alt+f11";

    /// <summary><c>on_blender_launch_action</c>.</summary>
    public OnLaunchAction OnLaunchAction { get; init; } = OnLaunchAction.DoNothing;

    /// <summary><c>launch_blender_no_console</c>: Blender's console window starts hidden.</summary>
    public bool HideConsole { get; init; } = true;

    /// <summary><c>blender_startup_arguments</c>, trimmed. Upstream has no Windows control for it; the port shows one.</summary>
    public string BlenderStartupArguments { get; init; } = "";

    /// <summary><c>bash_arguments</c> (Linux only; kept, hidden on Windows).</summary>
    public string BashArguments { get; init; } = "";

    /// <summary><c>use_nohup</c> (Linux only; kept, hidden on Windows).</summary>
    public bool UseNohup { get; init; }

    /// <summary><c>enable_quick_launch_key_seq</c>.</summary>
    public bool QuickLaunchHotkeyEnabled { get; init; }

    /// <summary><c>quick_launch_key_seq</c>, for example <c>ctrl+alt+b</c>, trimmed and lower-cased.</summary>
    public string QuickLaunchHotkey { get; init; } = DefaultQuickLaunchHotkey;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>State the launcher keeps for itself: not shown in Settings.</summary>
public sealed record InternalSettings
{
    /// <summary><c>first_time_setup_seen</c>.</summary>
    public bool FirstRunSeen { get; init; }

    /// <summary>The upstream settings import has run; it never runs again by itself.</summary>
    public bool UpstreamImportDone { get; init; }

    /// <summary>
    /// A relative upstream <c>library_folder</c> the importer could not resolve (upstream's exe folder is unknown),
    /// kept so the UI can ask the user for the folder instead of guessing. Cleared once dealt with.
    /// </summary>
    public string? UpstreamRelativeLibraryFolder { get; init; }

    /// <summary>
    /// <c>Internal/last_time_checked_utc</c>: the end of the last build check that found anything, with its offset.
    /// Null when there has been none (upstream's epoch).
    /// </summary>
    public DateTimeOffset? LastTimeChecked { get; init; }

    /// <summary><c>Internal/tray_icon_notified</c>: the one-time "still running in the tray" notice was shown.</summary>
    public bool TrayNoticeShown { get; init; }

    /// <summary>
    /// The first-run wizard asked for a Start Menu shortcut. The wizard only records the choice; the shell
    /// integration creates the shortcut and clears the flag.
    /// </summary>
    public bool PendingStartMenuShortcut { get; init; }

    /// <summary>The first-run wizard asked for a Desktop shortcut (created and cleared by the shell integration).</summary>
    public bool PendingDesktopShortcut { get; init; }

    /// <summary>The first-run wizard asked to register for <c>.blend</c> files (done and cleared by the shell integration).</summary>
    public bool PendingFileAssociation { get; init; }

    /// <summary>
    /// The launcher release tag whose update dialog was already shown after a check, so it is offered once
    /// per new version. The status bar's update button still opens it.
    /// </summary>
    public string? DismissedUpdateVersion { get; init; }

    /// <summary>
    /// The main window's placement. Replaces <c>Internal/window_geometry</c> (Qt's byte blob, dropped) and
    /// <c>Internal/window_maximized</c>.
    /// </summary>
    public WindowPlacement Window { get; init; } = new();

    /// <summary>
    /// Sort per view (<see cref="SortViews"/>), replacing <c>Internal/{LibraryPage,DownloadsPage,FavoritesPage}_sorting_type</c>.
    /// A view without an entry uses <see cref="ViewSort.Default"/>.
    /// </summary>
    public IReadOnlyDictionary<string, ViewSort> ViewSorts { get; init; } = new Dictionary<string, ViewSort>();

    /// <summary>Detail panel sections the user collapsed (for example <c>build</c>, <c>install</c>, <c>options</c>, <c>more</c>).</summary>
    public IReadOnlyList<string> CollapsedDetailSections { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>The sort for <paramref name="view"/>, or the default.</summary>
    public ViewSort SortFor(string view) => ViewSorts.TryGetValue(view, out var sort) ? sort : ViewSort.Default;
}

/// <summary>Main window placement in screen coordinates. X and Y are null until first saved (centre on the primary screen).</summary>
public sealed record WindowPlacement
{
    /// <summary>The design's proposed default size.</summary>
    public const int DefaultWidth = 1280;

    public const int DefaultHeight = 800;

    /// <summary>The design's minimum size.</summary>
    public const int MinimumWidth = 960;

    public const int MinimumHeight = 600;

    public int? X { get; init; }

    public int? Y { get; init; }

    public int Width { get; init; } = DefaultWidth;

    public int Height { get; init; } = DefaultHeight;

    public bool Maximised { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A list's sort column and direction.</summary>
public sealed record ViewSort
{
    /// <summary>Newest build first, as upstream's default (<c>sorting_type</c> 1, DATETIME).</summary>
    public static ViewSort Default { get; } = new();

    public SortColumn Column { get; init; } = SortColumn.Built;

    public bool Descending { get; init; } = true;
}

/// <summary>The keys of <see cref="InternalSettings.ViewSorts"/>.</summary>
public static class SortViews
{
    /// <summary>The installed list (upstream <c>LibraryPage</c>).</summary>
    public const string Installed = "installed";

    /// <summary>The download list (upstream <c>DownloadsPage</c>).</summary>
    public const string Download = "download";

    /// <summary>The Favorites entry (upstream <c>FavoritesPage</c>).</summary>
    public const string Favorites = "favorites";
}

/// <summary>Sortable columns in the design.</summary>
public enum SortColumn
{
    /// <summary>The build's commit time ("Built").</summary>
    Built = 0,

    Version = 1,
}

/// <summary><c>default_delete_action</c>; upstream's integers are the same.</summary>
public enum DeleteAction
{
    SendToTrash = 0,
    DeletePermanently = 1,
}

/// <summary><c>on_blender_launch_action</c>; upstream's integers are the same.</summary>
public enum OnLaunchAction
{
    DoNothing = 0,
    Minimise = 1,
    Close = 2,
}

/// <summary>
/// How far an installed build's update button looks. The numbers are the port's own: upstream
/// stores Major 0, Minor 1, Patch 2, which the importer maps by name.
/// </summary>
public enum UpdateBehaviour
{
    Patch = 0,
    Minor = 1,
    Major = 2,
}

/// <summary>Which channel's new downloads become the default build (upstream <c>mark_as_favorite</c>).</summary>
public enum NewDownloadDefault
{
    None = 0,
    Stable = 1,
    Daily = 2,

    /// <summary>Experimental and patch builds.</summary>
    Experimental = 3,
    Bforartists = 4,
    UpbgeStable = 5,
    UpbgeWeekly = 6,
}

/// <summary>Design accent colour.</summary>
public enum Accent
{
    Blue = 0,
    Orange = 1,
}

/// <summary>Design row density.</summary>
public enum Density
{
    Comfortable = 0,
    Compact = 1,
}
