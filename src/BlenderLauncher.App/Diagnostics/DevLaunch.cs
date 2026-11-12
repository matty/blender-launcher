using System.Diagnostics;
using System.Globalization;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Processes;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Core.Versioning;
using BlenderLauncher.Platform.Windows;

namespace BlenderLauncher.App.Diagnostics;

/// <summary>
/// Developer switch: <c>"Blender Launcher.exe" --dev-launch &lt;folder-or-query&gt;
/// [--library &lt;dir&gt;] [--wait &lt;seconds&gt;] [--out &lt;file&gt;]</c>. Opens the library (by default the
/// folder two levels above a build folder), finds the build (a folder, or the newest installed build matching a
/// version query), launches it through <see cref="LaunchService"/>, reports the process id, waits (10 s by
/// default), then sends "Quit all" and reports the exit. Exit code 0 when the build started and closed after "Quit
/// all", 1 otherwise, 2 for a bad command line.
/// </summary>
internal static class DevLaunch
{
    public const string Switch = "--dev-launch";

    private static readonly TimeSpan ExitLimit = TimeSpan.FromSeconds(60);

    public static bool IsRequested(string[] args) =>
        args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        var outPath = DevOutput.Option(args, "--out") is { } o ? Path.GetFullPath(o) : null;
        using var output = new DevOutput(outPath);
        var target = DevOutput.Positional(args, Switch, ["--out", "--library", "--wait", "--data"]);
        var waitText = DevOutput.Option(args, "--wait") ?? "10";
        if (target is null || !double.TryParse(waitText, NumberStyles.Float, CultureInfo.InvariantCulture, out var wait) || wait < 0)
        {
            output.Line($"Usage: \"{AppIdentity.ExecutableName}\" {Switch} <folder-or-query> [--library <dir>] [--wait <seconds>] [--out <file>]");
            return 2;
        }

        try
        {
            return RunAsync(target, DevOutput.Option(args, "--library"), TimeSpan.FromSeconds(wait), outPath, output).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            output.Line($"{Switch} failed: {e}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string target, string? libraryArg, TimeSpan wait, string? outPath, DevOutput output)
    {
        var isFolder = Directory.Exists(target);
        string? library = libraryArg is not null ? Path.GetFullPath(libraryArg)
            : isFolder ? Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(target)))
            : null;
        if (library is null)
        {
            output.Line($"{Switch}: --library is required with a query");
            return 2;
        }

        var dataRoot = Path.Combine(Path.GetTempPath(), $"{AppIdentity.TechnicalId}-dev-data-{Guid.NewGuid():N}");
        output.Line($"{AppIdentity.DisplayName} {AppIdentity.Version} {Switch} {target}");
        output.Line($"Library: {library}");

        var services = await AppServices.StartAsync(new AppServicesOptions
        {
            Directories = new DataDirectories(dataRoot),
            Settings = SettingsStore.InMemory(AppSettings.Default),
            Platform = WindowsPlatformServices.Create(),
            LibraryOverride = library,
            StartScraping = false,
            LogLevel = LogLevel.Debug,
        }).ConfigureAwait(false);
        try
        {
            var builds = services.Library.Current.Entries.Where(e => e.Build is not null).ToList();
            output.Line($"Installed builds: {builds.Count}");
            LibraryEntry? entry;
            if (isFolder)
            {
                entry = services.Library.Current.Find(target);
            }
            else
            {
                var query = VersionSearchQuery.Parse(target);
                entry = query.Match(builds.Where(e => e.Build!.FullSemVersion is not null), e => VersionSearchCandidate.FromBuildInfo(e.Build!, e.Path))
                    .MaxBy(e => VersionSearchCandidate.FromBuildInfo(e.Build!, e.Path));
            }

            if (entry?.Build is not { } build)
            {
                output.Line($"No installed build found for {target}");
                return 1;
            }

            output.Line($"Build: {build.Subversion} ({build.Branch}) at {entry.Path}");
            var exited = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
            services.Launches.RunningCountChanged += (_, e) =>
            {
                if (e.ExitCode is not null || (e.Count == 0 && LibraryLayout.PathComparer.Equals(e.BuildFolder, entry.Path)))
                {
                    output.Line($"  process {e.ProcessId} exited with {e.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; running now {e.Count}");
                    if (e.Count == 0)
                    {
                        exited.TrySetResult(e.ExitCode);
                    }
                }
            };
            services.Launches.Launched += (_, e) => output.Line($"  launched pid {e.Process.ProcessId}{(e.Process.ThroughCmd ? " (cmd.exe)" : "")}; running {e.RunningCount}; on-launch action {e.Action}");

            var clock = Stopwatch.StartNew();
            var process = services.Launches.Launch(build);
            output.Line($"Started pid {process.ProcessId}; waiting {DevOutput.Seconds(wait)}");
            if (await Task.WhenAny(exited.Task, Task.Delay(wait)).ConfigureAwait(false) == exited.Task)
            {
                output.Line($"The build exited by itself after {DevOutput.Seconds(clock.Elapsed)} (exit {exited.Task.Result})");
                return 1;
            }

            var running = services.Launches.GetRunningCount(entry.Path);
            output.Line($"Still running after {DevOutput.Seconds(clock.Elapsed)}: {running} process(es); windows: {VisibleWindowCount(process.ProcessId)}");
            var quit = services.Launches.QuitAll(entry.Path);
            output.Line($"Quit all: {quit.Processes} process(es), {quit.WindowsAsked} window(s) asked to close");
            var closeClock = Stopwatch.StartNew();
            if (await Task.WhenAny(exited.Task, Task.Delay(ExitLimit)).ConfigureAwait(false) != exited.Task)
            {
                output.Line($"The build did not exit within {DevOutput.Seconds(ExitLimit)} of Quit all");
                return 1;
            }

            output.Line($"Exited {DevOutput.Seconds(closeClock.Elapsed)} after Quit all with code {exited.Task.Result?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
            output.Line($"Last used (sidecar): {services.Library.GetState(entry.Path).LastUsed:O}");
            output.Line(quit.WindowsAsked > 0 ? "exit=0" : "exit=1");
            return quit.WindowsAsked > 0 ? 0 : 1;
        }
        finally
        {
            var logFile = services.Log.FilePath;
            await services.DisposeAsync().ConfigureAwait(false);
            if (outPath is not null)
            {
                DevOutput.TryCopy(logFile, outPath + ".log");
            }

            DevOutput.TryDelete(dataRoot);
        }
    }

    private static int VisibleWindowCount(int pid) =>
        Platform.Windows.Processes.WindowsProcessLauncher.FindTopLevelWindows([pid]).Count;
}
