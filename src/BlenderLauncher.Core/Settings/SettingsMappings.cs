using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Settings;

/// <summary>Projections of <see cref="AppSettings"/> onto the option records the services take.</summary>
public static class SettingsMappings
{
    /// <summary>The scrape service's settings.</summary>
    public static ScrapeSettings ToScrapeSettings(this AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var b = settings.Builds;
        return new ScrapeSettings
        {
            ScrapeStable = b.ScrapeStable,
            ScrapeDaily = b.ScrapeDaily,
            ScrapeExperimental = b.ScrapeExperimental,
            ScrapeBforartists = b.ScrapeBforartists,
            ScrapeUpbge = b.ScrapeUpbge,
            ScrapeUpbgeWeekly = b.ScrapeUpbgeWeekly,
            ShowStable = b.ShowStable,
            ShowDaily = b.ShowDaily,
            ShowExperimentalAndPatch = b.ShowExperimentalAndPatch,
            ShowBforartists = b.ShowBforartists,
            ShowUpbge = b.ShowUpbge,
            ShowUpbgeWeekly = b.ShowUpbgeWeekly,
            DailyArchive = b.ShowDailyArchive,
            ExperimentalArchive = b.ShowExperimentalArchive,
            PatchArchive = b.ShowPatchArchive,
            MinimumStableVersion = SemVer.TryParse(b.MinimumStableVersion, out var min, optionalMinorAndPatch: true)
                ? new SemVer(min.Major, min.Minor)
                : ScrapeContext.DefaultMinimumStableVersion,
            FetchPrTitles = b.FetchPrTitles,
            PrependPrNumber = b.PrependPrNumber,
            CheckAutomatically = b.CheckAutomatically,
            CheckIntervalHours = b.CheckIntervalHours,
            CheckOnStartup = b.CheckOnStartup,
            UsePreReleaseBuilds = settings.General.UsePreReleaseBuilds,
        };
    }

    /// <summary>
    /// The HTTP client's options, with the secrets read from <paramref name="credentials"/>. A credential store
    /// failure is logged and leaves that secret unset, so the launcher still starts.
    /// </summary>
    public static ConnectionOptions ToConnectionOptions(this AppSettings settings, ICredentialStore? credentials, Log? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var c = settings.Connection;
        return new ConnectionOptions
        {
            ProxyType = c.ProxyType,
            ProxyHost = c.ProxyHost,
            ProxyPort = c.ProxyPort,
            ProxyUser = Secret(CredentialKeys.ProxyUser),
            ProxyPassword = Secret(CredentialKeys.ProxyPassword),
            GitHubToken = Secret(CredentialKeys.GitHubToken),
            UserId = c.UserId,
            UseCustomTlsCertificates = c.UseCustomTlsCertificates,
        };

        string? Secret(string key)
        {
            if (credentials is null)
            {
                return null;
            }

            try
            {
                var value = credentials.Get(key)?.Trim();
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch (CredentialStoreException e)
            {
                log?.Warning($"Could not read credential '{key}'", e);
                return null;
            }
        }
    }
}
