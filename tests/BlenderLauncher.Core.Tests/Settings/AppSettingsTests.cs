using System.Text.Json.Nodes;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Tests.Settings;

public class AppSettingsTests
{
    [Fact]
    public void Defaults_match_upstream_and_the_design()
    {
        var s = AppSettings.Default;
        Assert.Equal(1, s.SchemaVersion);

        Assert.Null(s.General.LibraryFolder);
        Assert.Equal("auto", s.General.Language);
        Assert.False(s.General.MinimiseToTray);
        Assert.False(s.General.LaunchMinimisedToTray);
        Assert.Null(s.General.WorkerThreadCount);
        Assert.False(s.General.UsePreReleaseBuilds);
        Assert.Equal(3, s.General.LaunchTimerSeconds);
        Assert.Equal(DeleteAction.SendToTrash, s.General.DefaultDeleteAction);
        Assert.True(s.General.PurgeTempOnStartup);
        Assert.Equal(LogLevel.Info, s.General.LogLevel);
        Assert.False(s.General.RegisterInAddRemovePrograms);

        Assert.False(s.Appearance.UseSystemTitleBar);
        Assert.True(s.Appearance.NotifyNewBuilds && s.Appearance.NotifyDownloadFinished && s.Appearance.NotifyErrors);
        Assert.Equal("installed:all", s.Appearance.DefaultView);
        Assert.Equal(Accent.Blue, s.Appearance.Accent);
        Assert.Equal(Density.Comfortable, s.Appearance.Density);
        Assert.True(s.Appearance.ShowDetailsPanel);

        Assert.Null(s.Connection.UserId);
        Assert.True(s.Connection.UseCustomTlsCertificates);
        Assert.Equal(ProxyType.None, s.Connection.ProxyType);
        Assert.Equal("255.255.255.255", s.Connection.ProxyHost);
        Assert.Equal(9999, s.Connection.ProxyPort);

        var b = s.Builds;
        Assert.True(b.ShowStable && b.ShowDaily && b.ShowExperimentalAndPatch && b.ShowBforartists && b.ShowUpbge);
        Assert.False(b.ShowUpbgeWeekly);
        Assert.True(b.ScrapeStable && b.ScrapeDaily && b.ScrapeExperimental && b.ScrapeBforartists && b.ScrapeUpbge);
        Assert.False(b.ScrapeUpbgeWeekly);
        Assert.False(b.ShowDailyArchive || b.ShowExperimentalArchive || b.ShowPatchArchive);
        Assert.Equal("3.0", b.MinimumStableVersion);
        Assert.False(b.CheckAutomatically);
        Assert.Equal(12, b.CheckIntervalHours);
        Assert.True(b.CheckOnStartup);
        Assert.True(b.ShowUpdateButton);
        Assert.False(b.UseAdvancedUpdateButton);
        Assert.True(b.ShowStableUpdateButton && b.ShowDailyUpdateButton && b.ShowExperimentalUpdateButton
                    && b.ShowBforartistsUpdateButton && b.ShowUpbgeStableUpdateButton && b.ShowUpbgeWeeklyUpdateButton);
        Assert.All(
            [b.UpdateBehaviour, b.StableUpdateBehaviour, b.DailyUpdateBehaviour, b.ExperimentalUpdateBehaviour,
             b.BforartistsUpdateBehaviour, b.UpbgeStableUpdateBehaviour, b.UpbgeWeeklyUpdateBehaviour],
            v => Assert.Equal(UpdateBehaviour.Patch, v));
        Assert.False(b.InstallTemplate);
        Assert.Equal(NewDownloadDefault.None, b.MarkNewDownloadsAsDefault);
        Assert.False(b.FetchPrTitles);
        Assert.True(b.PrependPrNumber);
        Assert.Null(b.DefaultBuild);
        Assert.Empty(b.VersionSpecificQueries);

        var l = s.Launching;
        Assert.Equal(OnLaunchAction.DoNothing, l.OnLaunchAction);
        Assert.True(l.HideConsole);
        Assert.Equal("", l.BlenderStartupArguments);
        Assert.Equal("", l.BashArguments);
        Assert.False(l.UseNohup);
        Assert.False(l.QuickLaunchHotkeyEnabled);
        Assert.Equal("alt+f11", l.QuickLaunchHotkey);

        var i = s.Internal;
        Assert.False(i.FirstRunSeen || i.UpstreamImportDone || i.TrayNoticeShown);
        Assert.Null(i.LastTimeChecked);
        Assert.Null(i.Window.X);
        Assert.Equal((1280, 800, false), (i.Window.Width, i.Window.Height, i.Window.Maximised));
        Assert.Equal(ViewSort.Default, i.SortFor(SortViews.Installed));
        Assert.Equal(SortColumn.Built, ViewSort.Default.Column);
        Assert.True(ViewSort.Default.Descending);
        Assert.Empty(i.CollapsedDetailSections);
    }

