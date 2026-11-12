using System.Globalization;
using System.Text;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Localisation;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Updates;
using BlenderLauncher.Platform.Windows.Cli;

namespace BlenderLauncher.App.Diagnostics;

/// <summary>
/// Developer switch: <c>"Blender Launcher.exe" --dev-scrape [--out &lt;file&gt;]
/// [--data &lt;dir&gt;] [--pr-titles]</c>. Runs one forced check (every source, as Shift+Check with every channel
/// visible) against the live sites through the real <see cref="ScrapeService"/>, and prints the builds per channel,
/// per-source timings and errors. Exit code 0 when nothing failed, 1 when any source (or the API refresh) reported
/// an error, 2 for a bad command line.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Data (caches, state, log) go to a new temporary folder that is deleted afterwards, unless <c>--data</c>
/// names one, which is kept, so a second run shows the caches at work.</item>
/// <item>Output goes to the console the exe was started from (<see cref="ParentConsole"/>) or to redirected
/// standard output, and always to <c>--out</c> when given, with the run's log beside it as <c>&lt;out&gt;.log</c>.
/// A GUI-subsystem exe is not waited for by <c>cmd</c>, so <c>--out</c> (or <c>Start-Process -Wait</c>) is the
/// reliable way to read it.</item>
/// <item>It ships in the exe (it is small and has no UI), is not shown in any help text, and Avalonia is never
/// started for it.</item>
/// </list>
/// </remarks>
internal static class DevScrape
{
    public const string Switch = "--dev-scrape";

    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

    public static bool IsRequested(string[] args) =>
        args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        string? outPath = null;
        string? dataPath = null;
        var prTitles = false;
        string? error = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case Switch:
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = Path.GetFullPath(args[++i]);
                    break;
                case "--data" when i + 1 < args.Length:
                    dataPath = Path.GetFullPath(args[++i]);
                    break;
                case "--pr-titles":
                    prTitles = true;
                    break;
                default:
                    error ??= $"unknown or incomplete argument '{args[i]}'";
                    break;
            }
        }

        var attached = ParentConsole.TryAttach(out _);
        var canPrint = attached is ConsoleAttachOutcome.Attached or ConsoleAttachOutcome.Redirected;
        if (error is not null)
        {
            Emit($"{Switch}: {error}\nUsage: \"{AppIdentity.ExecutableName}\" {Switch} [--out <file>] [--data <dir>] [--pr-titles]\n", outPath, canPrint);
            return 2;
        }

        int exitCode;
        string report;
        try
        {
            (exitCode, report) = RunAsync(dataPath, prTitles, outPath).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            exitCode = 1;
            report = $"{Switch} failed: {e}\n";
        }

        Emit(report, outPath, canPrint);
        if (attached == ConsoleAttachOutcome.Attached)
        {
            ParentConsole.Detach();
        }

        return exitCode;
    }

    private static async Task<(int ExitCode, string Report)> RunAsync(string? dataPath, bool prTitles, string? outPath)
    {
        var temporary = dataPath is null;
        var root = dataPath ?? Path.Combine(Path.GetTempPath(), $"{AppIdentity.TechnicalId}-dev-scrape-{Guid.NewGuid():N}");
        var directories = new DataDirectories(root);
        Directory.CreateDirectory(directories.Cache);

        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');

        var log = Log.Open(directories.Logs, LogLevel.Debug);
        try
        {
            Line($"{AppIdentity.DisplayName} {AppIdentity.Version} {Switch}");
            Line($"Data: {root}{(temporary ? " (temporary)" : "")}");
            Line($"Started: {DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");

            using var http = new HttpClientProvider(ConnectionOptions.Default, log);
            var api = new ApiDataService(directories, http, log);
            await api.LoadAsync().ConfigureAwait(false);
            var releases = new AppReleaseService(http, log);

            // Every channel visible, so the forced check runs every source.
            var settings = ScrapeSettings.Default with { ShowUpbgeWeekly = true, FetchPrTitles = prTitles, CheckOnStartup = false };
            await using var service = ScrapeService.Create(http, directories, () => settings, api, releases, new InMemoryScrapeStateStore(), log);

            using var limit = new CancellationTokenSource(Limit);
            var snapshot = await service.CheckAsync(forced: true, limit.Token).ConfigureAwait(false);
            Format(snapshot, Line);
            return (snapshot.Errors.Count == 0 ? 0 : 1, text.ToString());
        }
        finally
        {
            await log.DisposeAsync().ConfigureAwait(false);
            if (outPath is not null)
            {
                TryCopy(log.FilePath, outPath + ".log");
            }

            if (temporary)
            {
                TryDelete(root);
            }
        }
    }

    private static void Format(ScrapeSnapshot snapshot, Action<string> line)
    {
        var translator = new Translator("en");
        var total = snapshot.CompletedAt - snapshot.StartedAt;

        line("");
        line($"{"Source",-14} {"Builds",7} {"Filtered",9} {"Dupes",6} {"Seconds",8}  Status");
        foreach (var source in snapshot.Sources)
        {
            line($"{source.SourceId,-14} {source.Builds.Count,7} {source.FilteredOut,9} {source.Duplicates,6} " +
                 $"{source.Duration.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture),8}  {(source.Succeeded ? "ok" : "FAILED")}");
        }

        line("");
        line("Builds per channel:");
        foreach (var channel in Enum.GetValues<Channel>().Where(c => c != Channel.Custom))
        {
            line($"  {channel,-13} {snapshot.ForChannel(channel).Count(),5}");
        }

        line($"  {"Total",-13} {snapshot.Builds.Count,5}  ({snapshot.NewCount} new)");

        line("");
        var api = snapshot.ApiRefresh;
        line(api is null
            ? "API data: not refreshed"
            : $"API data: fetched={api.ApiDataFetched}, changed={api.ApiDataChanged}, stable seed={api.StableSeed}{(api.StableSeedFromBundle ? " (bundled)" : "")}");
        var update = snapshot.LauncherUpdate;
        line(update is null ? "Launcher update: not checked" : $"Launcher update: {update.Status}{(update.Latest is { } l ? $" ({l.Tag})" : "")}");

        line("");
        if (snapshot.Errors.Count == 0)
        {
            line("Errors: none");
        }
        else
        {
            line($"Errors: {snapshot.Errors.Count}");
            foreach (var error in snapshot.Errors)
            {
                line($"  {error.SourceId}: {ScrapeErrorText.Format(translator, error)}");
                if (error.Detail is { } detail)
                {
                    line($"    detail: {detail}");
                }
            }
        }

        line("");
        line($"Total time: {total.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s");
        line($"exit={(snapshot.Errors.Count == 0 ? 0 : 1)}");
    }

    private static void Emit(string text, string? outPath, bool toConsole)
    {
        if (outPath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllText(outPath, text, new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                text += $"Could not write {outPath}: {e.Message}\n";
            }
        }

        if (toConsole)
        {
            Console.Out.Write(text.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
            Console.Out.Flush();
        }
    }

    private static void TryCopy(string from, string to)
    {
        try
        {
            File.Copy(from, to, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
