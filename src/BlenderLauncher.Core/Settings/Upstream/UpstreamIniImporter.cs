using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Net;

namespace BlenderLauncher.Core.Settings.Upstream;

/// <summary>What the importer may read and write besides the INI.</summary>
public sealed record UpstreamImportOptions
{
    /// <summary>Upstream's keyring entries (Credential Manager); null skips them, leaving only INI plain text.</summary>
    public ILegacyCredentialReader? LegacyCredentials { get; init; }

    /// <summary>Where imported secrets go; null means secrets are reported as failed.</summary>
    public ICredentialStore? Credentials { get; init; }

    /// <summary>The API data whose version list maps a legacy integer <c>minimum_blender_stable_version</c>.</summary>
    public ApiData? ApiData { get; init; }

    /// <summary>The local application data folder, for the default INI path; the user's when null.</summary>
    public string? LocalAppData { get; init; }

    public Log? Log { get; init; }
}

/// <summary>
/// Imports upstream Blender Launcher V2's settings: every key in <see cref="UpstreamKeyMap"/> with its
/// mapping, legacy integer dropdown values, the <c>scrape_automated_builds</c> and
/// <c>favorite_path</c> migrations, and upstream's keyring entries. Read only towards upstream: neither the INI
/// nor upstream's credentials are changed.
/// </summary>
public static partial class UpstreamIniImporter
{
    private static readonly string[] LibraryPages = ["stable", "daily", "experimental", "bforartists", "upbge-stable", "upbge-weekly", "custom"];
    private static readonly string[] DownloadsPages = ["stable", "daily", "experimental", "bforartists", "upbge-stable", "upbge-weekly"];
    private static readonly string[] FavoritePages = ["disable", .. DownloadsPages];

    /// <summary>
    /// Runs the import once: does nothing when <see cref="InternalSettings.UpstreamImportDone"/> is set or there is
    /// no INI at <paramref name="iniPath"/> (default <see cref="UpstreamPaths.UserIni"/>); otherwise imports and
    /// sets the flag. The result is logged.
    /// </summary>
    public static Task<UpstreamImportResult> RunOnceAsync(SettingsStore store, UpstreamImportOptions options, string? iniPath = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        var path = iniPath ?? UpstreamPaths.UserIni(options.LocalAppData);
        if (store.Current.Internal.UpstreamImportDone)
        {
            return Task.FromResult(UpstreamImportResult.NotRun(UpstreamImportStatus.AlreadyDone, path));
        }

        if (!File.Exists(path))
        {
            options.Log?.Info($"Upstream settings import: no file at {path}.");
            return Task.FromResult(UpstreamImportResult.NotRun(UpstreamImportStatus.NotFound, path));
        }

        return ImportFileAsync(store, path, options, ct);
    }