    [Fact]
    public void Defaults_are_already_normalised()
    {
        Assert.Empty(SettingsStore.Diff(AppSettings.Default, AppSettings.Default.Normalised()));
    }

    [Fact]
    public void Json_uses_camel_case_groups_and_enum_names_and_no_secrets()
    {
        var json = JsonNode.Parse(SettingsStore.Serialise(AppSettings.Default))!.AsObject();
        Assert.Equal(["schemaVersion", "general", "appearance", "connection", "builds", "launching", "internal"], json.Select(p => p.Key));
        Assert.Equal("sendToTrash", (string?)json["general"]!["defaultDeleteAction"]);
        Assert.Equal("info", (string?)json["general"]!["logLevel"]);
        Assert.Equal("none", (string?)json["connection"]!["proxyType"]);
        var text = json.ToJsonString();
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(6, 4)]
    [InlineData(10, 8)]
    [InlineData(0, 4)]
    public void Automatic_worker_count_is_three_quarters_of_the_cores(int cpu, int expected) =>
        Assert.Equal(expected, new GeneralSettings().EffectiveWorkerThreadCount(cpu));

    [Fact]
    public void Explicit_worker_count_wins() => Assert.Equal(5, new GeneralSettings { WorkerThreadCount = 5 }.EffectiveWorkerThreadCount(64));

    [Theory]
    [InlineData("installed:all", true)]
    [InlineData("download:upbge-weekly", true)]
    [InlineData("download:all", false)]
    [InlineData("library:stable", false)]
    [InlineData("installed:", false)]
    [InlineData("installed:Stable", false)]
    [InlineData("stable", false)]
    public void Sidebar_view_parse(string text, bool valid)
    {
        Assert.Equal(valid, SidebarView.TryParse(text, out var view));
        if (valid)
        {
            Assert.Equal(text, view!.Value.ToString());
        }
    }

    [Theory]
    [InlineData("4.2.1", "4.2")]
    [InlineData(" 3.6 ", "3.6")]
    [InlineData("None", "3.0")]
    [InlineData("4", null)]
    [InlineData("x.y", null)]
    public void Minimum_stable_version_normalisation(string text, string? expected) =>
        Assert.Equal(expected, SettingsValidation.NormaliseMinimumStableVersion(text));

    // ---- mappings ----

    [Fact]
    public void Maps_onto_scrape_settings()
    {
        var s = AppSettings.Default with
        {
            General = new GeneralSettings { UsePreReleaseBuilds = true },
            Builds = new BuildsSettings
            {
                ScrapeStable = false, ScrapeDaily = false, ScrapeExperimental = false, ScrapeBforartists = false, ScrapeUpbge = false, ScrapeUpbgeWeekly = true,
                ShowStable = false, ShowDaily = false, ShowExperimentalAndPatch = false, ShowBforartists = false, ShowUpbge = false, ShowUpbgeWeekly = true,
                ShowDailyArchive = true, ShowExperimentalArchive = true, ShowPatchArchive = true,
                MinimumStableVersion = "4.1", FetchPrTitles = true, PrependPrNumber = false,
                CheckAutomatically = true, CheckIntervalHours = 24, CheckOnStartup = false,
            },
        };
        var m = s.ToScrapeSettings();
        Assert.Equal(new ScrapeSettings
        {
            ScrapeStable = false, ScrapeDaily = false, ScrapeExperimental = false, ScrapeBforartists = false, ScrapeUpbge = false, ScrapeUpbgeWeekly = true,
            ShowStable = false, ShowDaily = false, ShowExperimentalAndPatch = false, ShowBforartists = false, ShowUpbge = false, ShowUpbgeWeekly = true,
            DailyArchive = true, ExperimentalArchive = true, PatchArchive = true,
            MinimumStableVersion = new SemVer(4, 1), FetchPrTitles = true, PrependPrNumber = false,
            CheckAutomatically = true, CheckIntervalHours = 24, CheckOnStartup = false, UsePreReleaseBuilds = true,
        }, m);
    }

