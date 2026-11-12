namespace BlenderLauncher.Core.Settings;

/// <summary>
/// One setting, named as it appears in <c>settings.json</c>: the group's member name and the setting's member
/// name (camelCase), for example <c>connection.proxyType</c>. The top-level members use the group
/// <see cref="RootGroup"/>.
/// </summary>
public readonly record struct SettingKey(string Group, string Name)
{
    /// <summary>The group name used for top-level members such as <c>schemaVersion</c>.</summary>
    public const string RootGroup = "";

    public override string ToString() => Group.Length == 0 ? Name : $"{Group}.{Name}";
}

/// <summary>The group names in <c>settings.json</c>.</summary>
public static class SettingsGroups
{
    public const string General = "general";
    public const string Appearance = "appearance";
    public const string Connection = "connection";
    public const string Builds = "builds";
    public const string Launching = "launching";
    public const string Internal = "internal";
}

/// <summary>Raised by <see cref="SettingsStore.Changed"/> after a change that altered at least one value.</summary>
public sealed class SettingsChangedEventArgs(AppSettings previous, AppSettings current, IReadOnlyList<SettingKey> changed) : EventArgs
{
    public AppSettings Previous { get; } = previous;

    public AppSettings Current { get; } = current;

    /// <summary>The settings whose value differs, as JSON names. A changed nested object (window placement) is one key.</summary>
    public IReadOnlyList<SettingKey> Changed { get; } = changed;

    /// <summary>True when any setting of <paramref name="group"/> changed.</summary>
    public bool Affects(string group) => Changed.Any(k => k.Group == group);

    /// <summary>True when <paramref name="key"/> changed.</summary>
    public bool Affects(SettingKey key) => Changed.Contains(key);

    /// <summary>The changed settings that only take effect after a restart.</summary>
    public IEnumerable<SettingKey> RestartRequired => Changed.Where(SettingsMetadata.RequiresRestart);
}

/// <summary>Facts about settings that the settings window needs.</summary>
public static class SettingsMetadata
{
    /// <summary>
    /// Settings that need a restart: proxy, TLS, worker count, language and log level. Upstream also
    /// listed <c>user_id</c> and the DPI factor; the DPI factor is dropped, and the user ID is applied live because
    /// a settings change rebuilds the HTTP client (<c>HttpClientProvider.Rebuild</c>), which sets the User-Agent.
    /// Proxy user and password are in <see cref="RestartRequiredCredentials"/>.
    /// </summary>
    public static IReadOnlyList<SettingKey> RestartRequiredSettings { get; } =
    [
        new(SettingsGroups.Connection, "useCustomTlsCertificates"),
        new(SettingsGroups.Connection, "proxyType"),
        new(SettingsGroups.Connection, "proxyHost"),
        new(SettingsGroups.Connection, "proxyPort"),
        new(SettingsGroups.General, "workerThreadCount"),
        new(SettingsGroups.General, "language"),
        new(SettingsGroups.General, "logLevel"),
    ];

    /// <summary>Credential keys (<see cref="CredentialKeys"/>) whose change needs a restart: the proxy user and password.</summary>
    public static IReadOnlyList<string> RestartRequiredCredentials { get; } = [CredentialKeys.ProxyUser, CredentialKeys.ProxyPassword];

    /// <summary>True when a change to <paramref name="key"/> needs a restart.</summary>
    public static bool RequiresRestart(SettingKey key) => RestartRequiredSettings.Contains(key);

    /// <summary>True when a change to credential <paramref name="credentialKey"/> needs a restart.</summary>
    public static bool CredentialRequiresRestart(string credentialKey) => RestartRequiredCredentials.Contains(credentialKey);
}
