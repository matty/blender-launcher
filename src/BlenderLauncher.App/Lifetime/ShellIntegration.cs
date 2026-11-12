using BlenderLauncher.App.Localisation;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Integration;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.Lifetime;

/// <summary>Facts about this copy of the launcher that the shell integration needs.</summary>
internal static class LauncherExe
{
    /// <summary>The running executable's full path.</summary>
    public static string Path => Environment.ProcessPath is { Length: > 0 } p
        ? p
        : System.IO.Path.Combine(AppContext.BaseDirectory, AppIdentity.ExecutableName);

    /// <summary>
    /// The files a release puts beside the exe (scripts/publish.ps1 and the release zip): the exe under any name, the
    /// native libraries and the licence files. <c>uninstall</c> removes the exe folder only when it holds nothing else.
    /// </summary>
    public static IReadOnlyCollection<string> ShippedFileNames =>
    [
        System.IO.Path.GetFileName(Path), AppIdentity.ExecutableName,
        "av_libglesv2.dll", "libHarfBuzzSharp.dll", "libSkiaSharp.dll",
        "LICENSE", "LICENSE.txt", "THIRD-PARTY-NOTICES.md", "README.md",
    ];
}

/// <summary>
/// The running app's side of the shell integration: keeps the hotkey and the Add/Remove
/// Programs entry in step with the settings, and carries out the first-run wizard's pending choices once. Questions and
/// reports go through the delegates, so this class has no window of its own.
/// </summary>
internal sealed class ShellIntegration : IDisposable
{
    private readonly PlatformServiceBundle _platform;
    private readonly SettingsStore _settings;
    private readonly Log _log;
    private readonly string _exe;
    private bool _disposed;

    public ShellIntegration(PlatformServiceBundle platform, SettingsStore settings, Log log, string? exePath = null)
    {
        _platform = platform;
        _settings = settings;
        _log = log;
        _exe = exePath ?? LauncherExe.Path;
        _platform.Hotkey.Pressed += OnHotkeyPressed;
    }

    /// <summary>The hotkey was pressed (on the hotkey thread).</summary>
    public event EventHandler? HotkeyPressed;

    /// <summary>
    /// Registers the hotkey when it is enabled (or unregisters it) and returns the failure text for the user, or null.
    /// </summary>
    public async Task<string?> ApplyHotkeyAsync(LaunchingSettings launching)
    {
        ArgumentNullException.ThrowIfNull(launching);
        if (_disposed || !_platform.Hotkey.IsAvailable)
        {
            return null;
        }

        var chord = launching.QuickLaunchHotkeyEnabled ? launching.QuickLaunchHotkey : null;
        HotkeyResult result;
        try
        {
            result = await _platform.Hotkey.SetAsync(chord).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        if (result.Success)
        {
            _log.Info(chord is null ? "Quick-launch hotkey off" : $"Quick-launch hotkey {chord} registered");
            return null;
        }

        _log.Warning($"Quick-launch hotkey {chord} not registered: {result.Failure} {result.Message}");
        return result.Failure switch
        {
            HotkeyFailure.InUse => Loc.Tr("ui.integration.hotkey_in_use", ("key", chord!)),
            HotkeyFailure.Invalid => Loc.Tr("ui.integration.hotkey_invalid", ("key", chord!)),
            _ => Loc.Tr("ui.integration.hotkey_failed", ("key", chord!), ("error", result.Message ?? "")),
        };
    }

    /// <summary>
    /// Creates, refreshes or removes the Add/Remove Programs entry to match the setting. An installed copy never has its
    /// own: Velopack's Setup.exe registered one, and a second entry would uninstall only half of it.
    /// </summary>
    public void SyncAppRegistration(bool wanted)
    {
        wanted &= AppLocation.Kind != InstallKind.Installed;
        var arp = _platform.AppRegistration;
        if (!arp.IsAvailable)
        {
            return;
        }

        try
        {
            if (wanted)
            {
                arp.Register(new AppRegistrationInfo(_exe, AppIdentity.Version));
            }
            else if (arp.IsRegistered())
            {
                arp.Unregister();
                _log.Info("Add/Remove Programs entry removed");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Warning("Updating the Add/Remove Programs entry failed", e);
        }
    }

    /// <summary>
    /// Carries out the wizard's pending choices once (the file association and both shortcuts: upstream's if/elif that
    /// skipped the shortcuts is not copied), then clears them. Returns the failures as one text, or null.
    /// </summary>
    public async Task<string?> ApplyPendingAsync(Func<string, Task<bool>> confirmReplace)
    {
        var pending = _settings.Current.Internal;
        if (!pending.PendingFileAssociation && !pending.PendingStartMenuShortcut && !pending.PendingDesktopShortcut)
        {
            return null;
        }

        // Cleared first, so a failure or a crash never repeats them at every start.
        _settings.Update(s => s with
        {
            Internal = s.Internal with { PendingFileAssociation = false, PendingStartMenuShortcut = false, PendingDesktopShortcut = false },
        });

        var errors = new List<string>();
        if (pending.PendingFileAssociation)
        {
            Try(errors, "file association", () => _platform.FileAssociation.Register(_exe));
        }

        foreach (var (wanted, location) in new[] { (pending.PendingStartMenuShortcut, ShortcutLocation.StartMenu), (pending.PendingDesktopShortcut, ShortcutLocation.Desktop) })
        {
            if (!wanted)
            {
                continue;
            }

            try
            {
                var outcome = await LauncherShortcut.CreateAsync(_platform.Shortcuts, location, _exe, confirmReplace).ConfigureAwait(true);
                _log.Info($"First-run {location} shortcut: {outcome}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                _log.Warning($"First-run {location} shortcut failed", e);
                errors.Add(e.Message);
            }
        }

        return errors.Count == 0 ? null : Loc.Tr("ui.integration.pending_failed", ("error", string.Join("; ", errors)));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _platform.Hotkey.Pressed -= OnHotkeyPressed;
        _platform.Hotkey.Dispose();
    }

    private void Try(List<string> errors, string what, Action action)
    {
        try
        {
            action();
            _log.Info($"First-run {what} set up");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Warning($"First-run {what} failed", e);
            errors.Add(e.Message);
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs e) => HotkeyPressed?.Invoke(this, EventArgs.Empty);
}