    /// <summary>
    /// Imports <paramref name="iniPath"/> whatever the flag says (for example a portable upstream INI picked in the
    /// first-run wizard), applies the result to <paramref name="store"/> and sets the flag.
    /// </summary>
    public static async Task<UpstreamImportResult> ImportFileAsync(SettingsStore store, string iniPath, UpstreamImportOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        QSettingsIni ini;
        try
        {
            ini = QSettingsIni.Parse(await File.ReadAllTextAsync(iniPath, ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            options.Log?.Error($"Upstream settings import: cannot read {iniPath}", e);
            return UpstreamImportResult.NotRun(UpstreamImportStatus.Unreadable, iniPath, e.Message);
        }

        UpstreamImportResult result = null!;
        store.Update(current =>
        {
            (var next, result) = Import(current, ini, iniPath, options);
            return next;
        });
        await store.FlushAsync(ct).ConfigureAwait(false);
        LogResult(result, options.Log);
        return result;
    }

    /// <summary>
    /// The import as a pure function: <paramref name="current"/> with every present upstream key applied and
    /// <see cref="InternalSettings.UpstreamImportDone"/> set, plus the report. Secrets are written to
    /// <see cref="UpstreamImportOptions.Credentials"/>.
    /// </summary>
    public static (AppSettings Settings, UpstreamImportResult Result) Import(AppSettings current, QSettingsIni ini, string? sourcePath, UpstreamImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(ini);
        ArgumentNullException.ThrowIfNull(options);
        var run = new Run(current, ini, options);
        run.Execute();
        var settings = run.Settings with { Internal = run.Settings.Internal with { UpstreamImportDone = true } };
        var result = new UpstreamImportResult(UpstreamImportStatus.Imported, sourcePath, run.Entries)
        {
            RelativeLibraryFolder = run.RelativeLibraryFolder,
        };
        return (settings.Normalised(), result);
    }

    /// <summary>Writes the report to the log: a summary line, then one line per key.</summary>
    public static void LogResult(UpstreamImportResult result, Log? log)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (log is null)
        {
            return;
        }

        log.Info($"Upstream settings import from {result.SourcePath}: {result.Status}; " +
                 $"{result.Imported.Count()} imported, {result.Skipped.Count()} skipped, {result.Failed.Count()} failed.");
        foreach (var entry in result.Entries)
        {
            if (entry.Outcome == ImportOutcome.Failed)
            {
                log.Warning($"  {entry}");
            }
            else
            {
                log.Info($"  {entry}");
            }
        }
    }

    /// <summary>
    /// The default build for an upstream quick-launch path: relative to <paramref name="libraryRoot"/> when it lies
    /// under it, otherwise the absolute path with Windows separators.
    /// </summary>
    public static string ToDefaultBuild(string path, string? libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var windows = path.Trim().Replace('/', '\\');
        if (libraryRoot is null || !Path.IsPathFullyQualified(windows) || !Path.IsPathFullyQualified(libraryRoot))
        {
            return windows;
        }

        var relative = Path.GetRelativePath(Path.GetFullPath(libraryRoot), Path.GetFullPath(windows));
        return relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? Path.GetFullPath(windows)
            : relative;
    }

    // Upstream's uuid4 user IDs: generated, never chosen.
    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UuidPattern();

    private sealed class Run(AppSettings start, QSettingsIni ini, UpstreamImportOptions options)
    {
        private readonly HashSet<string> _handled = new(StringComparer.OrdinalIgnoreCase);

        public AppSettings Settings { get; private set; } = start;

        public List<ImportEntry> Entries { get; } = [];

        public string? RelativeLibraryFolder { get; private set; }

        private GeneralSettings G { get => Settings.General; set => Settings = Settings with { General = value }; }

        private AppearanceSettings A { get => Settings.Appearance; set => Settings = Settings with { Appearance = value }; }

        private ConnectionSettings Cn { get => Settings.Connection; set => Settings = Settings with { Connection = value }; }

        private BuildsSettings B { get => Settings.Builds; set => Settings = Settings with { Builds = value }; }

        private LaunchingSettings L { get => Settings.Launching; set => Settings = Settings with { Launching = value }; }

        private InternalSettings In { get => Settings.Internal; set => Settings = Settings with { Internal = value }; }

        public void Execute()
        {
            LibraryFolder();

            Bool("first_time_setup_seen", v => In = In with { FirstRunSeen = v });
            Bool("launch_minimized_to_tray", v => G = G with { LaunchMinimisedToTray = v });
            Bool("show_tray_icon", v => G = G with { MinimiseToTray = v });
            Bool("use_pre_release_builds", v => G = G with { UsePreReleaseBuilds = v });
            Bool("purge_temp_on_startup", v => G = G with { PurgeTempOnStartup = v });
            Int("launch_timer", v =>
            {
                G = G with { LaunchTimerSeconds = Math.Max(v, SettingsValidation.MinimumLaunchTimer) };
                return v < SettingsValidation.MinimumLaunchTimer ? $"{v} raised to -1 (disabled)" : null;
            });
            Int("worker_thread_count", v =>
            {
                G = G with { WorkerThreadCount = v == 0 ? null : Math.Max(v, 1) };
                return v == 0 ? "0 means automatic" : v < 0 ? $"{v} raised to 1" : null;
            });
            Choice("default_delete_action", ["0", "1"], i => G = G with { DefaultDeleteAction = (DeleteAction)i });
            Language();
            LogLevelKey();
            Dropped("auto_register_winget", "Add/Remove Programs registration is opt-in in the port");

            Bool("use_system_title_bar", v => A = A with { UseSystemTitleBar = v });
            Bool("enable_new_builds_notifications", v => A = A with { NotifyNewBuilds = v });
            Bool("enable_download_notifications", v => A = A with { NotifyDownloadFinished = v });
            Bool("error_popup", v => A = A with { NotifyErrors = v });
            Dropped("dpi_scale_factor", "Avalonia scales per monitor");
            DefaultView();

            UserId();
            Bool("use_custom_tls_certificates", v => Cn = Cn with { UseCustomTlsCertificates = v });
            Choice("proxy/type", ["0", "1", "2", "3", "4"], i => Cn = Cn with { ProxyType = (ProxyType)i });
            ProxyHostAndPort();
            Secret("github_token", CredentialKeys.GitHubToken);
            Secret("proxy/user", CredentialKeys.ProxyUser);
            Secret("proxy/password", CredentialKeys.ProxyPassword);

            Bool("show_stable_builds", v => B = B with { ShowStable = v });
            Bool("show_daily_builds", v => B = B with { ShowDaily = v });
            Bool("show_experimental_and_patch_builds", v => B = B with { ShowExperimentalAndPatch = v });
            Bool("show_bfa_builds", v => B = B with { ShowBforartists = v });
            Bool("show_upbge_builds", v => B = B with { ShowUpbge = v });
            Bool("show_upbge_weekly_builds", v => B = B with { ShowUpbgeWeekly = v });
            Bool("scrape_stable_builds", v => B = B with { ScrapeStable = v });
            ScrapeAutomated();
            Bool("scrape_daily_builds", v => B = B with { ScrapeDaily = v });
            Bool("scrape_experimental_builds", v => B = B with { ScrapeExperimental = v });
            Bool("scrape_bfa_builds", v => B = B with { ScrapeBforartists = v });
            Bool("scrape_upbge_builds", v => B = B with { ScrapeUpbge = v });
            Bool("scrape_upbge_weekly_builds", v => B = B with { ScrapeUpbgeWeekly = v });
            Bool("show_daily_archive_builds", v => B = B with { ShowDailyArchive = v });
            Bool("show_experimental_archive_builds", v => B = B with { ShowExperimentalArchive = v });
            Bool("show_patch_archive_builds", v => B = B with { ShowPatchArchive = v });
            MinimumStableVersion();
            Bool("check_for_new_builds_automatically", v => B = B with { CheckAutomatically = v });
            Int("new_builds_check_frequency", v =>
            {
                var clamped = SettingsValidation.ClampCheckInterval(v);
                B = B with { CheckIntervalHours = clamped };
                return clamped != v ? $"{v} clamped to {clamped}" : null;
            });
            Bool("buildcheck_on_startup", v => B = B with { CheckOnStartup = v });
            Bool("show_update_button", v => B = B with { ShowUpdateButton = v });
            Bool("use_advanced_update_button", v => B = B with { UseAdvancedUpdateButton = v });
            Bool("show_stable_update_button", v => B = B with { ShowStableUpdateButton = v });
            Bool("show_daily_update_button", v => B = B with { ShowDailyUpdateButton = v });
            Bool("show_experimental_update_button", v => B = B with { ShowExperimentalUpdateButton = v });
            Bool("show_bfa_update_button", v => B = B with { ShowBforartistsUpdateButton = v });
            Bool("show_upbge_stable_update_button", v => B = B with { ShowUpbgeStableUpdateButton = v });
            Bool("show_upbge_weekly_update_button", v => B = B with { ShowUpbgeWeeklyUpdateButton = v });
            Behaviour("update_behavior", v => B = B with { UpdateBehaviour = v });
            Behaviour("stable_update_behavior", v => B = B with { StableUpdateBehaviour = v });
            Behaviour("daily_update_behavior", v => B = B with { DailyUpdateBehaviour = v });
            Behaviour("experimental_update_behavior", v => B = B with { ExperimentalUpdateBehaviour = v });
            Behaviour("bfa_update_behavior", v => B = B with { BforartistsUpdateBehaviour = v });
            Behaviour("upbge_stable_update_behavior", v => B = B with { UpbgeStableUpdateBehaviour = v });
            Behaviour("upbge_weekly_update_behavior", v => B = B with { UpbgeWeeklyUpdateBehaviour = v });
            Bool("install_template", v => B = B with { InstallTemplate = v });
            MarkAsFavorite();
            Bool("pr_names_fetch_during_scrape", v => B = B with { FetchPrTitles = v });
            Bool("prepend_pr_number_on_label", v => B = B with { PrependPrNumber = v });
            VersionSpecificQueriesKey();
            DefaultBuild();

            Int("on_blender_launch_action", v =>
            {
                if (v is < 0 or > 2)
                {
                    throw new FormatException($"{v} is not 0, 1 or 2");
                }

                L = L with { OnLaunchAction = (OnLaunchAction)v };
                return null;
            });
            Bool("launch_blender_no_console", v => L = L with { HideConsole = v });
            Text("blender_startup_arguments", v => L = L with { BlenderStartupArguments = v.Trim() });
            Text("bash_arguments", v => L = L with { BashArguments = v.Trim() });
            Bool("use_nohup", v => L = L with { UseNohup = v });
            Bool("enable_quick_launch_key_seq", v => L = L with { QuickLaunchHotkeyEnabled = v });
            Text("quick_launch_key_seq", v =>
            {
                if (string.IsNullOrWhiteSpace(v))
                {
                    throw new FormatException("empty");
                }

                L = L with { QuickLaunchHotkey = v.Trim().ToLowerInvariant() };
            });

            LastTimeChecked();
            Bool("Internal/tray_icon_notified", v => In = In with { TrayNoticeShown = v });
            Bool("Internal/window_maximized", v => In = In with { Window = In.Window with { Maximised = v } });
            Sort("Internal/LibraryPage_sorting_type", SortViews.Installed);
            Sort("Internal/DownloadsPage_sorting_type", SortViews.Download);
            Sort("Internal/FavoritesPage_sorting_type", SortViews.Favorites);
            Dropped("Internal/global_column_widths", "the design has fixed columns");
            Dropped("Internal/window_geometry", "the port stores its own window placement");
            Dropped("Internal/dont_show_resource_err_again", "there are no external resources to miss");

            foreach (var key in ini.Keys)
            {
                if (!_handled.Contains(key))
                {
                    Add(key, ImportOutcome.Skipped, "not an upstream setting the port knows");
                }
            }
        }

        // ---- helpers ----

        private void Add(string key, ImportOutcome outcome, string message)
        {
            _handled.Add(key);
            Entries.Add(new ImportEntry(key, outcome, message));
        }

        private IniValue? Value(string key)
        {
            _handled.Add(key);
            return ini.Get(key);
        }

        private static string Target(string key) => UpstreamKeyMap.Find(key)?.Target ?? "?";

        private void Bool(string key, Action<bool> apply)
        {
            if (Value(key) is not { } value)
            {
                return;
            }

            if (value.AsBool() is { } b)
            {
                apply(b);
                Add(key, ImportOutcome.Imported, $"{(b ? "true" : "false")} → {Target(key)}");
            }
            else
            {
                Add(key, ImportOutcome.Failed, $"not a boolean ({Describe(value)})");
            }
        }

        private void Int(string key, Func<int, string?> apply)
        {
            if (Value(key) is not { } value)
            {
                return;
            }

            if (value.AsInt() is not { } n)
            {
                Add(key, ImportOutcome.Failed, $"not an integer ({Describe(value)})");
                return;
            }

            try
            {
                var note = apply(n);
                Add(key, note is null ? ImportOutcome.Imported : ImportOutcome.Mapped, $"{n} → {Target(key)}{(note is null ? "" : $" ({note})")}");
            }
            catch (FormatException e)
            {
                Add(key, ImportOutcome.Failed, e.Message);
            }
        }

        private void Text(string key, Action<string> apply)
        {
            if (Value(key) is not { } value)
            {
                return;
            }

            if (value.Text is not { } text)
            {
                Add(key, ImportOutcome.Failed, $"not text ({Describe(value)})");
                return;
            }

            try
            {
                apply(text);
                Add(key, ImportOutcome.Imported, $"→ {Target(key)}");
            }
            catch (FormatException e)
            {
                Add(key, ImportOutcome.Failed, e.Message);
            }
        }

        /// <summary>An upstream <c>dropdown_setting</c> whose values are integers: index i is stored as <c>values[i]</c>.</summary>
        private void Choice(string key, string[] values, Action<int> apply)
        {
            if (Value(key) is not { } value)
            {
                return;
            }

            var index = value.Text is { } t ? Array.IndexOf(values, t.Trim()) : -1;
            if (index < 0)
            {
                Add(key, ImportOutcome.Failed, $"not one of {string.Join(", ", values)} ({Describe(value)})");
                return;
            }

            apply(index);
            Add(key, ImportOutcome.Imported, $"{values[index]} → {Target(key)}");
        }

        private void Behaviour(string key, Action<UpdateBehaviour> apply) =>
            Choice(key, ["0", "1", "2"], i => apply(i switch
            {
                0 => UpdateBehaviour.Major,
                1 => UpdateBehaviour.Minor,
                _ => UpdateBehaviour.Patch,
            }));

        private void Dropped(string key, string why)
        {
            if (Value(key) is not null)
            {
                Add(key, ImportOutcome.Dropped, why);
            }
        }

        /// <summary>
        /// Upstream's <c>dropdown_setting</c> getter: a string value is looked up in <paramref name="values"/>,
        /// digits are a legacy index into it. Returns the index, or -1.
        /// </summary>
        private static int DropdownIndex(IniValue value, string[] values)
        {
            var text = value.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return -1;
            }

            if (text.All(char.IsAsciiDigit))
            {
                return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < values.Length ? i : -1;
            }

            return Array.IndexOf(values, text);
        }

        private static string Describe(IniValue value) => value.Kind switch
        {
            IniValueKind.Skipped => "a Qt variant",
            IniValueKind.List => $"a list of {value.Items.Count}",
            _ => $"'{Shorten(value.Items[0])}'",
        };

        private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "…";

        // ---- keys with their own rules ----

        private void LibraryFolder()
        {
            const string key = "library_folder";
            if (Value(key) is not { } value)
            {
                return;
            }

            var text = value.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                Add(key, ImportOutcome.Failed, "empty");
                return;
            }

            var windows = text.Replace('/', '\\');
            if (Path.IsPathFullyQualified(windows))
            {
                G = G with { LibraryFolder = Path.GetFullPath(windows) };
                Add(key, ImportOutcome.Imported, $"'{G.LibraryFolder}' → general.libraryFolder");
            }
            else
            {
                RelativeLibraryFolder = text;
                In = In with { UpstreamRelativeLibraryFolder = text };
                Add(key, ImportOutcome.Skipped,
                    $"'{text}' is relative to upstream's exe folder, which is not known; the user is asked to pick the folder");
            }
        }

