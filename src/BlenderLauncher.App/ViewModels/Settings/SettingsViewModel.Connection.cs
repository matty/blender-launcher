using BlenderLauncher.App.Localisation;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlenderLauncher.App.ViewModels.Settings;

/// <summary>
/// Settings → Connection. The GitHub token and the proxy user and password live in the credential store
/// (masked fields; a saved secret is never shown back). Any change rebuilds the HTTP client; the proxy and TLS options
/// still ask for a restart, as upstream.
/// </summary>
public sealed partial class SettingsViewModel
{
    /// <summary>Upstream's documentation page on GitHub tokens.</summary>
    public const string GitHubTokenDocsUrl = "https://victor-ix.github.io/Blender-Launcher-V2/github_token/";

    private string _userIdDraft = "";
    private string _proxyHostDraft = "";
    private string _tokenDraft = "";
    private string _proxyUserDraft = "";
    private string _proxyPasswordDraft = "";
    private string _savedProxyUser = "";
    private bool _tokenTouched;
    private bool _proxyUserTouched;
    private bool _proxyPasswordTouched;

    public IReadOnlyList<string> ProxyTypeNames { get; } =
    [
        Loc.Tr("ui.settings.connection.proxy_types.none"), Loc.Tr("ui.settings.connection.proxy_types.http"),
        Loc.Tr("ui.settings.connection.proxy_types.https"), Loc.Tr("ui.settings.connection.proxy_types.socks4"),
        Loc.Tr("ui.settings.connection.proxy_types.socks5"),
    ];

    // ------------------------------------------------------------------ authentication

    public string UserIdDraft
    {
        get => _userIdDraft;
        set
        {
            if (SetProperty(ref _userIdDraft, value ?? ""))
            {
                UserIdError = null;
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUserIdError))]
    public partial string? UserIdError { get; private set; }

    public bool HasUserIdError => UserIdError is not null;

    public string GitHubTokenDraft
    {
        get => _tokenDraft;
        set
        {
            if (SetProperty(ref _tokenDraft, value ?? ""))
            {
                _tokenTouched = true;
            }
        }
    }

    /// <summary>A token is saved (shown as a hint; the token itself is not shown back).</summary>
    [ObservableProperty]
    public partial bool HasSavedToken { get; private set; }

    /// <summary>A credential store problem, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSecretError))]
    public partial string? SecretError { get; private set; }

    public bool HasSecretError => SecretError is not null;

    [RelayCommand]
    private void OpenTokenDocs() => Host?.Open(GitHubTokenDocsUrl, isFolder: false);

    // ------------------------------------------------------------------ proxy

    public bool UseCustomTlsCertificates
    {
        get => Current.Connection.UseCustomTlsCertificates;
        set => Update(s => s with { Connection = s.Connection with { UseCustomTlsCertificates = value } });
    }

    public int ProxyTypeIndex
    {
        get => (int)Current.Connection.ProxyType;
        set
        {
            if (Enum.IsDefined((ProxyType)value))
            {
                Update(s => s with { Connection = s.Connection with { ProxyType = (ProxyType)value } });
            }
        }
    }

    public bool IsProxyEnabled => Current.Connection.ProxyType != ProxyType.None;

    public string ProxyHostDraft
    {
        get => _proxyHostDraft;
        set
        {
            if (SetProperty(ref _proxyHostDraft, value ?? ""))
            {
                ProxyHostError = null;
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProxyHostError))]
    public partial string? ProxyHostError { get; private set; }

    public bool HasProxyHostError => ProxyHostError is not null;

    public decimal? ProxyPort
    {
        get => Current.Connection.ProxyPort;
        set
        {
            if (value is { } v and >= 1 and <= 65535)
            {
                Update(s => s with { Connection = s.Connection with { ProxyPort = (int)v } });
            }
        }
    }

    public string ProxyUserDraft
    {
        get => _proxyUserDraft;
        set
        {
            if (SetProperty(ref _proxyUserDraft, value ?? ""))
            {
                _proxyUserTouched = true;
            }
        }
    }

