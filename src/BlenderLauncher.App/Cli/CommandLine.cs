namespace BlenderLauncher.App.Cli;

/// <summary>What the command line asks the process to do.</summary>
public enum CliCommand
{
    /// <summary>No sub-command: the normal app (main window, or the first-run wizard).</summary>
    Gui,

    /// <summary><c>-h</c> / <c>--help</c>.</summary>
    Help,

    /// <summary><c>launch</c>, or a bare existing file path.</summary>
    Launch,

    Register,
    Unregister,
    Uninstall,
    Update,
}

/// <summary>
/// The parsed command line. <see cref="Error"/> is set when the command
/// line is not valid; the process then prints it with the usage and exits with <see cref="CommandLine.UsageExitCode"/>.
/// </summary>
public sealed record CliOptions
{
    public CliCommand Command { get; init; } = CliCommand.Gui;

    /// <summary>A parse error, or null.</summary>
    public string? Error { get; init; }

    /// <summary><c>-d</c>, <c>-debug</c>, <c>--debug</c>: debug logging for this session.</summary>
    public bool Debug { get; init; }

    /// <summary><c>-set-library-folder PATH</c>; quotes are stripped.</summary>
    public string? SetLibraryFolder { get; init; }

    /// <summary><c>-force-first-time</c>: show the first-run wizard even when it was completed.</summary>
    public bool ForceFirstTime { get; init; }

    /// <summary><c>--offline</c>, <c>-offline</c>: no build checks and no launcher update checks this session.</summary>
    public bool Offline { get; init; }

    /// <summary><c>--instanced</c>, <c>-instanced</c>: skip the single-instance check.</summary>
    public bool Instanced { get; init; }

    /// <summary><c>launch -f FILE</c>, or a bare file path; surrounding quotes are stripped.</summary>
    public string? File { get; init; }

    /// <summary>True when <see cref="File"/> came from a bare path (the file association's <c>"exe" "%1"</c>).</summary>
    public bool BareFile { get; init; }

    /// <summary><c>launch -ol</c> / <c>--open-last</c>.</summary>
    public bool OpenLast { get; init; }

    /// <summary><c>launch -v QUERY</c> / <c>--version QUERY</c>.</summary>
    public string? VersionQuery { get; init; }

    /// <summary><c>launch -c</c> / <c>--cli</c>: choose in the console, wait for Blender, exit with its code.</summary>
    public bool Cli { get; init; }

    /// <summary>Arguments for Blender: everything after <c>launch</c>'s options; only the first <c>--</c> is removed.</summary>
    public IReadOnlyList<string> BlenderArgs { get; init; } = [];

    /// <summary><c>uninstall -q</c> / <c>--quiet</c>.</summary>
    public bool Quiet { get; init; }

    /// <summary><c>update [tag]</c>.</summary>
    public string? UpdateTag { get; init; }

    public bool IsValid => Error is null;
}

/// <summary>
/// The hand-written command-line parser: no reflection, so it is safe under Native AOT. Grammar (options are
/// case-insensitive, as Windows users expect):
/// <code>
/// [global options] [command]
/// global:  -h|--help  -d|-debug|--debug  -set-library-folder PATH  -force-first-time  --offline|-offline
///          --instanced|-instanced
/// command: launch [-f|--file FILE | -ol|--open-last] [-v|--version QUERY] [-c|--cli] [--] [blender args…]
///          register | unregister | uninstall [-q|--quiet] | update [TAG] | FILE (an existing file: launch -f FILE)
/// </code>
/// Hidden developer switches (<c>--window-pos X,Y</c>, <c>--sample-data</c> and its options, the timing switches,
/// <c>--restarted</c>, <c>--dev-throw</c>, the self-update's <c>--updated-from VERSION</c>, the test-only
/// <c>--test-integration-root NAME</c>, <c>--update-source FOLDER-OR-URL</c> and <c>--update-test-fail-swap</c>) are accepted anywhere before <c>launch</c>'s Blender arguments and skipped
/// here; their own readers find them in the raw arguments.
/// </summary>
public static class CommandLine
{
    /// <summary>Exit code for a command line that does not parse (argparse's).</summary>
    public const int UsageExitCode = 2;

