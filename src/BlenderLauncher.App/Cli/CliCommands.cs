using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using BlenderLauncher.App.Lifetime;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.ViewModels.Detail;
using BlenderLauncher.App.Views.Dialogs;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Integration;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Core.Updates;
using BlenderLauncher.Platform.Windows;
using BlenderLauncher.Platform.Windows.Integration;

namespace BlenderLauncher.App.Cli;

/// <summary>
/// <c>register</c>, <c>unregister</c>, <c>uninstall [-q]</c> and <c>update [tag]</c>. They run
/// before the single-instance check, as upstream's.
/// </summary>
internal static class CliCommands
{
    public static int Run(string[] args, CliOptions cli) => cli.Command switch
    {
        CliCommand.Register => Register(),
        CliCommand.Unregister => Unregister(),
        CliCommand.Uninstall => CliUninstall.Run(args, cli),
        CliCommand.Update => CliUpdate.Run(args, cli),
        _ => CommandLine.UsageExitCode,
    };

    /// <summary><c>register</c>: puts this exe in the <c>.blend</c> "Open with" list.</summary>
    private static int Register()
    {
        var exe = LauncherExe.Path;
        return Try("register", () =>
        {
            var platform = WindowsPlatformServices.Create();
            platform.FileAssociation.Register(exe);
            CliOutput.Line($"Registered {exe} for .blend files: {AppIdentity.DisplayName} is in their \"Open with\" list.");
        });
    }

    /// <summary><c>unregister</c>: removes the launcher's ProgID and its <c>OpenWithProgids</c> values (also <c>.blend1</c>).</summary>
    private static int Unregister() => Try("unregister", () =>
    {
        WindowsPlatformServices.Create().FileAssociation.Unregister();
        CliOutput.Line($"{AppIdentity.DisplayName} is no longer registered for .blend files.");
    });

    private static int Try(string command, Action action)
    {
        try
        {
            action();
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            CliOutput.Error($"{command} failed: {e.Message}");
            return 1;
        }
    }
}

/// <summary>
/// <c>update [tag]</c>: updates this copy, through Velopack, to the newest release of the channel set in
/// Settings. A tag is accepted only when it names that release (Velopack installs the newest one); an older tag is
/// pointed at its release page. The progress window is shown either way; with a console the steps and any error are
/// also printed there, without one errors are dialogs. On success Velopack's Update.exe installs the release once this
/// process has exited (with 0) and starts the new version. A copy Velopack does not manage is sent to the release page.
/// </summary>
internal static class CliUpdate
{
    public static int Run(string[] args, CliOptions cli)
    {
        var console = CliOutput.HasConsole;
        return CliUninstall.RunWithDialogs(args, () => RunAsync(args, cli, console));
    }

