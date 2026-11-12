using System.Globalization;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Versioning;
using BlenderLauncher.Platform.Windows;
using BlenderLauncher.Platform.Windows.Cli;
using BlenderLauncher.Platform.Windows.Processes;

namespace BlenderLauncher.App.Cli;

/// <summary>
/// <c>launch -c</c> (upstream <c>modules/cli_launching.py</c>): chooses a build in the console, launches it,
/// waits, and exits with Blender's exit code. The build comes from <see cref="BlendLaunchResolver"/>: an explicit
/// <c>-v</c> query; else, with a file, the query saved for its version or <c>major.minor.^</c>; else the default
/// build; else <c>^.^.^@^</c>. Several matches are listed and the user picks one by number.
/// </summary>
/// <remarks>
/// <para>Exit codes: Blender's own; 1 when nothing matched, the query was invalid, no build could be chosen or
/// Blender could not be started.</para>
/// <para>The exe uses the GUI subsystem, so cmd and PowerShell do not wait for it and keep reading the same console.
/// It is best run from an existing console with <c>start /wait "" "Blender Launcher.exe" launch -c …</c> (cmd) or
/// piped, <c>&amp; '.\Blender Launcher.exe' launch -c … | Out-Host</c> (PowerShell). With a console (or redirected
/// output) Blender inherits it, so its output appears there; started without one, Blender gets its own console as in
/// a normal launch.</para>
/// </remarks>
internal static class CliLaunch
{
    public static int Run(CliOptions cli)
    {
        var attach = ParentConsole.TryAttach(out _);
        var console = attach is ConsoleAttachOutcome.Attached or ConsoleAttachOutcome.Redirected or ConsoleAttachOutcome.AlreadyAttached;
        if (attach == ConsoleAttachOutcome.Attached)
        {
            ParentConsole.EnsureConsoleInput();
        }

        try
        {
            return RunAsync(cli, console).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Error(console, $"launch: {e.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(CliOptions cli, bool console)
    {
        VersionSearchQuery? explicitQuery = null;
        if (cli.VersionQuery is { } text && !VersionSearchQuery.TryParse(text, out explicitQuery))
        {
            Error(console, $"Invalid version search query: {text}");
            Error(console, $"Syntax: {VersionSearchQuery.Syntax}");
            Error(console, "Examples: 4.2.^   ^.^.^-lts   ^.^.*-daily@^   *.*.*+cb886aba06d5");
            return 1;
        }

        await using var services = await AppServices.StartAsync(new AppServicesOptions
        {
            Platform = WindowsPlatformServices.Create(),
            StartScraping = false,
            Offline = true,
            LogLevel = cli.Debug ? LogLevel.Debug : null,
        }).ConfigureAwait(false);

        var builds = new List<(LibraryEntry Entry, VersionSearchCandidate Candidate)>();
        foreach (var entry in services.Library.Current.Entries)
        {
            if (entry.Build is { } build && VersionSearchCandidate.TryFromBuildInfo(build, out var candidate, entry.Path))
            {
                builds.Add((entry, candidate));
            }
        }

        if (builds.Count == 0)
        {
            Error(console, services.LibraryRoot is null ? "No library folder is set." : $"No builds are installed in {services.LibraryRoot}.");
            return 1;
        }

        BlendFileHeader? header = null;
        string? file = null;
        if (cli.File is { } f)
        {
            file = Path.GetFullPath(f);
            try
            {
                header = await BlendFileHeaderReader.ReadAsync(file).ConfigureAwait(false);
                Line(console, $"{Path.GetFileName(file)}: made in Blender {header.Major}.{header.Minor}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotABlendFileException or NotSupportedException)
            {
                Line(console, $"{Path.GetFileName(file)}: the Blender version could not be read ({e.Message})");
            }
        }

        string? defaultFolder = null;
        if (services.Library.DefaultBuild is { } d)
        {
            var wanted = LibraryLayout.NormaliseRoot(d);
            defaultFolder = builds.Select(b => b.Entry.Path).FirstOrDefault(p => LibraryLayout.PathComparer.Equals(LibraryLayout.NormaliseRoot(p), wanted));
        }

        var settings = services.Settings.Current;
        var resolution = BlendLaunchResolver.Resolve(
            builds, b => b.Candidate, header, BlendLaunchResolver.ParseSavedQueries(settings.Builds.VersionSpecificQueries),
            explicitQuery, defaultFolder);
        var matches = resolution.Matches.OrderByDescending(m => m.Candidate).ToList();
        var described = resolution.Source == BlendLaunchSource.DefaultBuild ? "the default build" : resolution.Query.ToString();
        if (matches.Count == 0)
        {
            Error(console, $"No installed build matches {described}.");
            return 1;
        }

        var chosen = matches[0];
        if (matches.Count > 1)
        {
            Line(console, $"{matches.Count} builds match {described}:");
            for (var i = 0; i < matches.Count; i++)
            {
                Line(console, string.Create(CultureInfo.InvariantCulture, $"  {i + 1,2}) {Describe(matches[i].Entry)}"));
            }

            if (Choose(console, matches.Count) is not { } index)
            {
                Error(console, "No build chosen.");
                return 1;
            }

            chosen = matches[index];
        }

        var mode = file is not null ? LaunchMode.WithBlendFile(file)
            : cli.OpenLast ? LaunchMode.OpenLast
            : LaunchMode.Plain;
        var folder = LibraryLayout.NormaliseRoot(chosen.Entry.Path);
        var command = LaunchArgsBuilder.Build(
            LaunchRequest.For(chosen.Entry.Build! with { Link = folder }) with
            {
                StartupArguments = settings.Launching.BlenderStartupArguments,
                Mode = mode,
                HideConsole = settings.Launching.HideConsole,
                ExtraArguments = cli.BlenderArgs,
            },
            File.Exists);

        Line(console, $"Launching {Describe(chosen.Entry)}");
        services.Log.Info($"launch -c: {command.CommandLine} in {folder}");
        int exitCode;
        if (console)
        {
            // Blender shares this console (or the redirected output), as upstream's Popen(shell=True) did.
            var application = command.UsesCmd ? WindowsProcessLauncher.ComSpec() : command.ApplicationName;
            using var process = SharedConsoleLauncher.Start(application, command.CommandLine, folder);
            MarkLaunched(services, folder);
            exitCode = await process.WaitForExitAsync().ConfigureAwait(false);
        }
        else
        {
            using var process = services.Platform.ProcessLauncher.Start(command, folder);
            MarkLaunched(services, folder);
            exitCode = await process.WaitForExitAsync().ConfigureAwait(false);
        }

        services.Log.Info(string.Create(CultureInfo.InvariantCulture, $"launch -c: Blender exited with {exitCode}"));
        return exitCode;
    }

    private static string Describe(LibraryEntry entry)
    {
        var b = entry.Build!;
        var date = b.CommitTime.ToDateTimeOffset(TimeZoneInfo.Local).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return $"{b.DisplayLabel}  {b.Subversion}  {b.Branch}  {date}  ({entry.Name})";
    }

    /// <summary>Asks for a number until one is valid; null at the end of input.</summary>
    private static int? Choose(bool console, int count)
    {
        if (!console)
        {
            return null;
        }

        while (true)
        {
            Console.Out.Write(string.Create(CultureInfo.InvariantCulture, $"Choose a build [1-{count}]: "));
            Console.Out.Flush();
            var line = Console.In.ReadLine();
            if (line is null)
            {
                return null;
            }

            if (int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= count)
            {
                return n - 1;
            }
        }
    }

    private static void MarkLaunched(AppServices services, string folder)
    {
        try
        {
            services.Library.MarkLaunched(folder);
        }
        catch (InvalidOperationException)
        {
            // Outside the library (should not happen): nothing to stamp.
        }
    }

    private static void Line(bool console, string text)
    {
        if (console)
        {
            Console.Out.WriteLine(text);
            Console.Out.Flush();
        }
    }

    private static void Error(bool console, string text)
    {
        if (console)
        {
            Console.Error.WriteLine(text);
            Console.Error.Flush();
        }
    }
}
