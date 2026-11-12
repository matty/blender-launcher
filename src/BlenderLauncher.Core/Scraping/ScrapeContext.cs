using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Scraping;

/// <summary>
/// Everything a source needs for one scrape run: the HTTP client, the LTS table, the platform, the scrape settings,
/// a cache folder, a log and a clock. Built by the scrape service per run from the current settings, so a source
/// reads settings only from here. Immutable; tests build one with <c>with</c>.
/// </summary>
public sealed record ScrapeContext
{
    /// <summary>The upstream default for <c>minimum_blender_stable_version</c> ("3.0").</summary>
    public static readonly SemVer DefaultMinimumStableVersion = new(3, 0, 0);

    /// <summary>The shared client (<c>HttpClientProvider.Client</c>, or a recorded-response client in tests).</summary>
    public required HttpClient Http { get; init; }

    /// <summary>Folder for the source's own caches (stable and Bforartists index caches, PR titles). Created by the caller.</summary>
    public required string CacheDirectory { get; init; }

    /// <summary>The current LTS table (an <see cref="ApiData"/>); queried at use, never copied.</summary>
    public ILtsLookup Lts { get; init; } = ApiData.Empty;

    /// <summary>The OS builds are scraped for.</summary>
    public HostOs Os { get; init; } = BuildPlatform.CurrentOs;

    /// <summary>The machine name in upstream's spelling (<c>amd64</c>, <c>arm64</c> on Windows); see <see cref="BuildPlatform.MachineName"/>.</summary>
    public string Machine { get; init; } = BuildPlatform.CurrentMachine;

    /// <summary>Setting <c>minimum_blender_stable_version</c> as major.minor (patch 0): stable series below it are not scraped.</summary>
    public SemVer MinimumStableVersion { get; init; } = DefaultMinimumStableVersion;

    /// <summary>Setting <c>show_daily_archive_builds</c>: scrape <c>daily/archive</c> instead of <c>daily</c>.</summary>
    public bool DailyArchive { get; init; }

    /// <summary>Setting <c>show_experimental_archive_builds</c>: scrape <c>experimental/archive</c>.</summary>
    public bool ExperimentalArchive { get; init; }

    /// <summary>Setting <c>show_patch_archive_builds</c>: scrape <c>patch/archive</c>.</summary>
    public bool PatchArchive { get; init; }

    /// <summary>Setting <c>pr_names_fetch_during_scrape</c>: look up pull-request titles while scraping patch builds.</summary>
    public bool FetchPrTitles { get; init; }

    /// <summary>Setting <c>prepend_pr_number_on_label</c>: a fetched title reads <c>#123: Title</c> rather than <c>Title</c>. Upstream's default is on.</summary>
    public bool PrependPrNumber { get; init; } = true;

    /// <summary>The <c>--build-cache</c> command-line flag: rebuild the stable cache from scratch.</summary>
    public bool RebuildStableCache { get; init; }

    /// <summary>Null when logging is off.</summary>
    public Log? Log { get; init; }

    /// <summary>Clock for "now" (cache ages, error times); tests pass a fake.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Whether the archive branch of builder channel <paramref name="channel"/> is selected; false for every other channel.</summary>
    public bool IncludesArchive(Channel channel) => channel switch
    {
        Channel.Daily => DailyArchive,
        Channel.Experimental => ExperimentalArchive,
        Channel.Patch => PatchArchive,
        _ => false,
    };
}