        private void Language()
        {
            const string key = "language";
            if (Value(key) is not { } value)
            {
                return;
            }

            var text = value.Text?.Trim();
            if (text is not null && SettingsValidation.Languages.Contains(text))
            {
                G = G with { Language = text };
                Add(key, ImportOutcome.Imported, $"{text} → general.language");
            }
            else
            {
                Add(key, ImportOutcome.Failed, $"unknown language ({Describe(value)})");
            }
        }

        private void LogLevelKey()
        {
            const string key = "log_level";
            if (Value(key) is not { } value)
            {
                return;
            }

            LogLevel? level = value.Text?.Trim() switch
            {
                "DEBUG" => LogLevel.Debug,
                "INFO" => LogLevel.Info,
                "WARNING" => LogLevel.Warning,
                "ERROR" => LogLevel.Error,
                _ => null,
            };
            if (level is { } l)
            {
                G = G with { LogLevel = l };
                Add(key, ImportOutcome.Imported, $"{value.Text!.Trim()} → general.logLevel");
            }
            else
            {
                // Upstream reads an unknown level as INFO, which is the port's default too.
                Add(key, ImportOutcome.Failed, $"not DEBUG, INFO, WARNING or ERROR ({Describe(value)}); INFO kept");
            }
        }

        private void UserId()
        {
            const string key = "user_id";
            if (Value(key) is not { } value)
            {
                return;
            }

            var text = value.Text?.Trim() ?? "";
            if (text.Length == 0)
            {
                Add(key, ImportOutcome.Skipped, "empty");
            }
            else if (UuidPattern().IsMatch(text))
            {
                Add(key, ImportOutcome.Skipped, "a UUID that upstream generated by itself; the port sends a user ID only when one is chosen");
            }
            else if (ConnectionOptions.IsValidUserId(text))
            {
                Cn = Cn with { UserId = text };
                Add(key, ImportOutcome.Imported, "→ connection.userId");
            }
            else
            {
                Add(key, ImportOutcome.Failed, "does not match ^[a-zA-Z0-9-]{8,64}$");
            }
        }

