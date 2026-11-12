using System.Globalization;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Pipeline;
using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Core.Tests.Library;
using BlenderLauncher.Core.Tests.Processes;
using StubHandler = BlenderLauncher.Core.Tests.Pipeline.StubHandler;
using TestArchives = BlenderLauncher.Core.Tests.Pipeline.TestArchives;

namespace BlenderLauncher.Core.Tests;

/// <summary>The composition root with in-memory settings, stand-in platform services and no network.</summary>
public sealed class AppServicesTests : IDisposable
{
    private readonly TempDir _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    private string Library()
    {
        var root = _temp.Folder("Lib");
        return root;
    }

    private AppServicesOptions Options(AppSettings? settings = null, string? library = null, PlatformServiceBundle? platform = null, Func<ConnectionOptions, HttpMessageHandler>? http = null) => new()
    {
        Directories = new DataDirectories(_temp.Combine("data")),
        Settings = settings is null ? null : SettingsStore.InMemory(settings),
        Platform = platform ?? new PlatformServiceBundle { VersionProbe = new FakeProbe(Samples.BlenderDaily) },
        LibraryOverride = library,
        StartScraping = false,
        UiCulture = CultureInfo.GetCultureInfo("fr-FR"),
        HttpHandlerFactory = http ?? (_ => new StubHandler((_, _, _) => throw new HttpRequestException("no network in tests"))),
    };

    [Fact]
    public async Task Starts_every_service_and_scans_the_library()
    {
        var lib = Library();
        await Samples.WriteBlinfoAsync(_temp.Folder("Lib", "daily", "a"), Samples.Info());
        var settings = AppSettings.Default with { General = new GeneralSettings { WorkerThreadCount = 2, Language = "auto" } };

        var services = await AppServices.StartAsync(Options(settings, lib), Ct);
        await using (services)
        {
            Assert.Equal(lib, services.LibraryRoot);
            Assert.Equal(lib, services.Library.LibraryRoot);
            Assert.Single(services.Library.Current.Builds);
            Assert.Equal(2, services.Queue.WorkerCount);
            Assert.Equal("fr", services.Translator.Language);
            Assert.False(services.Launches.IsAvailable);
            Assert.False(services.Platform.RecycleBin.IsAvailable);
            Assert.Same(ScrapeSnapshot.Empty, services.Scrape.Current);
            Assert.Null(await services.StartupScrape);
            Assert.False(Directory.Exists(Path.Combine(lib, ".temp", "BlenderLauncherNet")));
            Assert.False(Directory.Exists(Path.Combine(lib, "upbge-weekly")));
            Assert.False(Directory.Exists(Path.Combine(lib, "template")));
        }
    }

