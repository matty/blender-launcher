using System.Text.Json.Nodes;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Core.Tests.Scraping;

namespace BlenderLauncher.Core.Tests.Settings;

public class SettingsStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SettingsStoreOptions Options(TempFolder temp, ManualTimeProvider? time = null) => new()
    {
        FilePath = Path.Combine(temp.Path, "settings.json"),
        Time = time ?? new ManualTimeProvider(DateTimeOffset.UnixEpoch),
        SaveDelay = TimeSpan.FromMilliseconds(500),
    };

    [Fact]
    public async Task Missing_file_gives_defaults_and_writes_nothing()
    {
        using var temp = new TempFolder();
        await using var store = await SettingsStore.LoadAsync(Options(temp), Ct);
        Assert.Equal(SettingsLoadOutcome.New, store.LoadOutcome);
        Assert.Equal(SettingsStore.Serialise(AppSettings.Default), SettingsStore.Serialise(store.Current));
        await store.FlushAsync(Ct);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task Saves_are_debounced()
    {
        using var temp = new TempFolder();
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var store = await SettingsStore.LoadAsync(Options(temp, time), Ct);

        store.Update(s => s with { Appearance = s.Appearance with { Accent = Accent.Orange } });
        time.Advance(TimeSpan.FromMilliseconds(300));
        store.Update(s => s with { Appearance = s.Appearance with { Density = Density.Compact } });
        time.Advance(TimeSpan.FromMilliseconds(300));
        await store.WaitForPendingSaveAsync();
        Assert.Equal(0, store.SaveCount);
        Assert.False(File.Exists(store.FilePath));

        time.Advance(TimeSpan.FromMilliseconds(250));
        await store.WaitForPendingSaveAsync();
        Assert.Equal(1, store.SaveCount);

        var json = JsonNode.Parse(File.ReadAllText(store.FilePath!))!;
        Assert.Equal("orange", (string?)json["appearance"]!["accent"]);
        Assert.Equal("compact", (string?)json["appearance"]!["density"]);
        Assert.Equal(1, (int?)json["schemaVersion"]);
    }

    [Fact]
    public async Task Flush_writes_at_once_and_leaves_no_temp_files()
    {
        using var temp = new TempFolder();
        await using var store = await SettingsStore.LoadAsync(Options(temp), Ct);
        store.Update(s => s with { General = s.General with { Language = "ja" } });
        await store.FlushAsync(Ct);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(["settings.json"], Directory.GetFiles(temp.Path).Select(Path.GetFileName));

        // Nothing pending: no second write.
        await store.FlushAsync(Ct);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task Dispose_flushes()
    {
        using var temp = new TempFolder();
        var store = await SettingsStore.LoadAsync(Options(temp), Ct);
        store.Update(s => s with { General = s.General with { Language = "zh" } });
        await store.DisposeAsync();

        await using var reloaded = await SettingsStore.LoadAsync(Options(temp), Ct);
        Assert.Equal("zh", reloaded.Current.General.Language);
        Assert.Equal(SettingsLoadOutcome.Loaded, reloaded.LoadOutcome);
    }

    [Fact]
    public async Task Round_trip_keeps_every_value()
    {
        using var temp = new TempFolder();
        var custom = AppSettings.Default with
        {
            General = new GeneralSettings { LibraryFolder = @"D:\Lib", WorkerThreadCount = 3, LaunchTimerSeconds = -1, LogLevel = LogLevel.Warning },
            Connection = new ConnectionSettings { ProxyType = ProxyType.Socks4, ProxyHost = "h", ProxyPort = 1080, UserId = "abcdefgh" },
            Builds = new BuildsSettings { DefaultBuild = @"daily\x", VersionSpecificQueries = new Dictionary<string, string> { ["4.2.0"] = "4.^.^@^" }, MarkNewDownloadsAsDefault = NewDownloadDefault.UpbgeWeekly },
            Internal = new InternalSettings
            {
                LastTimeChecked = new DateTimeOffset(2026, 11, 11, 13, 5, 0, TimeSpan.FromHours(1)),
                Window = new WindowPlacement { X = -1200, Y = 40, Width = 1500, Height = 900, Maximised = true },
                ViewSorts = new Dictionary<string, ViewSort> { [SortViews.Download] = new() { Column = SortColumn.Version, Descending = false } },
                CollapsedDetailSections = ["install"],
            },
        };

        await using (var store = await SettingsStore.LoadAsync(Options(temp), Ct))
        {
            store.Update(_ => custom);
        }

        await using var reloaded = await SettingsStore.LoadAsync(Options(temp), Ct);
        Assert.Equal(SettingsStore.Serialise(custom.Normalised()), SettingsStore.Serialise(reloaded.Current));
        Assert.Empty(SettingsStore.Diff(custom.Normalised(), reloaded.Current));
        Assert.Equal(TimeSpan.FromHours(1), reloaded.Current.Internal.LastTimeChecked!.Value.Offset);
    }

    [Fact]
    public async Task Missing_members_keep_their_defaults()
    {
        using var temp = new TempFolder();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """{ "schemaVersion": 1, "builds": { "scrapeUpbgeWeekly": true } }""");
        await using var store = await SettingsStore.LoadAsync(Options(temp), Ct);
        Assert.True(store.Current.Builds.ScrapeUpbgeWeekly);
        Assert.True(store.Current.Builds.ScrapeStable);
        Assert.Equal(12, store.Current.Builds.CheckIntervalHours);
        Assert.Equal("3.0", store.Current.Builds.MinimumStableVersion);
        Assert.Equal("auto", store.Current.General.Language);
        Assert.True(store.Current.Appearance.ShowDetailsPanel);
        Assert.Equal(1280, store.Current.Internal.Window.Width);
    }

    [Fact]
    public async Task Unknown_fields_survive_a_write()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "futureGroup": { "a": [1, 2, 3] },
              "futureTop": "x",
              "general": { "language": "es", "futureGeneral": { "deep": true } },
              "internal": { "window": { "width": 1400, "monitor": "DISPLAY2" } }
            }
            """);
        await using (var store = await SettingsStore.LoadAsync(Options(temp), Ct))
        {
            store.Update(s => s with { General = s.General with { Language = "fr" } });
        }

        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("x", (string?)json["futureTop"]);
        Assert.Equal(3, json["futureGroup"]!["a"]!.AsArray().Count);
        Assert.Equal("fr", (string?)json["general"]!["language"]);
        Assert.True((bool?)json["general"]!["futureGeneral"]!["deep"]);
        Assert.Equal("DISPLAY2", (string?)json["internal"]!["window"]!["monitor"]);
        Assert.Equal(1400, (int?)json["internal"]!["window"]!["width"]);
    }

    [Fact]
    public async Task Newer_schema_version_is_kept_and_unknown_enum_names_fall_back()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, """{ "schemaVersion": 7, "appearance": { "accent": "purple", "density": "COMPACT" }, "general": { "logLevel": "verbose" } }""");
        await using (var store = await SettingsStore.LoadAsync(Options(temp), Ct))
        {
            Assert.Equal(SettingsLoadOutcome.Loaded, store.LoadOutcome);
            Assert.Equal(Accent.Blue, store.Current.Appearance.Accent);
            Assert.Equal(Density.Compact, store.Current.Appearance.Density);
            Assert.Equal(LogLevel.Info, store.Current.General.LogLevel);
            store.Update(s => s with { Appearance = s.Appearance with { ShowDetailsPanel = false } });
        }

        Assert.Equal(7, (int?)JsonNode.Parse(File.ReadAllText(path))!["schemaVersion"]);
        Assert.False(File.Exists(Path.Combine(temp.Path, "settings.v7.bak")));
    }

    [Fact]
    public async Task Migration_steps_run_in_order_and_keep_one_backup()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");
        const string original = """{ "schemaVersion": 1, "general": { "lang": "ja" }, "keep": 1 }""";
        File.WriteAllText(path, original);
        var steps = new List<SettingsMigration>
        {
            new(1, doc =>
            {
                var g = doc["general"]!.AsObject();
                g["language"] = g["lang"]!.DeepClone();
                g.Remove("lang");
            }),
            new(2, doc => doc["appearance"] = new JsonObject { ["accent"] = "orange" }),
        };
        var options = Options(temp) with { CurrentSchemaVersion = 3, Migrations = steps };

        await using (var store = await SettingsStore.LoadAsync(options, Ct))
        {
            Assert.Equal(SettingsLoadOutcome.Migrated, store.LoadOutcome);
            Assert.Equal("ja", store.Current.General.Language);
            Assert.Equal(Accent.Orange, store.Current.Appearance.Accent);
            Assert.Equal(3, store.Current.SchemaVersion);
        }

        var backup = Path.Combine(temp.Path, "settings.v1.bak");
        Assert.Equal(original, File.ReadAllText(backup));
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(3, (int?)json["schemaVersion"]);
        Assert.Null(json["general"]!["lang"]);
        Assert.Equal(1, (int?)json["keep"]);

        // A second v1 file does not replace the first backup.
        File.WriteAllText(path, """{ "schemaVersion": 1, "general": { "lang": "zh" } }""");
        await using (var again = await SettingsStore.LoadAsync(options, Ct))
        {
            Assert.Equal("zh", again.Current.General.Language);
        }

        Assert.Equal(original, File.ReadAllText(backup));
    }

    [Fact]
    public void Migration_pipeline_stops_at_a_missing_step()
    {
        var doc = new JsonObject();
        var reached = SettingsMigrations.Run(doc, 1, 4, [new SettingsMigration(1, d => d["one"] = true)]);
        Assert.Equal(2, reached);
        Assert.Equal(2, (int?)doc["schemaVersion"]);
        Assert.Empty(SettingsMigrations.All);
    }

    [Fact]
    public async Task Corrupt_file_gives_defaults_and_is_kept()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ not json");
        await using var store = await SettingsStore.LoadAsync(Options(temp), Ct);
        Assert.Equal(SettingsLoadOutcome.Corrupt, store.LoadOutcome);
        Assert.Equal("{ not json", File.ReadAllText(path + ".corrupt"));
        Assert.Equal("auto", store.Current.General.Language);
    }

    [Fact]
    public async Task A_malformed_group_loses_only_that_group()
    {
        using var temp = new TempFolder();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"),
            """{ "general": { "language": "fr" }, "builds": { "checkIntervalHours": [1] }, "extra": 5 }""");
        await using var store = await SettingsStore.LoadAsync(Options(temp), Ct);
        Assert.Equal("fr", store.Current.General.Language);
        Assert.Equal(12, store.Current.Builds.CheckIntervalHours);
        Assert.Equal(5, store.Current.Extra!["extra"].GetInt32());
    }

    [Fact]
    public async Task Values_are_clamped_on_load_and_on_update()
    {
        using var temp = new TempFolder();
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """
            { "builds": { "checkIntervalHours": 2, "minimumStableVersion": "None" },
              "general": { "workerThreadCount": 0, "launchTimerSeconds": -9, "language": "de" },
              "connection": { "userId": "short", "proxyPort": 70000, "proxyHost": " " },
              "appearance": { "defaultView": "download:favorites" } }
            """);
        await using var store = await SettingsStore.LoadAsync(Options(temp), Ct);
        var s = store.Current;
        Assert.Equal(6, s.Builds.CheckIntervalHours);
        Assert.Equal("3.0", s.Builds.MinimumStableVersion);
        Assert.Equal(1, s.General.WorkerThreadCount);
        Assert.Equal(-1, s.General.LaunchTimerSeconds);
        Assert.Equal("auto", s.General.Language);
        Assert.Null(s.Connection.UserId);
        Assert.Equal(ConnectionOptions.DefaultProxyPort, s.Connection.ProxyPort);
        Assert.Equal(ConnectionOptions.DefaultProxyHost, s.Connection.ProxyHost);
        Assert.Equal("installed:all", s.Appearance.DefaultView);

        store.Update(x => x with { Builds = x.Builds with { CheckIntervalHours = 10_000 }, Connection = x.Connection with { UserId = " valid-user-1 " } });
        Assert.Equal(596, store.Current.Builds.CheckIntervalHours);
        Assert.Equal("valid-user-1", store.Current.Connection.UserId);
    }

    [Fact]
    public void Changed_event_names_the_keys()
    {
        var store = SettingsStore.InMemory();
        SettingsChangedEventArgs? seen = null;
        store.Changed += (_, e) => seen = e;

        var keys = store.Update(s => s with
        {
            Connection = s.Connection with { ProxyType = ProxyType.Http },
            Internal = s.Internal with { Window = s.Internal.Window with { Maximised = true } },
            Appearance = s.Appearance with { Accent = Accent.Orange },
        });

        Assert.NotNull(seen);
        Assert.Equal(keys, seen.Changed);
        Assert.Equal(
            ["appearance.accent", "connection.proxyType", "internal.window"],
            seen.Changed.Select(k => k.ToString()).Order(StringComparer.Ordinal));
        Assert.True(seen.Affects(SettingsGroups.Connection));
        Assert.False(seen.Affects(SettingsGroups.Builds));
        Assert.True(seen.Affects(new SettingKey(SettingsGroups.Appearance, "accent")));
        Assert.Equal([new SettingKey(SettingsGroups.Connection, "proxyType")], seen.RestartRequired);
        Assert.Equal(Accent.Blue, seen.Previous.Appearance.Accent);
        Assert.Same(store.Current, seen.Current);
    }

    [Fact]
    public void No_event_when_nothing_changes()
    {
        var store = SettingsStore.InMemory();
        var raised = 0;
        store.Changed += (_, _) => raised++;
        Assert.Empty(store.Update(s => s with { Builds = s.Builds with { CheckIntervalHours = 12 } }));

        // A value normalised back to the current one is not a change either.
        store.Update(s => s with { Builds = s.Builds with { CheckIntervalHours = 6 } });
        raised = 0;
        Assert.Empty(store.Update(s => s with { Builds = s.Builds with { CheckIntervalHours = 3 } }));
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Restart_metadata_matches_plan_P5_02()
    {
        Assert.Equal(
            ["connection.proxyHost", "connection.proxyPort", "connection.proxyType", "connection.useCustomTlsCertificates",
             "general.language", "general.logLevel", "general.workerThreadCount"],
            SettingsMetadata.RestartRequiredSettings.Select(k => k.ToString()).Order(StringComparer.Ordinal));
        Assert.True(SettingsMetadata.CredentialRequiresRestart(CredentialKeys.ProxyPassword));
        Assert.False(SettingsMetadata.CredentialRequiresRestart(CredentialKeys.GitHubToken));

        // Every listed key is a real member of settings.json.
        var json = JsonNode.Parse(SettingsStore.Serialise(AppSettings.Default))!;
        foreach (var key in SettingsMetadata.RestartRequiredSettings)
        {
            Assert.True(json[key.Group]!.AsObject().ContainsKey(key.Name), key.ToString());
        }
    }

    [Fact]
    public async Task Write_failure_is_logged_not_thrown()
    {
        using var temp = new TempFolder();
        var blocker = Path.Combine(temp.Path, "file");
        File.WriteAllText(blocker, "");
        var options = new SettingsStoreOptions { FilePath = Path.Combine(blocker, "settings.json") };
        await using var store = await SettingsStore.LoadAsync(options, Ct);
        store.Update(s => s with { General = s.General with { Language = "en" } });
        await store.FlushAsync(Ct);
        Assert.Equal(0, store.SaveCount);
    }
}
