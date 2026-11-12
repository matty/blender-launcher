namespace BlenderLauncher.Core.PlatformServices;

// The OS integration services: file association, launch at sign-in, shortcuts, hotkey and Add/Remove Programs. Each has a stand-in that does nothing and reports
// itself unavailable, so the UI can hide what depends on it. Platform.Windows implements them over roots that tests
// redirect to throwaway registry keys and folders.

/// <summary>Whether <c>.blend</c> files are associated with the launcher.</summary>
public enum FileAssociationState
{
    /// <summary>The launcher's ProgID does not exist.</summary>
    NotRegistered,

    /// <summary>The ProgID opens this executable.</summary>
    ThisExecutable,

    /// <summary>The ProgID exists but opens another copy of the launcher (or its command is unreadable).</summary>
    OtherExecutable,
}

/// <summary>
/// The launcher in the <c>.blend</c> "Open with" list: its ProgID (open command and icon) and the
/// <c>.blend\OpenWithProgids</c> entry. Does not take over the default program for <c>.blend</c>.
/// </summary>
public interface IFileAssociation
{
    bool IsAvailable { get; }

    FileAssociationState GetState(string exePath);

    /// <summary>Registers <paramref name="exePath"/>; <paramref name="iconPath"/> (an <c>.ico</c>) or the exe's own icon.</summary>
    void Register(string exePath, string? iconPath = null);

    /// <summary>Removes the ProgID and the <c>OpenWithProgids</c> values (also the legacy <c>.blend1</c> one).</summary>
    void Unregister();
}

/// <summary>Launch at sign-in: the per-user Run value.</summary>
public interface IStartupRegistration
{
    bool IsAvailable { get; }

    /// <summary>True only when the Run value exists and starts <paramref name="exePath"/>.</summary>
    bool IsEnabled(string exePath);

    /// <summary>True when the Run value exists, whichever executable it names.</summary>
    bool Exists { get; }

    void Enable(string exePath);

    void Disable();
}

/// <summary>What a <c>.lnk</c> file holds.</summary>
public sealed record ShortcutSpec(
    string TargetPath,
    string Arguments = "",
    string WorkingDirectory = "",
    string IconPath = "",
    int IconIndex = 0,
    string Description = "");

/// <summary>Shortcut files and the folders the launcher's own shortcut goes in.</summary>
public interface IShortcutService
{
    bool IsAvailable { get; }

    /// <summary>The user's Start Menu "Programs" folder, or null.</summary>
    string? StartMenuFolder { get; }

    /// <summary>The user's Desktop folder, or null.</summary>
    string? DesktopFolder { get; }

    /// <summary>Writes (or overwrites) the shortcut at <paramref name="path"/>.</summary>
    void Write(string path, ShortcutSpec spec);

    /// <summary>Reads a shortcut; null when there is none or it cannot be read.</summary>
    ShortcutSpec? TryRead(string path);
}

/// <summary>Why a hotkey could not be registered.</summary>
public enum HotkeyFailure
{
    None,

    /// <summary>The text is not a key combination the launcher understands.</summary>
    Invalid,

    /// <summary>Another program owns the combination.</summary>
    InUse,

    /// <summary>Anything else; the message says what.</summary>
    Other,
}

/// <summary>The outcome of <see cref="IGlobalHotkey.SetAsync"/>.</summary>
public sealed record HotkeyResult(HotkeyFailure Failure, string? Message = null)
{
    public static HotkeyResult Ok { get; } = new(HotkeyFailure.None);

    public bool Success => Failure == HotkeyFailure.None;
}

/// <summary>The quick-launch global hotkey. One combination at a time.</summary>
public interface IGlobalHotkey : IDisposable
{
    bool IsAvailable { get; }

    /// <summary>Raised on a background thread when the registered combination is pressed.</summary>
    event EventHandler? Pressed;

    /// <summary>
    /// Replaces the registered combination with <paramref name="chord"/> (upstream's text, for example
    /// <c>ctrl+alt+f11</c>); null only unregisters.
    /// </summary>
    Task<HotkeyResult> SetAsync(string? chord);
}

/// <summary>What the Add/Remove Programs entry shows.</summary>
public sealed record AppRegistrationInfo(string ExePath, string Version);

/// <summary>The opt-in Add/Remove Programs ("Installed apps") entry.</summary>
public interface IAppRegistration
{
    bool IsAvailable { get; }

    bool IsRegistered();

    /// <summary>Creates or refreshes the entry.</summary>
    void Register(AppRegistrationInfo info);

    void Unregister();
}

/// <summary>Stand-ins for platforms without the integration: nothing is available and nothing is done.</summary>
public sealed class NullShellIntegration : IFileAssociation, IStartupRegistration, IShortcutService, IGlobalHotkey, IAppRegistration
{
    public static NullShellIntegration Instance { get; } = new();

    public bool IsAvailable => false;

    public bool Exists => false;

    public string? StartMenuFolder => null;

    public string? DesktopFolder => null;

    public event EventHandler? Pressed
    {
        add { }
        remove { }
    }

    public FileAssociationState GetState(string exePath) => FileAssociationState.NotRegistered;

    public void Register(string exePath, string? iconPath = null)
    {
    }

    public void Unregister()
    {
    }

    public bool IsEnabled(string exePath) => false;

    public void Enable(string exePath)
    {
    }

    public void Disable()
    {
    }

    public void Write(string path, ShortcutSpec spec)
    {
    }

    public ShortcutSpec? TryRead(string path) => null;

    public Task<HotkeyResult> SetAsync(string? chord) =>
        Task.FromResult(chord is null ? HotkeyResult.Ok : new HotkeyResult(HotkeyFailure.Other, "Global hotkeys are not available on this system."));

    public bool IsRegistered() => false;

    public void Register(AppRegistrationInfo info)
    {
    }

    public void Dispose()
    {
    }
}