        /// <summary>
        /// <c>default_tab</c>, <c>default_library_page</c>, <c>default_downloads_page</c> and
        /// <c>sync_library_and_downloads_pages</c> collapse into one sidebar entry.
        /// </summary>
        private void DefaultView()
        {
            var tabValue = Value("default_tab");
            var libraryValue = Value("default_library_page");
            var downloadsValue = Value("default_downloads_page");
            var sync = Value("sync_library_and_downloads_pages");
            if (sync is not null)
            {
                Add("sync_library_and_downloads_pages", ImportOutcome.Dropped, "collapsed into appearance.defaultView");
            }

            if (tabValue is null && libraryValue is null && downloadsValue is null)
            {
                return;
            }

            var tab = 0;
            if (tabValue is not null)
            {
                tab = DropdownIndex(tabValue, ["0", "1", "2"]);
                if (tab < 0)
                {
                    Add("default_tab", ImportOutcome.Failed, $"not 0, 1 or 2 ({Describe(tabValue)})");
                    tab = 0;
                }
            }

            SidebarView? view = null;
            switch (tab)
            {
                case 2:
                    view = new SidebarView(SidebarSection.Installed, SidebarView.FavoritesKey);
                    break;
                case 1:
                    var d = downloadsValue is null ? 0 : DropdownIndex(downloadsValue, DownloadsPages);
                    if (d >= 0)
                    {
                        view = new SidebarView(SidebarSection.Download, DownloadsPages[d]);
                    }

                    break;
                default:
                    if (libraryValue is null)
                    {
                        view = SidebarView.Default;
                    }
                    else
                    {
                        var l = DropdownIndex(libraryValue, LibraryPages);
                        if (l >= 0)
                        {
                            view = new SidebarView(SidebarSection.Installed, LibraryPages[l]);
                        }
                    }

                    break;
            }

            var message = view is { } v ? $"→ appearance.defaultView {v}" : null;
            if (view is { } chosen)
            {
                A = A with { DefaultView = chosen.ToString() };
            }

            if (tabValue is not null && _handled.Contains("default_tab") && Entries.All(e => e.Key != "default_tab"))
            {
                Add("default_tab", message is null ? ImportOutcome.Failed : ImportOutcome.Mapped, message ?? "the page it names is unknown");
            }

            Page("default_library_page", libraryValue, LibraryPages, tab == 0 && view is not null);
            Page("default_downloads_page", downloadsValue, DownloadsPages, tab == 1 && view is not null);

            void Page(string key, IniValue? value, string[] pages, bool used)
            {
                if (value is null)
                {
                    return;
                }

                if (DropdownIndex(value, pages) < 0)
                {
                    Add(key, ImportOutcome.Failed, $"unknown page ({Describe(value)})");
                }
                else if (used)
                {
                    Add(key, ImportOutcome.Mapped, message!);
                }
                else
                {
                    Add(key, ImportOutcome.Skipped, "not the default tab, so not the default view");
                }
            }
        }

