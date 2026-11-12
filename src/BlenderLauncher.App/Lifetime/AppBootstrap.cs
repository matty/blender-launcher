using System.Globalization;
using BlenderLauncher.App.Cli;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Localisation;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Platform.Windows.Ipc;

namespace BlenderLauncher.App.Lifetime;

/// <summary>
/// Hidden <c>--sample-data</c> switch: opens the main window filled with the design's sample data, with no
/// services, no settings file, no network and no single-instance pipe. For screenshots and design comparison.
/// Options: <c>--accent blue|orange</c>, <c>--density comfortable|compact</c>, <c>--view installed:all|download:daily…</c>,
/// <c>--no-details</c>, <c>--system-title-bar</c>, <c>--size 1400x820</c>, <c>--rows 500</c> (adds that many synthetic
/// download rows to the shown download channel, Daily by default, to try the list with many rows).
/// </summary>
public sealed record SampleMode(AppSettings Settings, SidebarView? View, int ExtraRows = 0)
{
    public const string Switch = "--sample-data";

    public static SampleMode? Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!args.Contains(Switch, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var appearance = new AppearanceSettings();
        var window = new WindowPlacement();
        SidebarView? view = null;
        var rows = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : "";
            switch (args[i].ToLowerInvariant())
            {
                case "--accent":
                    appearance = appearance with { Accent = value.Equals("orange", StringComparison.OrdinalIgnoreCase) ? Accent.Orange : Accent.Blue };
                    break;
                case "--density":
                    appearance = appearance with { Density = value.Equals("compact", StringComparison.OrdinalIgnoreCase) ? Density.Compact : Density.Comfortable };
                    break;
                case "--view" when SidebarView.TryParse(value, out var parsed):
                    view = parsed;
                    break;
                case "--no-details":
                    appearance = appearance with { ShowDetailsPanel = false };
                    break;
                case "--size" when value.Split('x') is [var w, var h] && int.TryParse(w, out var width) && int.TryParse(h, out var height):
                    window = window with { Width = width, Height = height };
                    break;
                case "--rows" when int.TryParse(value, out var count) && count > 0:
                    rows = count;
                    break;
                case "--system-title-bar":
                    appearance = appearance with { UseSystemTitleBar = true };
                    break;
            }
        }

        return new SampleMode(new AppSettings { Appearance = appearance, Internal = new InternalSettings { Window = window } }, view, rows);
    }
}

/// <summary>
/// What the app needs before Avalonia starts, loaded synchronously in <c>Main</c> (a few milliseconds): the data folders,
/// the log (open early so the error handlers have it), the settings (the window's placement and theme are applied
/// before it is first shown) and the translator. Owned by <see cref="AppController"/>, which disposes it on exit.
/// </summary>
public sealed class AppBootstrap
{
    private AppBootstrap(DataDirectories directories, Log log, SettingsStore settings, Translator translator,
        InstancePipeServer? pipe, SampleMode? sample, string[] args, CliOptions cli)
    {
        Cli = cli;
        Directories = directories;
        Log = log;
        Settings = settings;
        Translator = translator;
        Pipe = pipe;
        Sample = sample;
        Args = args;
    }

    public DataDirectories Directories { get; }

    public Log Log { get; }

    /// <summary>The settings store; passed on to <c>AppServices</c>, which then owns and disposes it.</summary>
    public SettingsStore Settings { get; }

    public Translator Translator { get; }

    /// <summary>The single-instance pipe this process owns; null in sample and timing runs.</summary>
    public InstancePipeServer? Pipe { get; }

    /// <summary>Set for <c>--sample-data</c>: no services, no settings file.</summary>
    public SampleMode? Sample { get; }

    public bool IsSample => Sample is not null;

    public string[] Args { get; }

    /// <summary>The parsed command line: <c>-debug</c>, <c>-force-first-time</c>, <c>--offline</c>.</summary>
    public CliOptions Cli { get; }

    public static AppBootstrap Load(string[] args, SampleMode? sample, InstancePipeServer? pipe, CliOptions? cli = null)
    {
        cli ??= new CliOptions();
        var directories = DataDirectories.Resolve();
        var log = Log.Open(directories.Logs);
        ErrorHandling.Log = log;
        ErrorHandling.LogFolder = directories.Logs;

        SettingsStore settings;
        if (sample is not null)
        {
            settings = SettingsStore.InMemory(sample.Settings);
        }
        else
        {
            // No synchronisation context exists yet in Main, so blocking here cannot deadlock.
            settings = SettingsStore.LoadAsync(new SettingsStoreOptions
            {
                FilePath = directories.SettingsFile,
                Log = log,
            }).GetAwaiter().GetResult();
        }

        log.MinimumLevel = cli.Debug ? LogLevel.Debug : settings.Current.General.LogLevel;
        var translator = Translator.Create(settings.Current.General.Language, CultureInfo.CurrentUICulture);
        Loc.Initialise(translator);
        log.Info($"{AppIdentity.DisplayName} {AppIdentity.Version} starting (UI language {translator.Language}{(sample is null ? "" : ", sample data")})");
        return new AppBootstrap(directories, log, settings, translator, pipe, sample, args, cli);
    }
}
