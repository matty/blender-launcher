using System.Globalization;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.DesignData;

/// <summary>
/// The design's own sample data: its <c>AV</c> list of available builds and its <c>INIT</c> installed set, with
/// the same favourites, default, running instance, portable and replace-on-new-build flags. Needs no services, files
/// or network, so previews, render tests and the main-window self-test can build the whole window from it.
/// </summary>
/// <remarks>
/// Differences from the design's data, because the port derives these from real build data: experimental builds show
/// no "Alpha" label (their prerelease field is the branch name), and installed paths use the real library folders
/// (LTS builds live in <c>stable</c>).
/// </remarks>
public static class SampleData
{
    /// <summary>The design's library folder.</summary>
    public const string LibraryRoot = @"D:\Blender\builds";

    private sealed record Av(string Ch, string V, string Br, string Hash, string Date, int Mb, string Py, bool IsNew = false, string? Desc = null);

    private static readonly Av[] AvList =
    [
        new("stable", "5.2.2", "v5.2.2", "a3f81c09e2b4", "Oct 14, 2026", 412, "3.11.9"),
        new("stable", "5.2.1", "v5.2.1", "2be04d17f8a3", "Aug 26, 2026", 411, "3.11.9"),
        new("stable", "5.2.0", "v5.2.0", "90cd3e5a1f62", "Jul 15, 2026", 410, "3.11.9"),
        new("stable", "5.1.1", "v5.1.1", "7c19d04b55af", "May 2, 2026", 398, "3.11.9"),
        new("stable", "5.1.0", "v5.1.0", "d41e86b0c7f9", "Mar 17, 2026", 397, "3.11.9"),
        new("stable", "5.0.1", "v5.0.1", "5f0a2c9e81d4", "Dec 16, 2025", 389, "3.11.9"),
        new("lts", "5.2.2", "v5.2.2", "a3f81c09e2b4", "Oct 14, 2026", 412, "3.11.9"),
        new("lts", "4.5.6", "v4.5.6", "c86f12ad3e07", "Sep 23, 2026", 371, "3.11.9"),
        new("lts", "4.5.5", "v4.5.5", "1e7b94f05ac2", "Jul 29, 2026", 371, "3.11.9"),
        new("lts", "4.2.14", "v4.2.14", "6a3d0b8ef419", "Aug 19, 2026", 340, "3.11.7"),
        new("lts", "4.2.13", "v4.2.13", "b59e7c21d0f8", "Jun 17, 2026", 340, "3.11.7"),
        new("lts", "3.6.23", "v3.6.23", "0d8c5af4e936", "Jun 3, 2025", 302, "3.10.13"),
        new("daily", "5.4.0-alpha", "main", "c47e1b2f9a10", "Nov 11, 2026", 433, "3.13.2", IsNew: true),
        new("daily", "5.4.0-alpha", "main", "f6d2a8810c3e", "Nov 10, 2026", 431, "3.13.2"),
        new("daily", "5.3.0-beta", "blender-v5.3-release", "8d2f6a41be07", "Nov 11, 2026", 429, "3.13.2", IsNew: true),
        new("daily", "5.3.0-beta", "blender-v5.3-release", "e01b7f3a9c42", "Nov 9, 2026", 428, "3.13.2"),
        new("daily", "5.2.3-rc", "blender-v5.2-release", "4c90e7d2a15b", "Nov 10, 2026", 413, "3.11.9"),
        new("experimental", "5.4.0-npr-prototype", "npr-prototype", "19ab4e6c7d02", "Nov 6, 2026", 436, "3.13.2",
            Desc: "Non-photorealistic rendering prototype: line-art and stylised shading built on the compositor."),
        new("experimental", "5.4.0-vulkan-default", "vulkan-default", "3e58d1b0f7c6", "Nov 8, 2026", 434, "3.13.2",
            IsNew: true, Desc: "Vulkan as the default GPU backend. Report driver issues on the tracker."),
        new("experimental", "5.4.0-sculpt-layers", "sculpt-layers", "a7f0c39d24e1", "Nov 4, 2026", 432, "3.13.2",
            Desc: "Non-destructive sculpt layers with per-layer strength and masking."),
        new("experimental", "5.3.0-vse-strip-modifiers", "vse-strip-modifiers", "6bd28e4f0a93", "Oct 30, 2026", 427, "3.13.2",
            Desc: "Stackable modifiers for sequencer strips, previewed in real time."),
    ];

    private sealed record Inst(string Ch, string Hash, string InstalledOn, string? LastUsed, bool Portable = false, bool AutoUpdate = false,
        bool Favourite = false, bool Default = false, int Running = 0);

    // The design's INIT, with its favourites (INIT[0], INIT[5]), default (INIT[0]) and running instance (INIT[5]).
    private static readonly Inst[] InitList =
    [
        new("lts", "a3f81c09e2b4", "Oct 15, 2026", "today 09:41", Favourite: true, Default: true),
        new("stable", "7c19d04b55af", "May 3, 2026", "Nov 2, 2026"),
        new("lts", "c86f12ad3e07", "Sep 24, 2026", "Nov 7, 2026", Portable: true),
        new("lts", "6a3d0b8ef419", "Aug 20, 2026", "Oct 21, 2026"),
        new("daily", "e01b7f3a9c42", "Nov 9, 2026", "yesterday 18:12", AutoUpdate: true),
        new("daily", "f6d2a8810c3e", "Nov 10, 2026", "today 10:05", AutoUpdate: true, Favourite: true, Running: 1),
        new("experimental", "19ab4e6c7d02", "Nov 6, 2026", "Nov 8, 2026"),
    ];