    private static async Task<int> RunAsync(string[] args, CliOptions cli, bool console)
    {
        // A running launcher may be downloading, and its process would keep the old version alive; the test source is
        // exempt so a verification run is not blocked by the user's own launcher.
        if (!SelfUpdate.HasTestSource(args) && Program.IsLauncherRunning())
        {
            return await FailAsync(console, Loc.Tr("ui.update.close_launcher")).ConfigureAwait(true);
        }

        if (!AppLocation.IsVelopack)
        {
            if (console)
            {
                CliOutput.Error(Loc.Tr("ui.update.not_managed"));
                CliOutput.Error(SelfUpdate.ReleasePage(cli.UpdateTag));
            }
            else
            {
                await SelfUpdate.ShowNotManagedAsync(cli.UpdateTag, null).ConfigureAwait(true);
            }

            return 1;
        }

        var services = await Task.Run(() => AppServices.StartAsync(new AppServicesOptions
        {
            Platform = WindowsPlatformServices.Create(),
            StartScraping = false,
            ScanLibrary = false,
            LogLevel = cli.Debug ? Core.Logging.LogLevel.Debug : null,
        })).ConfigureAwait(true);
        try
        {
            var log = services.Log;
            var manager = SelfUpdate.CreateManager(args, () => services.Http.Client, services.Settings.Current.General.UsePreReleaseBuilds, log);
            if (manager is null)
            {
                return await FailAsync(console, "Self-update is switched off in this build.").ConfigureAwait(true);
            }

            // Velopack installs the newest release of the channel; the update check names it (and its notes) first.
            string version;
            if (SelfUpdate.HasTestSource(args))
            {
                version = cli.UpdateTag ?? "the newest test release";
            }
            else
            {
                var (latest, message) = await LatestAsync(services).ConfigureAwait(true);
                if (latest is null)
                {
                    if (message is null)
                    {
                        Say(console, Loc.Tr("ui.update.up_to_date", ("version", AppIdentity.Version)));
                        return 0;
                    }

                    return await FailAsync(console, message).ConfigureAwait(true);
                }

                if (cli.UpdateTag is { } wanted && !string.Equals(wanted.TrimStart('v'), latest.Tag.TrimStart('v'), StringComparison.OrdinalIgnoreCase))
                {
                    return await FailAsync(console,
                        $"update installs the newest release, {latest.Tag}. To install {wanted}, download it from {SelfUpdate.ReleasePage(wanted)}").ConfigureAwait(true);
                }

                version = latest.Version.ToString();
            }

            Say(console, $"Updating {AppIdentity.DisplayName} {AppIdentity.Version} to {version}");
            UpdateStage? last = null;
            Velopack.VelopackAsset? package = null;
            var result = await UpdateProgressWindow.RunAsync(null, version, async (progress, ct) =>
            {
                var tee = new Tee(progress, p =>
                {
                    if (console && p.Stage != last)
                    {
                        last = p.Stage;
                        CliOutput.Line("  " + p.Stage);
                    }
                });
                (var outcome, package) = await SelfUpdate.PrepareAsync(manager, tee, log, ct).ConfigureAwait(false);
                return outcome;
            }).ConfigureAwait(true);

            switch (result.Outcome)
            {
                case UpdateOutcome.Cancelled:
                    Say(console, "Cancelled; nothing was changed.");
                    return 1;
                case UpdateOutcome.UpToDate:
                    Say(console, Loc.Tr("ui.update.up_to_date", ("version", AppIdentity.Version)));
                    return 0;
                case UpdateOutcome.Ready when package is not null:
                    break;
                default:
                    return await FailAsync(console, Loc.Tr("ui.update.failed", ("version", version)), result.Error, services.Directories.Logs).ConfigureAwait(true);
            }

            // Update.exe waits for this process to exit, installs the package and starts the new version.
            if (!SelfUpdate.ApplyAndRestart(manager, package, args, log))
            {
                return await FailAsync(console, Loc.Tr("ui.update.apply_failed", ("version", version)), null, services.Directories.Logs).ConfigureAwait(true);
            }

            Say(console, $"Installing {package.Version}; Blender Launcher starts again when it is done.");
            return 0;
        }
        finally
        {
            await services.DisposeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The newest release when it is newer than this version, from the update check over GitHub with the channel from
    /// Settings. (null, null) means up to date; (null, text) is a failure.
    /// </summary>
    private static async Task<(AppRelease? Latest, string? Error)> LatestAsync(AppServices services)
    {
        var check = await services.Releases.CheckAsync(AppIdentity.Version, services.Settings.Current.General.UsePreReleaseBuilds).ConfigureAwait(true);
        return check.Status switch
        {
            UpdateCheckStatus.UpdateAvailable => (check.Latest, null),
            UpdateCheckStatus.UpToDate => (null, null),
            UpdateCheckStatus.NoRelease => (null, "No release has been published yet."),
            UpdateCheckStatus.SkippedDevBuild => (null, $"{AppIdentity.Version} is a development build, which does not update itself."),
            UpdateCheckStatus.Disabled => (null, "Self-update is switched off in this build."),
            _ => (null, $"The update check failed: {check.Error?.Detail ?? "unknown error"}"),
        };
    }

    private static void Say(bool console, string text)
    {
        if (console)
        {
            CliOutput.Line(text);
        }
    }

    private static async Task<int> FailAsync(bool console, string message, string? details = null, string? logFolder = null)
    {
        if (console)
        {
            CliOutput.Error(details is null ? message : $"{message}\n{details}");
        }
        else
        {
            await DialogService.ErrorAsync(Loc.Tr("ui.update.failed_title"), message, details, logFolder).ConfigureAwait(true);
        }

        return 1;
    }

    /// <summary>Reports to the window and to the console.</summary>
    private sealed class Tee(IProgress<UpdateProgress> first, Action<UpdateProgress> second) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value)
        {
            first.Report(value);
            second(value);
        }
    }
}