    [Fact]
    public async Task Dispose_flushes_the_settings_the_library_state_and_the_log()
    {
        var lib = Library();
        var folder = _temp.Folder("Lib", "daily", "a");
        await Samples.WriteBlinfoAsync(folder, Samples.Info());
        var data = new DataDirectories(_temp.Combine("data"));
        Directory.CreateDirectory(data.Root);
        File.WriteAllText(data.SettingsFile, """{ "schemaVersion": 1, "general": { "libraryFolder": "" } }""");

        var services = await AppServices.StartAsync(Options(library: lib) with { Settings = null }, Ct);
        services.Settings.Update(s => s with { General = s.General with { LibraryFolder = lib } });
        services.Library.UpdateState(folder, s => s with { ReplaceOnNewBuild = true });
        services.Log.Info("marker-line");
        var logFile = services.Log.FilePath;
        await services.DisposeAsync();
        await services.DisposeAsync(); // twice is harmless

        Assert.Contains("libraryFolder", File.ReadAllText(data.SettingsFile), StringComparison.Ordinal);
        Assert.Contains(lib.Replace("\\", "\\\\", StringComparison.Ordinal), File.ReadAllText(data.SettingsFile), StringComparison.Ordinal);
        Assert.Contains("replaceOnNewBuild", File.ReadAllText(Path.Combine(data.State, LibraryState.FileName)), StringComparison.Ordinal);
        Assert.Contains("marker-line", File.ReadAllText(logFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Starts_without_a_library()
    {
        var services = await AppServices.StartAsync(Options(AppSettings.Default), Ct);
        await using (services)
        {
            Assert.Null(services.LibraryRoot);
            Assert.Null(services.Library.LibraryRoot);
        }
    }

    [Fact]
    public async Task Default_build_moves_from_the_settings_into_the_library_state()
    {
        var lib = Library();
        var folder = _temp.Folder("Lib", "daily", "b");
        await Samples.WriteBlinfoAsync(folder, Samples.Info());
        var settings = AppSettings.Default with { Builds = new BuildsSettings { DefaultBuild = @"daily\b" } };

        var services = await AppServices.StartAsync(Options(settings, lib), Ct);
        await using (services)
        {
            Assert.Equal(folder, services.Library.DefaultBuild);
            Assert.Null(services.Settings.Current.Builds.DefaultBuild);
        }
    }

    [Fact]
    public async Task Default_build_already_in_the_library_state_wins()
    {
        var lib = Library();
        var a = _temp.Folder("Lib", "daily", "a");
        var b = _temp.Folder("Lib", "daily", "b");
        var state = await LibraryState.LoadAsync(_temp.Combine("data", "state"), ct: Ct);
        state.SetDefaultBuild(lib, a);
        await state.DisposeAsync();
        var settings = AppSettings.Default with { Builds = new BuildsSettings { DefaultBuild = b } };

        var services = await AppServices.StartAsync(Options(settings, lib), Ct);
        await using (services)
        {
            Assert.Equal(a, services.Library.DefaultBuild);
            Assert.Null(services.Settings.Current.Builds.DefaultBuild);
        }
    }

    [Fact]
    public async Task Default_build_import_does_nothing_without_a_carried_value()
    {
        var lib = Library();
        var services = await AppServices.StartAsync(Options(AppSettings.Default, lib), Ct);
        await using (services)
        {
            Assert.Equal(DefaultBuildImportOutcome.NothingToImport, DefaultBuildImport.Reconcile(services.Library, services.Settings));
            Assert.Null(services.Library.DefaultBuild);
        }
    }

    [Fact]
    public async Task Settings_changes_rebuild_the_client_and_open_a_new_library()
    {
        var first = Library();
        var second = _temp.Folder("Other");
        await Samples.WriteBlinfoAsync(_temp.Folder("Other", "stable", "x"), Samples.Info("4.2.3", "stable", null));
        var settings = AppSettings.Default with { General = new GeneralSettings { LibraryFolder = first } };
        var services = await AppServices.StartAsync(Options(settings), Ct);
        await using (services)
        {
            var client = services.Http.Client;
            services.Settings.Update(s => s with { Connection = s.Connection with { UserId = "abcdefgh-1234" } });
            Assert.NotSame(client, services.Http.Client);
            Assert.Equal("abcdefgh-1234", services.Http.Options.UserId);

            services.Settings.Update(s => s with { General = s.General with { LibraryFolder = second } });
            for (var i = 0; i < 500 && services.Library.Current.Builds.Count() != 1; i++)
            {
                await Task.Delay(10, Ct);
            }

            Assert.Equal(second, services.LibraryRoot);
            Assert.Equal("4.2.3", services.Library.Current.Builds.Single().Subversion);
        }
    }

    [Fact]
    public async Task Install_adds_the_build_to_the_library_and_marks_it_default()
    {
        var lib = Library();
        const string link = "https://builder.blender.org/download/daily/blender-4.3.0-alpha+main.ddc9f92777cd-windows.amd64-release.zip";
        var zip = TestArchives.Zip(
            ("blender-4.3.0-alpha+main.ddc9f92777cd-windows.amd64-release/blender.exe", "exe"),
            ("blender-4.3.0-alpha+main.ddc9f92777cd-windows.amd64-release/4.3/scripts/startup.py", "print(1)"));
        var settings = AppSettings.Default with { Builds = new BuildsSettings { MarkNewDownloadsAsDefault = NewDownloadDefault.Daily } };
        var services = await AppServices.StartAsync(
            Options(settings, lib, http: _ => new StubHandler((_, _, _) => Task.FromResult(StubHandler.Bytes(zip)))), Ct);
        await using (services)
        {
            var available = new AvailableBuild(Samples.Info() with { Link = link }) { SizeBytes = zip.Length };

            var entry = await services.InstallAsync(available, Ct);

            Assert.NotNull(entry);
            Assert.Equal(LibraryEntryKind.Build, entry.Kind);
            Assert.Equal(Path.Combine(lib, "daily", "blender-4.3.0-alpha+daily.ddc9f92777cd"), entry.Path);
            Assert.Same(entry, services.Library.Current.Find(entry.Path));
            Assert.NotNull(services.Library.GetState(entry.Path).InstalledOn);
            Assert.Equal(entry.Path, services.Library.DefaultBuild);
            Assert.True(File.Exists(Path.Combine(entry.Path, BlinfoCodec.FileName)));

            // A second request for an installed build's job returns the same entry.
            Assert.Empty(services.Install.Jobs);

            // Its folder size is measured in the background (the lists and the disk card show it).
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (services.Library.GetState(entry.Path).SizeBytes is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, Ct);
            }

            Assert.True(services.Library.GetState(entry.Path).SizeBytes > 0);
        }
    }

    [Theory]
    [InlineData(NewDownloadDefault.None, "daily", false)]
    [InlineData(NewDownloadDefault.Stable, "lts", true)]
    [InlineData(NewDownloadDefault.Daily, "daily", true)]
    [InlineData(NewDownloadDefault.Experimental, "PR12345", true)]
    [InlineData(NewDownloadDefault.Bforartists, "bforartists", true)]
    [InlineData(NewDownloadDefault.UpbgeStable, "upbge-weekly", false)]
    [InlineData(NewDownloadDefault.UpbgeWeekly, "upbge-weekly", true)]
    public void Marks_new_downloads_as_default_per_channel(NewDownloadDefault setting, string branch, bool expected) =>
        Assert.Equal(expected, AppServices.MarksAsDefault(setting, branch));

    [Fact]
    public async Task Launching_through_the_services_stamps_the_library()
    {
        var lib = Library();
        var folder = _temp.Folder("Lib", "daily", "a");
        await Samples.WriteBlinfoAsync(folder, Samples.Info());
        var launcher = new FakeLauncher();
        var services = await AppServices.StartAsync(
            Options(AppSettings.Default, lib, new PlatformServiceBundle { ProcessLauncher = launcher, VersionProbe = FakeProbe.Failing() }), Ct);
        await using (services)
        {
            var process = services.Launches.Launch(services.Library.Current.Builds.Single());
            Assert.NotNull(services.Library.GetState(folder).LastUsed);
            launcher.Processes[process.ProcessId].Exit();
            await services.Launches.WhenNotRunningAsync(folder, Ct);
        }
    }
}
