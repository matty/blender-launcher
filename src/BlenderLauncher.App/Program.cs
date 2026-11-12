using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using BlenderLauncher.App.Cli;
using BlenderLauncher.App.Diagnostics;
using BlenderLauncher.App.Lifetime;
using BlenderLauncher.App.SelfTest;
using BlenderLauncher.App.Spikes;
using BlenderLauncher.App.Theme;
using BlenderLauncher.Core;
using BlenderLauncher.Platform.Windows.Ipc;

namespace BlenderLauncher.App;

public static class Program
{
    /// <summary>
    /// Entry point. Arguments that change how the process runs are handled here, before Avalonia starts.
    /// The hidden developer switches come first; then the command line (<see cref="CommandLine"/>) is parsed and
    /// dispatched.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // First, always: Velopack's installer, updater and uninstaller start the exe with hook arguments, and this runs
        // the hook and exits. Otherwise it records how this copy was installed (AppLocation).
        VelopackStartup.Run(args);

        if (SelfTestOptions.IsRequested(args))
        {
            return SelfTestHost.Run(args);
        }

        // Developer check of the live scrapers; prints and exits without starting Avalonia.
        if (DevScrape.IsRequested(args))
        {
            return DevScrape.Run(args);
        }

        // Developer checks of the install pipeline and launching.
        if (DevInstall.IsRequested(args))
        {
            return DevInstall.Run(args);
        }

        if (DevLaunch.IsRequested(args))
        {
            return DevLaunch.Run(args);
        }

        // Hidden: opens a spike window (or the theme gallery) for manual checking, for example `--spike titlebar`.
        if (ManualSpikes.GetRequested(args) is { } spike)
        {
            return ManualSpikes.Run(spike, args);
        }

        // The command line, before Avalonia starts.
        var cli = CommandLine.Parse(args);
        if (cli.Error is { } error)
        {
            WindowPositionOverride.Configure(args);
            return CliOutput.UsageError(args, error);
        }

        if (cli.Command == CliCommand.Help)
        {
            WindowPositionOverride.Configure(args);
            return CliOutput.Help(args);
        }

        // Test-only: redirect the shell integration's registry keys, folders and credentials to throwaway ones.
        if (TestIntegrationRoot.Configure(args) is { } rootError)
        {
            return CliOutput.UsageError(args, rootError);
        }

        ErrorHandling.InstallProcessHandlers();
        WindowPositionOverride.Configure(args);
        var sample = SampleMode.Parse(args);

        // Upstream's -set-library-folder: save the library, then continue normal startup (exit 1 if invalid).
        if (sample is null && cli.SetLibraryFolder is { } libraryFolder && SetLibraryFolder.Apply(libraryFolder) is { } failed)
        {
            return failed;
        }

        // Commands; as upstream, they run before the single-instance check.
        switch (cli.Command)
        {
            case CliCommand.Register or CliCommand.Unregister or CliCommand.Uninstall or CliCommand.Update:
                return CliCommands.Run(args, cli);
            case CliCommand.Launch when cli.Cli:
                return CliLaunch.Run(cli);
            case CliCommand.Launch:
                return RunLaunchPicker(args, cli);
        }

        // Single instance. Sample-data runs stay out of it, so they never collide with a real run.
        InstancePipeServer? pipe = null;
        if (sample is null && !cli.Instanced)
        {
            pipe = InstancePipeServer.TryStart(InstancePipeServer.PipeNameFor(), InstanceRequestHub.HandleAsync);
            if (pipe is null && AppRestart.IsRestart(args))
            {
                // "Restart now": the old copy may still be closing; wait for it to release the pipe.
                var deadline = DateTime.UtcNow + AppRestart.PipeRetry;
                while (pipe is null && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(100);
                    pipe = InstancePipeServer.TryStart(InstancePipeServer.PipeNameFor(), InstanceRequestHub.HandleAsync);
                }
            }

            if (pipe is null)
            {
                return SecondInstance.Run(args, new InstanceRequest(InstanceCommand.Show, AppIdentity.Version)) ?? 1;
            }
        }

        App.Bootstrap = AppBootstrap.Load(args, sample, pipe, cli);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>
    /// GUI <c>launch</c> and a double-clicked <c>.blend</c>. When only a file was given (the file association) and a
    /// launcher is running, the file is handed to it over the instance pipe and it opens the picker; otherwise this
    /// process shows the picker itself, without a main window, and exits once it closes.
    /// </summary>
    private static int RunLaunchPicker(string[] args, CliOptions cli)
    {
        var forward = cli is { File: { } file, VersionQuery: null, OpenLast: false, BlenderArgs.Count: 0, Instanced: false };
        if (forward && IsLauncherRunning())
        {
            var request = new InstanceRequest(InstanceCommand.Open, AppIdentity.Version, Path.GetFullPath(cli.File!));
            if (SecondInstance.Run(args, request) is { } code)
            {
                return code;
            }

            // The running launcher did not answer: show the picker here instead.
        }

        return LaunchPicker.RunStandalone(args, cli, LaunchPicker.RequestFor(cli));
    }

    /// <summary>True when another process owns the single-instance pipe (probed by trying to create it).</summary>
    internal static bool IsLauncherRunning()
    {
        var probe = InstancePipeServer.TryStart(InstancePipeServer.PipeNameFor(), static (_, _) => Task.FromResult(""));
        if (probe is null)
        {
            return true;
        }

        probe.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return false;
    }

    /// <summary>Also used by the XAML previewer and the headless tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia()
            .UseHarfBuzz()
            .ConfigureFonts(fonts => fonts.AddFontCollection(new AppFontCollection()))
            .With(AppFonts.CreateFontManagerOptions(CultureInfo.CurrentUICulture))
            .LogToTrace();
}