/// <summary>
/// The hidden, test-only <c>--test-integration-root NAME</c> switch: <see cref="IntegrationRoots.Current"/> becomes
/// <see cref="IntegrationRoots.Throwaway"/>, so registry keys, shortcut folders and credentials go to throwaway places.
/// </summary>
internal static class TestIntegrationRoot
{
    /// <summary>Applies the switch when present; returns an error text for a bad name, else null.</summary>
    public static string? Configure(string[] args)
    {
        var at = Array.FindIndex(args, a => a.Equals(CommandLine.TestIntegrationRootSwitch, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            return null;
        }

        if (at + 1 >= args.Length)
        {
            return $"{CommandLine.TestIntegrationRootSwitch} needs a name";
        }

        try
        {
            IntegrationRoots.Current = IntegrationRoots.Throwaway(args[at + 1]);
            return null;
        }
        catch (ArgumentException e)
        {
            return e.Message;
        }
    }
}

/// <summary>
/// <c>uninstall [-q]</c>. Asks first unless <c>-q</c>: in the console it was started from, or in a dialog when
/// there is none (upstream answered "yes" by itself when it had no input). Then <see cref="Uninstaller"/> removes the
/// registrations, shortcuts, credentials and data folder, never the library, and the exe folder is deleted by a
/// detached <c>cmd</c> after a short delay when it holds only the launcher's own files.
/// </summary>
internal static class CliUninstall
{
    public static int Run(string[] args, CliOptions cli)
    {
        var console = CliOutput.HasConsole;
        var gui = !console && !cli.Quiet;

        // Installed by Setup.exe: Velopack's uninstaller asks, removes its shortcuts, Add/Remove Programs entry and the
        // install folder, and calls RunForVelopackUninstall first for what the launcher registered itself.
        if (AppLocation.Kind == InstallKind.Installed && IntegrationRoots.Current.TestName is null)
        {
            if (VelopackStartup.StartUninstall(cli.Quiet))
            {
                return 0;
            }

            CliOutput.Error("Could not start Velopack's uninstaller; use Settings → Apps → Installed apps instead.");
            return 1;
        }

        var directories = DataDirectories.Resolve();
        var library = ReadLibrary(directories);

        if (IntegrationRoots.Current.TestName is null && Program.IsLauncherRunning())
        {
            if (gui)
            {
                return RunWithDialogs(args, async () =>
                {
                    await DialogService.WarningAsync(Loc.Tr("ui.integration.uninstall_title"), Loc.Tr("ui.integration.uninstall_running")).ConfigureAwait(true);
                    return 1;
                });
            }

            CliOutput.Error(Loc.Tr("ui.integration.uninstall_running"));
            return 1;
        }

        var question = library is null
            ? Loc.Tr("ui.integration.uninstall_confirm_no_library")
            : Loc.Tr("ui.integration.uninstall_confirm", ("library", library));
        if (gui)
        {
            return RunWithDialogs(args, async () =>
            {
                var confirmed = await DialogService.ConfirmAsync(Loc.Tr("ui.integration.uninstall_title"), question,
                    Loc.Tr("ui.integration.uninstall"), Loc.Tr("act.cancel"), danger: true).ConfigureAwait(true);
                if (!confirmed)
                {
                    return 1;
                }

                var report = await Task.Run(() => Execute(directories, library)).ConfigureAwait(true);
                await DialogService.ShowAsync(new DialogRequest
                {
                    Kind = report.Succeeded ? DialogKind.Success : DialogKind.Warning,
                    Title = Loc.Tr(report.Succeeded ? "ui.integration.uninstall_done" : "ui.integration.uninstall_failed"),
                    Message = Loc.Tr("ui.integration.uninstall_title"),
                    ScrollText = string.Join(Environment.NewLine, Describe(report)),
                    Buttons = DialogService.OkButtons(),
                    Width = 640,
                }).ConfigureAwait(true);
                return report.Succeeded ? 0 : 1;
            });
        }

        if (!cli.Quiet)
        {
            CliOutput.Line(question);
            Console.Out.Write("Uninstall? [y/N] ");
            Console.Out.Flush();
            var answer = Console.In.ReadLine()?.Trim();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                CliOutput.Line("Cancelled; nothing was changed.");
                return 1;
            }
        }

        var result = Execute(directories, library);
        foreach (var line in Describe(result))
        {
            CliOutput.Line(line);
        }

