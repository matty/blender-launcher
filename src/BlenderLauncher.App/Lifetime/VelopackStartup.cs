using System.ComponentModel;
using System.Diagnostics;
using BlenderLauncher.App.Cli;
using BlenderLauncher.Core;
using Velopack;
using Velopack.Locators;

namespace BlenderLauncher.App.Lifetime;

/// <summary>
/// Velopack's side of start-up. <see cref="Run"/> must be the first thing <c>Main</c> does: when Velopack's Setup.exe,
/// Update.exe or uninstaller starts the exe with one of its hook arguments, <see cref="VelopackApp.Run"/> runs the hook
/// and exits the process. Otherwise it returns, and this records in <see cref="AppLocation"/> how the copy was installed.
/// </summary>
/// <remarks>
/// Velopack owns, for an installed copy, the Start Menu and Desktop shortcuts and the Add/Remove Programs entry; it
/// creates them on install and removes them on uninstall. What the launcher itself registered (the <c>.blend</c>
/// association, launch at startup, saved credentials, its data folder) is removed in the before-uninstall hook, as the
/// <c>uninstall</c> command does; the library is never touched.
/// </remarks>
internal static class VelopackStartup
{
    public static void Run(string[] args)
    {
        VelopackApp.Build()
            .SetArgs(args)
            .OnBeforeUninstallFastCallback(_ => CliUninstall.RunForVelopackUninstall())
            .Run();

        Detect();
    }

    /// <summary>Starts Velopack's uninstaller for an installed copy; false when it could not be started.</summary>
    public static bool StartUninstall(bool silent)
    {
        if (AppLocation.UpdateExePath is not { } update || !File.Exists(update))
        {
            return false;
        }

        var info = new ProcessStartInfo(update) { UseShellExecute = false };
        info.ArgumentList.Add("uninstall");
        if (silent)
        {
            info.ArgumentList.Add("--silent");
        }

        try
        {
            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static void Detect()
    {
        try
        {
            var locator = VelopackLocator.IsCurrentSet ? VelopackLocator.Current : VelopackLocator.CreateDefaultForPlatform();
            if (locator.CurrentlyInstalledVersion is null || string.IsNullOrEmpty(locator.RootAppDir))
            {
                return;
            }

            AppLocation.Set(locator.IsPortable ? InstallKind.Portable : InstallKind.Installed, locator.RootAppDir, locator.UpdateExePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // Not a Velopack layout (a development build or a hand-copied exe): stays Loose.
        }
    }
}
