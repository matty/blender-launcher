using System.Diagnostics;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Pipeline;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Core.Versioning;
using BlenderLauncher.Platform.Windows;

namespace BlenderLauncher.App.Diagnostics;

/// <summary>
/// Developer switch: <c>"Blender Launcher.exe" --dev-install &lt;query&gt; [--library
/// &lt;dir&gt;] [--data &lt;dir&gt;] [--out &lt;file&gt;]</c>. Builds the real <see cref="AppServices"/> over the
/// given library (a new temporary one when none is given, which is kept), runs one forced check, picks the newest
/// available build matching the version query (for example <c>4.5.^-lts</c> or <c>^.^.^-daily</c>), installs it
/// with progress lines and prints the installed folder and its <c>.blinfo</c>. Exit code 0 on success, 1 on
/// failure, 2 for a bad command line. Not shown in any help text; Avalonia is never started.
/// </summary>
internal static class DevInstall
{
    public const string Switch = "--dev-install";

    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(30);

    public static bool IsRequested(string[] args) =>
        args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        var outPath = DevOutput.Option(args, "--out") is { } o ? Path.GetFullPath(o) : null;
        using var output = new DevOutput(outPath);
        var query = DevOutput.Positional(args, Switch, ["--out", "--library", "--data"]);
        if (query is null || !VersionSearchQuery.TryParse(query, out var parsed))
        {
            output.Line($"{Switch}: a version query is required (for example \"^.^.^-lts\"); got '{query}'");
            output.Line($"Usage: \"{AppIdentity.ExecutableName}\" {Switch} <query> [--library <dir>] [--data <dir>] [--out <file>]");
            return 2;
        }

        try
        {
            return RunAsync(parsed, DevOutput.Option(args, "--library"), DevOutput.Option(args, "--data"), outPath, output).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            output.Line($"{Switch} failed: {e}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(VersionSearchQuery query, string? libraryArg, string? dataArg, string? outPath, DevOutput output)
    {
        var temporaryData = dataArg is null;
        var dataRoot = dataArg is not null ? Path.GetFullPath(dataArg) : Path.Combine(Path.GetTempPath(), $"{AppIdentity.TechnicalId}-dev-data-{Guid.NewGuid():N}");
        var library = libraryArg is not null ? Path.GetFullPath(libraryArg) : Path.Combine(Path.GetTempPath(), $"{AppIdentity.TechnicalId}-dev-library-{Guid.NewGuid():N}");
        Directory.CreateDirectory(library);

        output.Line($"{AppIdentity.DisplayName} {AppIdentity.Version} {Switch} {query}");
        output.Line($"Library: {library}{(libraryArg is null ? " (new, kept)" : "")}");
        output.Line($"Data: {dataRoot}{(temporaryData ? " (temporary)" : "")}");

        using var limit = new CancellationTokenSource(Limit);
        var services = await AppServices.StartAsync(new AppServicesOptions
        {
            Directories = new DataDirectories(dataRoot),
            Settings = SettingsStore.InMemory(AppSettings.Default),
            Platform = WindowsPlatformServices.Create(),
            LibraryOverride = library,
            StartScraping = false,
            LogLevel = LogLevel.Debug,
        }, limit.Token).ConfigureAwait(false);
        try
        {
            output.Line("Checking for builds (forced, every visible channel)...");
            var snapshot = await services.Scrape.CheckAsync(forced: true, limit.Token).ConfigureAwait(false);
            output.Line($"Found {snapshot.Builds.Count} builds; {snapshot.Errors.Count} error(s)");

            var matches = query.Match(
                snapshot.Builds.Where(b => b.Build.FullSemVersion is not null),
                b => VersionSearchCandidate.FromBuildInfo(b.Build));
            if (matches.Count == 0)
            {
                output.Line($"No available build matches {query}");
                return 1;
            }

            var pick = matches.MaxBy(b => VersionSearchCandidate.FromBuildInfo(b.Build))!;
            output.Line($"Matches: {matches.Count}; installing {pick.Build.Subversion} ({pick.Build.Branch}) from {pick.Build.Link}" +
                        (pick.SizeBytes is { } size ? $", {DevOutput.FormatBytes(size)}" : ""));

            var clock = Stopwatch.StartNew();
            var job = services.Install.Install(pick);
            var lastTenth = -1;
            job.StateChanged += j => output.Line($"  [{DevOutput.Seconds(clock.Elapsed),8}] {j.State}");
            job.ProgressChanged += (j, p) =>
            {
                if (p.Fraction is not { } f)
                {
                    return;
                }

                var tenth = (int)(f * 10);
                if (Interlocked.Exchange(ref lastTenth, tenth) != tenth)
                {
                    output.Line($"  [{DevOutput.Seconds(clock.Elapsed),8}] {j.State} {tenth * 10,3}% ({DevOutput.FormatBytes(p.Done)} of {DevOutput.FormatBytes(p.Total ?? 0)})");
                }
            };

            var entry = await services.InstallAsync(pick, limit.Token).ConfigureAwait(false);
            clock.Stop();
            if (entry is null)
            {
                output.Line($"Install failed: {job.State} {job.Error?.Message}");
                return 1;
            }

            var bytes = BuildFolderInspector.MeasureSize(entry.Path);
            output.Line("");
            output.Line($"Installed: {entry.Path}");
            output.Line($"Kind: {entry.Kind}; version {entry.Build?.Subversion}; branch {entry.Build?.Branch}; hash {entry.Build?.BuildHash ?? "(none)"}");
            output.Line($"Size: {DevOutput.FormatBytes(bytes)}; install time: {DevOutput.Seconds(clock.Elapsed)}");
            output.Line($"Installed on (sidecar): {services.Library.GetState(entry.Path).InstalledOn:O}");
            output.Line("");
            output.Line(".blinfo:");
            output.Line(await File.ReadAllTextAsync(Path.Combine(entry.Path, BlinfoCodec.FileName), limit.Token).ConfigureAwait(false));
            output.Line("exit=0");
            return 0;
        }
        finally
        {
            var logFile = services.Log.FilePath;
            await services.DisposeAsync().ConfigureAwait(false);
            if (outPath is not null)
            {
                DevOutput.TryCopy(logFile, outPath + ".log");
            }

            if (temporaryData)
            {
                DevOutput.TryDelete(dataRoot);
            }
        }
    }
}
