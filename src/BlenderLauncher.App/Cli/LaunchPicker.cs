using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using BlenderLauncher.App.Controls;
using BlenderLauncher.App.Lifetime;
using BlenderLauncher.App.Theme;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.App.Views.Dialogs;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Platform.Windows;

namespace BlenderLauncher.App.Cli;

/// <summary>
/// The launch picker's host over <see cref="AppServices"/>: the running app's (a <c>.blend</c> forwarded by a second
/// launch), or services this process starts for itself (<see cref="LaunchPicker.RunStandalone"/>).
/// </summary>
internal sealed class ServicesLaunchPickerHost : ILaunchPickerHost, IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<AppServices>> _services;
    private readonly bool _owns;
    private Task<AppServices>? _start;

    /// <param name="services">Gets (or starts) the services.</param>
    /// <param name="owns">True when the services were started for the picker and are disposed with it.</param>
    public ServicesLaunchPickerHost(Func<CancellationToken, Task<AppServices>> services, bool owns)
    {
        _services = services;
        _owns = owns;
    }

    public async Task<LaunchPickerLibrary> LoadAsync(CancellationToken ct)
    {
        _start ??= _services(ct);
        var services = await _start.ConfigureAwait(false);
        var snapshot = services.Library.Current;
        var entries = snapshot.Entries.Where(e => e.Build is not null).ToList();
        string? defaultFolder = null;
        if (services.Library.DefaultBuild is { } d)
        {
            var wanted = LibraryLayout.NormaliseRoot(d);
            defaultFolder = entries.FirstOrDefault(e => LibraryLayout.PathComparer.Equals(LibraryLayout.NormaliseRoot(e.Path), wanted))?.Path;
        }

        var settings = services.Settings.Current;
        return new LaunchPickerLibrary(entries, defaultFolder, settings.Builds.VersionSpecificQueries, settings.General.LaunchTimerSeconds);
    }

    public void SaveQuery(string versionKey, string queryText)
    {
        if (_start is not { IsCompletedSuccessfully: true } start)
        {
            return;
        }

        start.Result.Settings.Update(s => s with
        {
            Builds = s.Builds with
            {
                VersionSpecificQueries = new Dictionary<string, string>(s.Builds.VersionSpecificQueries, StringComparer.Ordinal) { [versionKey] = queryText },
            },
        });
    }

    public void Launch(BuildInfo build, LaunchMode mode, IReadOnlyList<string> blenderArgs)
    {
        if (_start is not { IsCompletedSuccessfully: true } start)
        {
            throw new InvalidOperationException("The library is not loaded yet.");
        }

        start.Result.Launches.Launch(build, mode, script: null, blenderArgs);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_owns || _start is null)
        {
            return;
        }

        try
        {
            var services = await _start.ConfigureAwait(false);
            await services.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Start failed; it disposed what it had built.
        }
    }
}

/// <summary>
/// The launch picker as its own app mode (GUI <c>launch</c>, and a double-clicked <c>.blend</c> when no launcher is
/// running): no main window, no tray, no single-instance pipe; the process ends when the picker closes.
/// </summary>
internal static class LaunchPicker
{
    /// <summary>Runs the picker; exit code 0 after a launch, 1 when it was closed without one.</summary>
    public static int RunStandalone(string[] args, CliOptions cli, LaunchPickerRequest request)
    {
        var boot = AppBootstrap.Load(args, sample: null, pipe: null, cli);
        boot.Log.Info($"Launch picker for {request.File ?? "(no file)"}{(request.Query is null ? "" : " query " + request.Query)}");
        var launched = false;
        var host = new ServicesLaunchPickerHost(
            ct => AppServices.StartAsync(new AppServicesOptions
            {
                Directories = boot.Directories,
                Settings = boot.Settings,
                Log = boot.Log,
                LogLevel = cli.Debug ? LogLevel.Debug : null,
                Platform = WindowsPlatformServices.Create(),
                StartScraping = false,
                Offline = cli.Offline,
            }, ct),
            owns: true);

        App.MainWindowFactory = () =>
        {
            var settings = boot.Settings.Current;
            ThemeManager.Apply(settings.Appearance);
            WindowChrome.SetUseSystemTitleBar(settings.Appearance.UseSystemTitleBar);
            var viewModel = new LaunchPickerViewModel(host, request);
            var window = new LaunchPickerWindow(viewModel);
            WindowPositionOverride.Apply(window);
            viewModel.Finished += (_, ok) => launched = ok;
            window.Closed += async (_, _) =>
            {
                await host.DisposeAsync().ConfigureAwait(true);
                await boot.Log.DisposeAsync().ConfigureAwait(true);
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown(launched ? 0 : 1);
                }
            };
            return window;
        };

        return Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>The request a parsed <c>launch</c> (or bare file) makes of the picker.</summary>
    public static LaunchPickerRequest RequestFor(CliOptions cli) =>
        new(cli.File, cli.VersionQuery, cli.OpenLast, cli.BlenderArgs);
}
