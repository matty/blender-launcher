using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.ViewModels.Detail;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.ViewModels.Settings;

/// <summary>What the Settings window asks of its host window: pickers, dialogs and the shell.</summary>
public interface ISettingsHost
{
    /// <summary>Asks for a folder; null when cancelled.</summary>
    Task<string?> PickFolderAsync(string title, string? startFolder);

    /// <summary>A dialog over the Settings window; returns the chosen button's index.</summary>
    Task<int> AskAsync(DialogKind kind, string title, string message, IReadOnlyList<PromptButton> buttons);

    /// <summary>Opens a link, file or folder (created first when it is a folder that does not exist).</summary>
    void Open(string target, bool isFolder);
}

/// <summary>What the Settings window reads besides the settings (all optional: sample data and tests have none).</summary>
public sealed record SettingsEnvironment
{
    /// <summary>The running services: library, staging purge, HTTP client rebuild, API data. Null until started.</summary>
    public AppServices? Services { get; init; }

    public DataDirectories? Directories { get; init; }

    /// <summary>The exe folder, against which a relative library folder is resolved.</summary>
    public string ExeDirectory { get; init; } = AppLocation.BaseDirectory;

    public int ProcessorCount { get; init; } = Environment.ProcessorCount;

    /// <summary>The Blender series for the minimum stable version list; the services' API data, else the bundled file.</summary>
    public ApiData? ApiData { get; init; }

    /// <summary>The shell integration (file association, start-up, shortcuts); null hides those controls.</summary>
    public Core.PlatformServices.PlatformServiceBundle? Platform { get; init; }

    /// <summary>The executable the integration registers (this copy of the launcher).</summary>
    public string ExePath { get; init; } = Lifetime.LauncherExe.Path;

    /// <summary>Asks before the launcher shortcut replaces one that starts something else; given the path.</summary>
    public Func<string, Task<bool>>? ConfirmReplaceShortcut { get; init; }
}

/// <summary>
/// The Settings window. Every control reads <see cref="SettingsStore.Current"/> and writes
/// through <see cref="SettingsStore.Update"/> at once, as upstream saves in its setters; text fields keep a draft that is
/// validated and saved when the field loses focus, on Enter, and when the window closes (<see cref="CommitDrafts"/>).
/// Secrets go to the credential store. A change made anywhere refreshes every binding.
/// <para>
/// On close, <see cref="PendingRestart"/> lists the settings changed since the window opened that only apply after a
/// restart (<see cref="SettingsMetadata"/>), for the "Restart now / Later" prompt.
/// </para>
/// Tabs are partial files: <c>.General</c>, <c>.Appearance</c>, <c>.Connection</c>, <c>.Builds</c>, <c>.About</c>.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly SettingsStore _store;
    private readonly ICredentialStore _credentials;
    private readonly SettingsEnvironment _environment;
    private readonly AppSettings _initial;
    private readonly HashSet<string> _changedCredentials = new(StringComparer.Ordinal);
    private bool _disposed;

    public SettingsViewModel(SettingsStore store, ICredentialStore credentials, SettingsEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(credentials);
        _store = store;
        _credentials = credentials;
        _environment = environment ?? new SettingsEnvironment();
        _initial = store.Current;

        InitialiseGeneral();
        RefreshIntegration();
        InitialiseConnection();
        InitialiseBuilds();
        _store.Changed += OnStoreChanged;
    }

    /// <summary>The host window; set by the view before anything asks for a picker or a dialog.</summary>
    public ISettingsHost? Host { get; set; }

    private AppSettings Current => _store.Current;

    private AppServices? Services => _environment.Services;

    /// <summary>
    /// The settings changed since the window opened that need a restart, as their labels (empty for none): the keys
    /// in <see cref="SettingsMetadata.RestartRequiredSettings"/> whose value now differs, and the proxy user or
    /// password if either was changed.
    /// </summary>
    public IReadOnlyList<string> PendingRestart()
    {
        var labels = new List<string>();
        foreach (var key in SettingsStore.Diff(_initial, Current).Where(SettingsMetadata.RequiresRestart))
        {
            labels.Add(RestartLabel(key.ToString()));
        }

        foreach (var key in SettingsMetadata.RestartRequiredCredentials.Where(_changedCredentials.Contains))
        {
            labels.Add(RestartLabel(key));
        }

        return labels.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>The upstream label of a restart-required setting (a settings key or a credential key).</summary>
    public static string RestartLabel(string key) => key switch
    {
        "connection.useCustomTlsCertificates" => Loc.Tr("settings.connection.use_custom_tls_certificates"),
        "connection.proxyType" => Loc.Tr("settings.connection.proxy_settings") + ": " + Loc.Tr("settings.connection.proxy_type"),
        "connection.proxyHost" => Loc.Tr("settings.connection.proxy_settings") + ": " + Loc.Tr("settings.connection.proxy_host"),
        "connection.proxyPort" => Loc.Tr("settings.connection.proxy_settings") + ": " + Loc.Tr("settings.connection.proxy_port"),
        CredentialKeys.ProxyUser => Loc.Tr("settings.connection.proxy_settings") + ": " + Loc.Tr("settings.connection.proxy_user"),
        CredentialKeys.ProxyPassword => Loc.Tr("settings.connection.proxy_settings") + ": " + Loc.Tr("settings.connection.proxy_password"),
        "general.workerThreadCount" => Loc.Tr("settings.general.app.worker_count"),
        "general.language" => Loc.Tr("settings.general.app.language"),
        "general.logLevel" => Loc.Tr("settings.general.logging.log_level"),
        _ => key,
    };

    /// <summary>Saves every text field's draft that is valid (each shows its own problem otherwise).</summary>
    public void CommitDrafts()
    {
        CommitConnectionDrafts();
        CommitBuildsDrafts();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.Changed -= OnStoreChanged;
    }

    /// <summary>Changes the settings; every binding refreshes through <see cref="OnStoreChanged"/>.</summary>
    private void Update(Func<AppSettings, AppSettings> change)
    {
        if (!_disposed)
        {
            _store.Update(change);
        }
    }

    private void OnStoreChanged(object? sender, SettingsChangedEventArgs e) => UiDispatcher.Post(() =>
    {
        if (_disposed)
        {
            return;
        }

        // One notification for every property: cheap at this size, and nothing can be missed.
        OnPropertyChanged(string.Empty);
        foreach (var row in Repos)
        {
            row.Refresh();
        }

        foreach (var row in UpdateRows)
        {
            row.Refresh();
        }
    });

    private Task<int> AskAsync(DialogKind kind, string title, string message, params PromptButton[] buttons) =>
        Host?.AskAsync(kind, title, message, buttons) ?? Task.FromResult(buttons.Length - 1);

    private Task InformAsync(DialogKind kind, string title, string message) =>
        AskAsync(kind, title, message, new PromptButton(Loc.Tr("act.ok"), IsPrimary: true, IsCancel: true));
}