    /// <summary>The design's available builds.</summary>
    public static IReadOnlyList<AvailableBuild> AvailableBuilds() =>
        [.. AvList.Select(a => new AvailableBuild(ToBuild(a, link: $"https://example.invalid/{a.Ch}/{a.Hash}.zip"))
        {
            SizeBytes = a.Mb * 1_000_000L,
            IsNew = a.IsNew,
            Description = a.Desc,
        })];

    /// <summary>
    /// A shell view model filled with the design's data and state: installed view "All builds", the first installed
    /// build selected, the first available build of each channel selected for the download view.
    /// </summary>
    /// <param name="extraRows">Synthetic download rows added to the download channel of <paramref name="view"/> (Daily
    /// otherwise), to try the list with many rows.</param>
    public static MainWindowViewModel CreateMainWindowViewModel(SidebarView? view = null, AppSettings? settings = null, int extraRows = 0,
        SettingsStore? settingsStore = null)
    {
        var store = settingsStore ?? (settings is null ? null : SettingsStore.InMemory(settings));
        var shell = new MainWindowViewModel(store);

        var available = AvList.Concat(ExtraRows(view, extraRows)).Select(a =>
        {
            var row = new AvailableBuildRowViewModel(new AvailableBuild(ToBuild(a, link: $"https://example.invalid/{a.Ch}/{a.Hash}.zip"))
            {
                SizeBytes = a.Mb * 1_000_000L,
                IsNew = a.IsNew,
                Description = a.Desc,
            });
            row.ApplySample(python: null);
            return row;
        }).ToList();

        var installed = InitList.Select(i =>
        {
            var a = AvList.First(x => x.Ch == i.Ch && x.Hash == i.Hash);
            var channel = ChannelOf(a.Ch);
            var folder = Path.Combine(LibraryRoot, Channels.LibraryFolderOf(channel),
                "blender-" + VersionPart(a.V) + (a.Ch is "daily" or "experimental" ? "-" + a.Hash[..8] : "") + "-windows-x64");
            var build = ToBuild(a, link: folder) with { IsFavorite = i.Favourite };
            var row = new InstalledBuildRowViewModel(folder);
            row.ApplySample(build, channel, a.Mb * 1_000_000L, a.Py, a.Desc);
            row.InstalledOn = ParseDate(i.InstalledOn);
            row.LastUsed = i.LastUsed is null ? null : ParseLastUsed(i.LastUsed);
            row.IsPortable = i.Portable;
            row.ReplaceOnNewBuild = i.AutoUpdate;
            row.IsDefault = i.Default;
            row.RunningCount = i.Running;
            return row;
        }).ToList();

        shell.Catalog.LoadSample(installed, available, LibraryRoot, DateTimeOffset.Now.AddMinutes(-4));
        shell.SelectedInstalled = installed[0];
        shell.SelectedAvailable = available.First(r => r.Channel == Channel.Daily);
        if (view is { } v)
        {
            shell.Navigate(v);
            if (v.Section == SidebarSection.Download && DisplayFormat.ChannelFromKey(v.Key) is { } channel)
            {
                shell.SelectedAvailable = available.FirstOrDefault(r => r.Channel == channel) ?? shell.SelectedAvailable;
            }
        }

        return shell;
    }

    // Older builds of one channel, a day apart, with unique hashes.
    private static IEnumerable<Av> ExtraRows(SidebarView? view, int count)
    {
        var channel = view is { Section: SidebarSection.Download } v && DisplayFormat.ChannelFromKey(v.Key) is { } c
            ? DisplayFormat.ChannelKey(c)
            : "daily";
        var start = new DateTime(2026, 10, 1);
        for (var i = 0; i < count; i++)
        {
            var version = (5 - i / 120) + "." + (i / 30 % 4) + "." + (i % 3) + (channel is "daily" ? "-alpha" : "");
            var hash = ((uint)(i * 2654435761u)).ToString("x8", CultureInfo.InvariantCulture) + "beef";
            var date = start.AddDays(-i).ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
            yield return new Av(channel, version, channel is "daily" ? "main" : "v" + version, hash, date, 380 + i % 60, "3.11.9");
        }
    }

    private static BuildInfo ToBuild(Av a, string link) => new()
    {
        Link = link,
        Subversion = a.V,
        BuildHash = a.Hash,
        CommitTime = IsoTimestamp.Aware(ParseDate(a.Date).AddHours(12)),
        Branch = a.Ch,
    };

    private static Channel ChannelOf(string ch) => DisplayFormat.ChannelFromKey(ch) ?? Channel.Custom;

    private static string VersionPart(string v) => v.Split('-')[0];

    private static DateTimeOffset ParseDate(string text) =>
        new(DateTime.ParseExact(text, "MMM d, yyyy", CultureInfo.InvariantCulture), TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));

    // "today 09:41", "yesterday 18:12", or a date.
    private static DateTimeOffset ParseLastUsed(string text)
    {
        var parts = text.Split(' ');
        if (parts[0] is "today" or "yesterday")
        {
            var time = TimeSpan.ParseExact(parts[1], @"hh\:mm", CultureInfo.InvariantCulture);
            var day = DateTime.Today.AddDays(parts[0] == "yesterday" ? -1 : 0);
            return new DateTimeOffset(day + time, TimeZoneInfo.Local.GetUtcOffset(day));
        }

        return ParseDate(text).AddHours(15);
    }
}