        return result.Succeeded ? 0 : 1;
    }

    /// <summary>The library folder from the settings (relative paths against the exe folder), or null.</summary>
    private static string? ReadLibrary(DataDirectories directories)
    {
        if (!File.Exists(directories.SettingsFile))
        {
            return null;
        }

        var store = SettingsStore.LoadAsync(new SettingsStoreOptions { FilePath = directories.SettingsFile }).GetAwaiter().GetResult();
        try
        {
            return store.Current.General.LibraryFolder is { } folder ? LibraryLayout.Resolve(folder, AppLocation.BaseDirectory) : null;
        }
        finally
        {
            store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Velopack's before-uninstall hook: everything <c>uninstall</c> removes except the program files, which Velopack
    /// deletes itself. No questions and no output: the hook has 30 seconds and no console.
    /// </summary>
    public static void RunForVelopackUninstall()
    {
        var directories = DataDirectories.Resolve();
        Execute(directories, ReadLibrary(directories));
    }

    private static UninstallReport Execute(DataDirectories directories, string? library)
    {
        var platform = WindowsPlatformServices.Create();
        try
        {
            var report = Uninstaller.Run(new UninstallContext
            {
                Platform = platform,
                Directories = directories,
                LibraryRoot = library,
                ExePath = LauncherExe.Path,
                ShippedFileNames = LauncherExe.ShippedFileNames,
                ProtectedFolders = ProtectedFolders(),
            });
            // Under Velopack the exe folder is its "current" folder: the installer deletes it, or (portable) the user
            // deletes the whole extracted folder, which also holds Update.exe.
            if (report.ExeFolderFiles is { } files && !AppLocation.IsVelopack)
            {
                ScheduleSelfDelete(files, Path.GetDirectoryName(LauncherExe.Path)!);
            }

            return report;
        }
        finally
        {
            platform.Hotkey.Dispose();
        }
    }

    private static IEnumerable<string> Describe(UninstallReport report)
    {
        foreach (var step in report.Steps)
        {
            yield return step.Text;
        }

        yield return report.LibraryKept is { } library
            ? $"Library kept: {library}"
            : "No library folder was set; nothing else was touched.";
        if (AppLocation.Kind == InstallKind.Portable)
        {
            yield return $"Delete the folder {AppLocation.BaseDirectory} to remove the launcher's program files.";
            yield break;
        }

        var folder = Path.GetDirectoryName(LauncherExe.Path);
        yield return report.RemoveExeFolder
            ? $"The launcher's folder {folder} is deleted in a few seconds."
            : $"The launcher's files in {folder} were left in place ({report.ExeFolderKeptReason}); delete {Path.GetFileName(LauncherExe.Path)} yourself.";
    }

    /// <summary>Folders the exe folder must never be or contain.</summary>
    private static IReadOnlyCollection<string> ProtectedFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string>
        {
            profile,
            Path.Combine(profile, "Downloads"),
            Path.GetTempPath(),
        };
        list.AddRange(new[]
        {
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.Programs, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
        }.Select(f => Environment.GetFolderPath(f)));
        return list.Where(p => p.Length > 0).ToList();
    }

    /// <summary>
    /// Upstream's self-delete: a hidden, detached <c>cmd</c> waits about two seconds (this process has exited by then),
    /// deletes the listed files and removes the folder if it is empty. It runs from the temp folder so it does not hold
    /// the folder open.
    /// </summary>
    private static void ScheduleSelfDelete(IReadOnlyList<string> files, string folder)
    {
        var del = files.Count == 0 ? "" : " & del /f /q " + string.Join(' ', files.Select(f => "\"" + f + "\""));
        var arguments = string.Create(CultureInfo.InvariantCulture, $"/d /c ping -n 3 127.0.0.1 >nul{del} & rmdir \"{folder}\"");
        using var _ = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    /// <summary>Runs Avalonia without a lifetime for a sequence of dialogs, then returns <paramref name="flow"/>'s code.</summary>
    internal static int RunWithDialogs(string[] args, Func<Task<int>> flow)
    {
        _ = args;
        Program.BuildAvaloniaApp().SetupWithoutStarting();
        var exit = 1;
        using var done = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                exit = await flow().ConfigureAwait(true);
            }
            finally
            {
                done.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        return exit;
    }
}
