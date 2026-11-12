using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Scraping;

/// <summary>
/// The settings the scrape service reads, one property per upstream setting.
/// The app builds it from <see cref="Settings.AppSettings"/> with
/// <see cref="Settings.SettingsMappings.ToScrapeSettings"/>; constructing it directly is for tests and developer tools.
/// Defaults are upstream's. Immutable; the service reads a fresh copy at each check.
/// </summary>
public sealed record ScrapeSettings
{
    /// <summary>The lowest <c>new_builds_check_frequency</c>, in hours.</summary>
    public const int MinimumIntervalHours = 6;

    /// <summary>The highest <c>new_builds_check_frequency</c>, in hours.</summary>
    public const int MaximumIntervalHours = 596;

    /// <summary>Upstream's default <c>new_builds_check_frequency</c>, in hours.</summary>
    public const int DefaultIntervalHours = 12;

    private readonly int _checkIntervalHours = DefaultIntervalHours;

    /// <summary>Upstream's defaults.</summary>
    public static ScrapeSettings Default { get; } = new();

    /// <summary><c>scrape_stable_builds</c>: the stable source.</summary>
    public bool ScrapeStable { get; init; } = true;

    /// <summary><c>scrape_daily_builds</c>: the daily source.</summary>
    public bool ScrapeDaily { get; init; } = true;

    /// <summary><c>scrape_experimental_builds</c>: the experimental <b>and</b> patch sources.</summary>
    public bool ScrapeExperimental { get; init; } = true;

    /// <summary><c>scrape_bfa_builds</c>: the Bforartists source.</summary>
    public bool ScrapeBforartists { get; init; } = true;

    /// <summary><c>scrape_upbge_builds</c>: the UPBGE stable source.</summary>
    public bool ScrapeUpbge { get; init; } = true;

    /// <summary><c>scrape_upbge_weekly_builds</c>: the UPBGE weekly source. Off by default.</summary>
    public bool ScrapeUpbgeWeekly { get; init; }

    /// <summary><c>show_stable_builds</c>: the channel is visible, so a forced check (Shift+Check) scrapes it.</summary>
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

    /// <summary><c>show_daily_archive_builds</c>.</summary>
    public bool DailyArchive { get; init; }

    /// <summary><c>show_experimental_archive_builds</c>.</summary>
    public bool ExperimentalArchive { get; init; }

    /// <summary><c>show_patch_archive_builds</c>.</summary>
    public bool PatchArchive { get; init; }

    /// <summary><c>minimum_blender_stable_version</c> as major.minor (patch 0).</summary>
    public SemVer MinimumStableVersion { get; init; } = ScrapeContext.DefaultMinimumStableVersion;

    /// <summary><c>pr_names_fetch_during_scrape</c>. Off by default.</summary>
    public bool FetchPrTitles { get; init; }

    /// <summary><c>prepend_pr_number_on_label</c>.</summary>
    public bool PrependPrNumber { get; init; } = true;

    /// <summary><c>check_for_new_builds_automatically</c>: re-check every <see cref="CheckIntervalHours"/>. Off by default.</summary>
    public bool CheckAutomatically { get; init; }

    /// <summary>
    /// <c>new_builds_check_frequency</c> in hours, clamped to <see cref="MinimumIntervalHours"/>..<see cref="MaximumIntervalHours"/>
    /// when set (upstream rewrote out-of-range values the same way).
    /// </summary>
    public int CheckIntervalHours
    {
        get => _checkIntervalHours;
        init => _checkIntervalHours = ClampIntervalHours(value);
    }

    /// <summary><see cref="CheckIntervalHours"/> as a time span.</summary>
    public TimeSpan CheckInterval => TimeSpan.FromHours(CheckIntervalHours);

    /// <summary><c>buildcheck_on_startup</c>: check when the service starts.</summary>
    public bool CheckOnStartup { get; init; } = true;

    /// <summary><c>use_pre_release_builds</c>: the launcher's own update channel.</summary>
    public bool UsePreReleaseBuilds { get; init; }

    /// <summary>The <c>--build-cache</c> command-line flag: rebuild the stable cache from scratch.</summary>
    public bool RebuildStableCache { get; init; }

    /// <summary>Clamps an interval to 6..596 hours.</summary>
    public static int ClampIntervalHours(int hours) => Math.Clamp(hours, MinimumIntervalHours, MaximumIntervalHours);

    /// <summary>
    /// Whether source <paramref name="sourceId"/> (one of <see cref="BuildSourceIds"/>) runs: its <c>scrape_*</c>
    /// setting, or, for a forced check (upstream's Shift+Check, <c>window.py:675-709</c>), also its <c>show_*</c>
    /// setting. Unknown ids never run.
    /// </summary>
    public bool IsEnabled(string sourceId, bool forced) => sourceId switch
    {
        BuildSourceIds.Stable => ScrapeStable || (forced && ShowStable),
        BuildSourceIds.Daily => ScrapeDaily || (forced && ShowDaily),
        BuildSourceIds.Experimental or BuildSourceIds.Patch => ScrapeExperimental || (forced && ShowExperimentalAndPatch),
        BuildSourceIds.Bforartists => ScrapeBforartists || (forced && ShowBforartists),
        BuildSourceIds.UpbgeStable => ScrapeUpbge || (forced && ShowUpbge),
        BuildSourceIds.UpbgeWeekly => ScrapeUpbgeWeekly || (forced && ShowUpbgeWeekly),
        _ => false,
    };
}