    public string ProxyPasswordDraft
    {
        get => _proxyPasswordDraft;
        set
        {
            if (SetProperty(ref _proxyPasswordDraft, value ?? ""))
            {
                _proxyPasswordTouched = true;
            }
        }
    }

    [ObservableProperty]
    public partial bool HasSavedProxyPassword { get; private set; }

    /// <summary>True for a host name or an IP address (IPv6 with or without brackets).</summary>
    public static bool IsValidHost(string host)
    {
        var bare = host.Trim().TrimStart('[').TrimEnd(']');
        return bare.Length > 0 && Uri.CheckHostName(bare) != UriHostNameType.Unknown;
    }

    private void InitialiseConnection()
    {
        _userIdDraft = Current.Connection.UserId ?? "";
        _proxyHostDraft = Current.Connection.ProxyHost;
        try
        {
            HasSavedToken = !string.IsNullOrEmpty(_credentials.Get(CredentialKeys.GitHubToken));
            _savedProxyUser = _credentials.Get(CredentialKeys.ProxyUser) ?? "";
            _proxyUserDraft = _savedProxyUser;
            HasSavedProxyPassword = !string.IsNullOrEmpty(_credentials.Get(CredentialKeys.ProxyPassword));
        }
        catch (CredentialStoreException e)
        {
            SecretError = Loc.Tr("ui.settings.connection.secret_failed", ("error", e.Message));
        }
    }

    private void CommitConnectionDrafts()
    {
        var userId = _userIdDraft.Trim();
        if (userId.Length == 0)
        {
            Update(s => s with { Connection = s.Connection with { UserId = null } });
        }
        else if (ConnectionOptions.IsValidUserId(userId))
        {
            Update(s => s with { Connection = s.Connection with { UserId = userId } });
        }
        else
        {
            UserIdError = Loc.Tr("ui.settings.connection.user_id_invalid");
        }

        var host = _proxyHostDraft.Trim();
        if (IsValidHost(host))
        {
            Update(s => s with { Connection = s.Connection with { ProxyHost = host } });
        }
        else if (host.Length > 0 || IsProxyEnabled)
        {
            ProxyHostError = Loc.Tr("ui.settings.connection.host_invalid");
        }

        var rebuild = false;
        if (_tokenTouched && SaveSecret(CredentialKeys.GitHubToken, _tokenDraft))
        {
            _tokenTouched = false;
            HasSavedToken = _tokenDraft.Trim().Length > 0;
            GitHubTokenDraft = "";
            _tokenTouched = false;
            rebuild = true;
        }

        if (_proxyUserTouched && _proxyUserDraft.Trim() != _savedProxyUser && SaveSecret(CredentialKeys.ProxyUser, _proxyUserDraft))
        {
            _savedProxyUser = _proxyUserDraft.Trim();
            _changedCredentials.Add(CredentialKeys.ProxyUser);
            rebuild = true;
        }

        _proxyUserTouched = false;
        if (_proxyPasswordTouched && SaveSecret(CredentialKeys.ProxyPassword, _proxyPasswordDraft))
        {
            HasSavedProxyPassword = _proxyPasswordDraft.Trim().Length > 0;
            ProxyPasswordDraft = "";
            _proxyPasswordTouched = false;
            _changedCredentials.Add(CredentialKeys.ProxyPassword);
            rebuild = true;
        }

        if (rebuild)
        {
            // Secrets are not in the settings, so the store's change event does not rebuild the client for them.
            Services?.RebuildHttpClient();
        }
    }

    private bool SaveSecret(string key, string value)
    {
        try
        {
            _credentials.Set(key, value.Trim());
            SecretError = null;
            return true;
        }
        catch (CredentialStoreException e)
        {
            SecretError = Loc.Tr("ui.settings.connection.secret_failed", ("error", e.Message));
            return false;
        }
    }
}