    [Fact]
    public void Default_settings_map_to_default_scrape_settings() =>
        Assert.Equal(ScrapeSettings.Default, AppSettings.Default.ToScrapeSettings());

    [Fact]
    public void Maps_onto_connection_options_with_secrets()
    {
        var creds = new InMemoryCredentialStore();
        creds.Set(CredentialKeys.GitHubToken, " ghp_x ");
        creds.Set(CredentialKeys.ProxyUser, "u");
        creds.Set(CredentialKeys.ProxyPassword, "p");
        var s = AppSettings.Default with
        {
            Connection = new ConnectionSettings { ProxyType = ProxyType.Https, ProxyHost = "proxy", ProxyPort = 8443, UserId = "abcdefgh", UseCustomTlsCertificates = false },
        };
        Assert.Equal(new ConnectionOptions
        {
            ProxyType = ProxyType.Https, ProxyHost = "proxy", ProxyPort = 8443, ProxyUser = "u", ProxyPassword = "p",
            GitHubToken = "ghp_x", UserId = "abcdefgh", UseCustomTlsCertificates = false,
        }, s.ToConnectionOptions(creds));
        Assert.Equal(ConnectionOptions.Default, AppSettings.Default.ToConnectionOptions(null));
    }

    [Fact]
    public void Credential_failure_leaves_secrets_unset()
    {
        var o = AppSettings.Default.ToConnectionOptions(new ThrowingCredentialStore());
        Assert.Null(o.GitHubToken);
        Assert.Null(o.ProxyPassword);
    }

    [Fact]
    public void In_memory_credential_store_deletes_on_blank()
    {
        var c = new InMemoryCredentialStore();
        c.Set("k", "v");
        c.Set("k", "  ");
        Assert.Null(c.Get("k"));
        Assert.False(c.Delete("k"));
    }

    // ---- version_specific_queries (port of tests/backend/test_complicated_config_options.py) ----

    [Fact]
    public async Task Saving_version_specific_queries()
    {
        var queries = new Dictionary<SemVer, VersionSearchQuery>
        {
            [new SemVer(4, 2, 0)] = VersionSearchQuery.Version(4, "^", "^") with { Branch = ["daily"] },
            [new SemVer(2, 80, 0)] = VersionSearchQuery.Version(2, "^", "^") with { CommitTime = VersionSearchTime.Newest },
        };

        using var temp = new TempFolder();
        var options = new SettingsStoreOptions { FilePath = Path.Combine(temp.Path, "settings.json") };
        await using (var store = await SettingsStore.LoadAsync(options, TestContext.Current.CancellationToken))
        {
            store.Update(s => s with { Builds = VersionSpecificQueries.With(s.Builds, queries) });
        }

        await using var reloaded = await SettingsStore.LoadAsync(options, TestContext.Current.CancellationToken);
        var retrieved = VersionSpecificQueries.Get(reloaded.Current.Builds);

        Assert.Equal(queries.Keys.Order(), retrieved.Keys.Order());
        foreach (var (k, v) in queries)
        {
            Assert.Equal(v.GetHashCode(), retrieved[k].GetHashCode());
            Assert.Equal(v, retrieved[k]);
        }
    }

    [Fact]
    public void Version_specific_queries_skip_bad_entries_and_add()
    {
        var b = new BuildsSettings { VersionSpecificQueries = new Dictionary<string, string> { ["4.2.0"] = "4.^.^@^", ["bad"] = "4.^.^", ["4.3.0"] = "???" } };
        Assert.Single(VersionSpecificQueries.Get(b));
        var added = VersionSpecificQueries.WithQuery(b, new SemVer(5, 0, 0), VersionSearchQuery.Default());
        Assert.Equal("^.^.^@^", added.VersionSpecificQueries["5.0.0"]);
        Assert.Equal(3, b.VersionSpecificQueries.Count);
    }

    private sealed class ThrowingCredentialStore : ICredentialStore
    {
        public string? Get(string key) => throw new CredentialStoreException("boom");

        public void Set(string key, string value) => throw new CredentialStoreException("boom");

        public bool Delete(string key) => throw new CredentialStoreException("boom");
    }
}
