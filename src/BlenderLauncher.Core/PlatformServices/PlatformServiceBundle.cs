using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.Core.PlatformServices;

/// <summary>
/// The platform services <see cref="AppServices"/> is given. Every member has a stand-in, so the
/// app runs with a service missing and the UI hides what depends on it. Platform.Windows fills in the real ones;
/// further interfaces join this record as they are written.
/// </summary>
/// <remarks>Named <c>PlatformServiceBundle</c> because <c>PlatformServices</c> is this namespace's own name.</remarks>
public sealed record PlatformServiceBundle
{
    /// <summary>Every service a stand-in: no bin, credentials in memory, no launching.</summary>
    public static PlatformServiceBundle None { get; } = new();

    /// <summary>The Recycle Bin; <see cref="NullRecycleBin"/> by default.</summary>
    public IRecycleBin RecycleBin { get; init; } = NullRecycleBin.Instance;

    /// <summary>Secrets; an <see cref="InMemoryCredentialStore"/> by default (nothing survives a restart).</summary>
    public ICredentialStore Credentials { get; init; } = new InMemoryCredentialStore();

    /// <summary>Starts builds and asks them to close; <see cref="NullProcessLauncher"/> by default.</summary>
    public IProcessLauncher ProcessLauncher { get; init; } = NullProcessLauncher.Instance;

    /// <summary>Runs <c>blender -v</c> for builds without a <c>.blinfo</c>; the real process probe by default.</summary>
    public IBlenderVersionProbe VersionProbe { get; init; } = new ProcessBlenderVersionProbe();

    /// <summary>The <c>.blend</c> "Open with" registration; a no-op by default.</summary>
    public IFileAssociation FileAssociation { get; init; } = NullShellIntegration.Instance;

    /// <summary>Launch at sign-in; a no-op by default.</summary>
    public IStartupRegistration Startup { get; init; } = NullShellIntegration.Instance;

    /// <summary>Shortcut files; a no-op by default.</summary>
    public IShortcutService Shortcuts { get; init; } = NullShellIntegration.Instance;

    /// <summary>The quick-launch hotkey; a no-op by default.</summary>
    public IGlobalHotkey Hotkey { get; init; } = NullShellIntegration.Instance;

    /// <summary>The Add/Remove Programs entry; a no-op by default.</summary>
    public IAppRegistration AppRegistration { get; init; } = NullShellIntegration.Instance;
}