        private void ProxyHostAndPort()
        {
            if (Find("proxy/host") is { } host)
            {
                Cn = Cn with { ProxyHost = host.Value };
                Add("proxy/host", ImportOutcome.Imported, $"from {Source(host)} → connection.proxyHost");
            }

            if (Find("proxy/port") is { } port)
            {
                if (int.TryParse(port.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 65535)
                {
                    Cn = Cn with { ProxyPort = n };
                    Add("proxy/port", ImportOutcome.Imported, $"{n} from {Source(port)} → connection.proxyPort");
                }
                else
                {
                    Add("proxy/port", ImportOutcome.Failed, $"not a port number ('{Shorten(port.Value)}' from {Source(port)})");
                }
            }
        }

        private void Secret(string upstreamKey, string credentialKey)
        {
            if (Find(upstreamKey) is not { } secret)
            {
                return;
            }

            if (options.Credentials is not { } store)
            {
                Add(upstreamKey, ImportOutcome.Failed, $"found in {Source(secret)}, but there is no credential store");
                return;
            }

            try
            {
                if (!string.IsNullOrEmpty(store.Get(credentialKey)))
                {
                    Add(upstreamKey, ImportOutcome.Skipped, $"found in {Source(secret)}, but the port already has one");
                    return;
                }

                store.Set(credentialKey, secret.Value);
                Add(upstreamKey, ImportOutcome.Imported, $"from {Source(secret)} → credential {credentialKey}");
            }
            catch (CredentialStoreException e)
            {
                Add(upstreamKey, ImportOutcome.Failed, $"found in {Source(secret)}, but storing it failed: {e.Message}");
            }
        }

        private UpstreamSecret? Find(string key)
        {
            _handled.Add(key);
            var secret = UpstreamCredentialLookup.Find(key, options.LegacyCredentials, ini, out var error);
            if (error is not null && secret is null)
            {
                Add(key, ImportOutcome.Failed, $"reading upstream's keyring failed: {error}");
            }

            return secret;
        }

        private static string Source(UpstreamSecret secret) => secret.Source switch
        {
            UpstreamSecretSource.Keyring => "upstream's keyring",
            UpstreamSecretSource.KeyringCompound => "upstream's keyring (compound target)",
            _ => "the INI (plain text)",
        };

        /// <summary>Upstream's fallback: <c>scrape_daily_builds</c> and <c>scrape_experimental_builds</c>, when absent, follow <c>scrape_automated_builds</c>.</summary>
        private void ScrapeAutomated()
        {
            const string key = "scrape_automated_builds";
            if (Value(key) is not { } value)
            {
                return;
            }

            if (value.AsBool() is not { } b)
            {
                Add(key, ImportOutcome.Failed, $"not a boolean ({Describe(value)})");
                return;
            }

            var targets = new List<string>();
            if (!ini.Contains("scrape_daily_builds"))
            {
                B = B with { ScrapeDaily = b };
                targets.Add("builds.scrapeDaily");
            }

            if (!ini.Contains("scrape_experimental_builds"))
            {
                B = B with { ScrapeExperimental = b };
                targets.Add("builds.scrapeExperimental");
            }

            Add(key, targets.Count == 0 ? ImportOutcome.Skipped : ImportOutcome.Mapped,
                targets.Count == 0 ? "superseded by scrape_daily_builds and scrape_experimental_builds" : $"{(b ? "true" : "false")} → {string.Join(", ", targets)}");
        }

        private void MinimumStableVersion()
        {
            const string key = "minimum_blender_stable_version";
            if (Value(key) is not { } value)
            {
                return;
            }

            var text = value.Text?.Trim() ?? "";
            if (SettingsValidation.NormaliseMinimumStableVersion(text) is { } version)
            {
                B = B with { MinimumStableVersion = version };
                Add(key, text == "None" ? ImportOutcome.Mapped : ImportOutcome.Imported, $"{version} → builds.minimumStableVersion");
                return;
            }

            // Legacy: an index into the API data's version list (upstream's dropdown_blender_version).
            if (text.Length > 0 && text.All(char.IsAsciiDigit) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                var series = options.ApiData?.Series;
                if (series is not null && index < series.Count)
                {
                    var mapped = series[index].ToString();
                    B = B with { MinimumStableVersion = mapped };
                    Add(key, ImportOutcome.Mapped, $"legacy index {index} → {mapped}");
                }
                else
                {
                    Add(key, ImportOutcome.Failed, $"legacy index {index} is not in the API data's version list");
                }

                return;
            }

            Add(key, ImportOutcome.Failed, $"not a version ({Describe(value)})");
        }

        /// <summary>
        /// <c>mark_as_favorite</c> becomes "mark new downloads of channel X as default". Upstream's check for
        /// UPBGE Weekly tested the UPBGE stable branch; the port's value is UPBGE Weekly, fixing it.
        /// </summary>
        private void MarkAsFavorite()
        {
            const string key = "mark_as_favorite";
            if (Value(key) is not { } value)
            {
                return;
            }

            var index = DropdownIndex(value, FavoritePages);
            if (index < 0)
            {
                Add(key, ImportOutcome.Failed, $"unknown channel ({Describe(value)})");
                return;
            }

            var channel = (NewDownloadDefault)index;
            B = B with { MarkNewDownloadsAsDefault = channel };
            Add(key, ImportOutcome.Mapped, $"{FavoritePages[index]} → builds.markNewDownloadsAsDefault {channel}" +
                                           (channel == NewDownloadDefault.UpbgeWeekly ? " (upstream applied this to UPBGE stable by mistake)" : ""));
        }

        private void VersionSpecificQueriesKey()
        {
            const string key = "version_specific_queries";
            if (Value(key) is not { } value)
            {
                return;
            }

            if (value.Text is not { } text)
            {
                Add(key, ImportOutcome.Failed, $"not text ({Describe(value)})");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    Add(key, ImportOutcome.Failed, "not a JSON object");
                    return;
                }

                var queries = new Dictionary<string, string>(StringComparer.Ordinal);
                var ignored = 0;
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        queries[p.Name] = p.Value.GetString()!;
                    }
                    else
                    {
                        ignored++;
                    }
                }