    // Hidden switches: name to the number of values that follow it.
    private static readonly Dictionary<string, int> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--window-pos"] = 1,
        ["--sample-data"] = 0,
        ["--accent"] = 1,
        ["--density"] = 1,
        ["--view"] = 1,
        ["--no-details"] = 0,
        ["--size"] = 1,
        ["--rows"] = 1,
        ["--system-title-bar"] = 0,
        ["--restarted"] = 0,
        ["--dev-throw"] = 0,
        [TestIntegrationRootSwitch] = 1,
        ["--updated-from"] = 1,
        ["--update-source"] = 1,
        ["--update-test-fail-swap"] = 0,
    };

    /// <summary>
    /// Hidden and test-only: <c>--test-integration-root NAME</c> points every registry key, shortcut folder and credential
    /// the shell integration touches at throwaway locations (<c>IntegrationRoots.Throwaway</c>), so <c>register</c>,
    /// <c>uninstall</c> and the Settings buttons can be verified without changing the user's real profile.
    /// </summary>
    public const string TestIntegrationRootSwitch = "--test-integration-root";

    /// <summary>Parses <paramref name="args"/>; <paramref name="fileExists"/> decides the bare-file form.</summary>
    public static CliOptions Parse(IReadOnlyList<string> args, Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        fileExists ??= System.IO.File.Exists;
        var o = new CliOptions();
        var i = 0;
        for (; i < args.Count; i++)
        {
            var a = args[i];
            if (TrySkipHidden(args, ref i))
            {
                continue;
            }

            switch (a.ToLowerInvariant())
            {
                case "-h" or "--help" or "-help" or "/?":
                    o = o with { Command = CliCommand.Help };
                    continue;
                case "-d" or "-debug" or "--debug":
                    o = o with { Debug = true };
                    continue;
                case "-force-first-time" or "--force-first-time":
                    o = o with { ForceFirstTime = true };
                    continue;
                case "--offline" or "-offline":
                    o = o with { Offline = true };
                    continue;
                case "--instanced" or "-instanced":
                    o = o with { Instanced = true };
                    continue;
                case "-set-library-folder" or "--set-library-folder":
                    if (i + 1 >= args.Count)
                    {
                        return o with { Error = $"{a} needs a folder" };
                    }

                    o = o with { SetLibraryFolder = Unquote(args[++i]) };
                    continue;
            }

            // The first word that is not a global option is the command.
            if (o.Command == CliCommand.Help)
            {
                // Help wins over anything else, as argparse's -h does.
                return o;
            }

            return a.ToLowerInvariant() switch
            {
                "launch" => ParseLaunch(o with { Command = CliCommand.Launch }, args, i + 1),
                "register" => NoMore(o with { Command = CliCommand.Register }, args, i + 1),
                "unregister" => NoMore(o with { Command = CliCommand.Unregister }, args, i + 1),
                "uninstall" => ParseUninstall(o with { Command = CliCommand.Uninstall }, args, i + 1),
                "update" => ParseUpdate(o with { Command = CliCommand.Update }, args, i + 1),
                _ when !a.StartsWith('-') && fileExists(Unquote(a)) =>
                    // Upstream's bare-file shortcut (the file association runs "exe" "%1"). Unlike upstream, the global
                    // options given with it are kept; anything after it is an error.
                    NoMore(o with { Command = CliCommand.Launch, File = Unquote(a), BareFile = true }, args, i + 1),
                _ => o with { Error = a.StartsWith('-') ? $"unrecognised option '{a}'" : $"unrecognised argument '{a}' (not a command or an existing file)" },
            };
        }

        return o;
    }

    private static CliOptions ParseLaunch(CliOptions o, IReadOnlyList<string> args, int i)
    {
        for (; i < args.Count; i++)
        {
            var a = args[i];
            if (TrySkipHidden(args, ref i))
            {
                continue;
            }

            switch (a.ToLowerInvariant())
            {
                case "-f" or "--file":
                    if (o.OpenLast)
                    {
                        return o with { Error = "launch: -f/--file and -ol/--open-last cannot be used together" };
                    }

                    if (i + 1 >= args.Count)
                    {
                        return o with { Error = "launch: -f/--file needs a file" };
                    }

                    o = o with { File = Unquote(args[++i]) };
                    continue;
                case "-ol" or "--open-last":
                    if (o.File is not null)
                    {
                        return o with { Error = "launch: -f/--file and -ol/--open-last cannot be used together" };
                    }

                    o = o with { OpenLast = true };
                    continue;
                case "-v" or "--version":
                    if (i + 1 >= args.Count)
                    {
                        return o with { Error = "launch: -v/--version needs a query" };
                    }

                    o = o with { VersionQuery = args[++i] };
                    continue;
                case "-c" or "--cli":
                    o = o with { Cli = true };
                    continue;
                case "-d" or "-debug" or "--debug":
                    o = o with { Debug = true };
                    continue;
            }

            // argparse.REMAINDER: the rest goes to Blender, with only the first "--" removed.
            var rest = args.Skip(i).ToList();
            if (rest.Count > 0 && rest[0] == "--")
            {
                rest.RemoveAt(0);
            }
            else if (rest.Count > 0 && rest[0].StartsWith('-'))
            {
                return o with { Error = $"launch: unrecognised option '{rest[0]}' (put Blender's arguments after --)" };
            }

            return o with { BlenderArgs = rest };
        }

        return o;
    }

    private static CliOptions ParseUninstall(CliOptions o, IReadOnlyList<string> args, int i)
    {
        for (; i < args.Count; i++)
        {
            if (TrySkipHidden(args, ref i))
            {
                continue;
            }

            if (args[i].ToLowerInvariant() is "-q" or "--quiet")
            {
                o = o with { Quiet = true };
                continue;
            }

            return o with { Error = $"uninstall: unrecognised argument '{args[i]}'" };
        }

        return o;
    }

    private static CliOptions ParseUpdate(CliOptions o, IReadOnlyList<string> args, int i)
    {
        for (; i < args.Count; i++)
        {
            if (TrySkipHidden(args, ref i))
            {
                continue;
            }

            if (o.UpdateTag is null && !args[i].StartsWith('-'))
            {
                o = o with { UpdateTag = args[i] };
                continue;
            }

            return o with { Error = $"update: unrecognised argument '{args[i]}'" };
        }

        return o;
    }

    private static CliOptions NoMore(CliOptions o, IReadOnlyList<string> args, int i)
    {
        for (; i < args.Count; i++)
        {
            if (TrySkipHidden(args, ref i))
            {
                continue;
            }

            // Global options are also accepted after the command (for example "file.blend -debug").
            switch (args[i].ToLowerInvariant())
            {
                case "-d" or "-debug" or "--debug":
                    o = o with { Debug = true };
                    continue;
                case "--offline" or "-offline":
                    o = o with { Offline = true };
                    continue;
                case "--instanced" or "-instanced":
                    o = o with { Instanced = true };
                    continue;
            }

            return o with { Error = $"unrecognised argument '{args[i]}'" };
        }

        return o;
    }

    private static bool TrySkipHidden(IReadOnlyList<string> args, ref int i)
    {
        if (!Hidden.TryGetValue(args[i], out var values))
        {
            return false;
        }

        i = Math.Min(i + values, args.Count - 1);
        return true;
    }

    private static string Unquote(string value) => value.Trim().Trim('"');

    /// <summary>The help text (upstream's argparse help, in the port's words).</summary>
    public static string Usage(string exeName) =>
        $"""
        Usage: "{exeName}" [options] [command]

        Options:
          -h, --help                 Show this help and exit
          -d, -debug, --debug        Write debug messages to the log
          -set-library-folder PATH   Use PATH as the library folder, then start normally
          -force-first-time          Show the first-run setup even if it was completed
          --offline, -offline        Do not check for new builds or launcher updates
          --instanced, -instanced    Do not hand over to a running launcher

        Commands:
          FILE                       Open FILE (a .blend) with a matching build (same as launch -f FILE)
          launch                     Launch a build
              -f, --file FILE        Open FILE with the build
              -ol, --open-last       Open the last file (cannot be used with -f)
              -v, --version QUERY    Choose the build with a version query
              -c, --cli              Choose in this console, wait for Blender and exit with its exit code
              -- ARGS...             Pass ARGS to Blender (everything after the first --)
          register                   Register .blend files with this launcher
          unregister                 Remove the .blend file registration
          uninstall [-q, --quiet]    Remove the launcher's registrations, shortcuts and data (never the library)
          update [TAG]               Update the launcher to the newest release (TAG, if given, must name it)

        Version queries: <major>.<minor>[.<patch>][-<branch>[,<branch>...]][+<hash>][@<commit time>]
          Each of major, minor and patch is ^ (newest), - (oldest), * (any) or a number; the commit time is
          ^, -, * or an ISO date and time. Examples:
            4.2.^            the newest 4.2 build
            ^.^.^-lts        the newest LTS build
            ^.^.*-daily@^    the most recent daily build
            *.*.*+cb886aba06d5    the build with that hash

        Without a file or a query, launch uses the default build. With a file, it uses the query saved for the
        file's Blender version, else the newest build of that version, else the default build.

        Exit codes: 0 success; 1 failure (nothing matched, a bad query or folder); 2 a bad command line.
        launch -c exits with Blender's exit code. It is best run from an existing console; in cmd use
        start /wait "" "{exeName}" launch -c ..., in PowerShell pipe it: & '.\{exeName}' launch -c ... | Out-Host
        """;
}