                B = B with { VersionSpecificQueries = queries };
                Add(key, ImportOutcome.Imported, $"{queries.Count} queries → builds.versionSpecificQueries" + (ignored > 0 ? $" ({ignored} non-text entries ignored)" : ""));
            }
            catch (JsonException e)
            {
                Add(key, ImportOutcome.Failed, $"not valid JSON: {e.Message}");
            }
        }

        /// <summary>
        /// <c>Internal/quick_launch_paths</c> (first entry) or, when that is empty or not a JSON list, the legacy
        /// <c>Internal/favorite_path</c>, as upstream's <c>get_quick_launch_paths</c>.
        /// </summary>
        private void DefaultBuild()
        {
            const string listKey = "Internal/quick_launch_paths";
            const string legacyKey = "Internal/favorite_path";
            var list = Value(listKey);
            var legacy = Value(legacyKey);
            List<string>? paths = null;
            if (list?.Text is { Length: > 0 } json)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        paths = doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
                    }
                }
                catch (JsonException)
                {
                }

                if (paths is null)
                {
                    Add(listKey, ImportOutcome.Failed, "not a JSON list of paths");
                }
            }

            var root = G.LibraryFolder;
            if (paths is not null)
            {
                var first = paths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                if (first is null)
                {
                    Add(listKey, ImportOutcome.Skipped, "empty list");
                }
                else
                {
                    B = B with { DefaultBuild = ToDefaultBuild(first, root) };
                    Add(listKey, ImportOutcome.Mapped, $"first of {paths.Count} → builds.defaultBuild '{B.DefaultBuild}'" + (paths.Count > 1 ? "; the rest are ignored" : ""));
                }

                if (legacy is not null)
                {
                    Add(legacyKey, ImportOutcome.Skipped, "superseded by Internal/quick_launch_paths");
                }

                return;
            }

            if (legacy is null)
            {
                return;
            }

            if (legacy.Text?.Trim() is { Length: > 0 } path)
            {
                B = B with { DefaultBuild = ToDefaultBuild(path, root) };
                Add(legacyKey, ImportOutcome.Mapped, $"→ builds.defaultBuild '{B.DefaultBuild}'");
            }
            else
            {
                Add(legacyKey, ImportOutcome.Skipped, "empty");
            }
        }

        private void LastTimeChecked()
        {
            const string key = "Internal/last_time_checked_utc";
            if (Value(key) is not { } value)
            {
                return;
            }

            if (!IsoTimestamp.TryParse(value.Text?.Trim(), out var stamp))
            {
                Add(key, ImportOutcome.Failed, $"not an ISO time ({Describe(value)})");
                return;
            }

            var time = stamp.ToDateTimeOffset();
            if (time <= DateTimeOffset.UnixEpoch)
            {
                Add(key, ImportOutcome.Skipped, "the epoch: never checked");
                return;
            }

            In = In with { LastTimeChecked = time };
            Add(key, ImportOutcome.Imported, $"{time:yyyy-MM-dd'T'HH:mm:sszzz} → internal.lastTimeChecked");
        }

        /// <summary>Upstream's sorting types: 1 DATETIME → Built, 2 VERSION and 3 LABEL → Version (the design has no label column).</summary>
        private void Sort(string key, string view)
        {
            Int(key, n =>
            {
                var column = n switch
                {
                    1 => SortColumn.Built,
                    2 or 3 => SortColumn.Version,
                    _ => throw new FormatException($"{n} is not 1, 2 or 3"),
                };
                In = In with { ViewSorts = new Dictionary<string, ViewSort>(In.ViewSorts) { [view] = new ViewSort { Column = column } } };
                return $"internal.viewSorts.{view} = {column}";
            });
        }
    }
}
