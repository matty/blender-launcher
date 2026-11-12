using System.Text.RegularExpressions;

namespace BlenderLauncher.Core.Net;

/// <summary>
/// Proxy kinds, with upstream's <c>proxy/type</c> integer values.
/// </summary>
public enum ProxyType
{
    /// <summary>No proxy configured. Unlike upstream (a plain direct connection), .NET's system proxy applies.</summary>
    None = 0,

    /// <summary><c>http://host:port</c>.</summary>
    Http = 1,

    /// <summary><c>https://host:port</c> (TLS to the proxy itself; supported since .NET 8).</summary>
    Https = 2,

    /// <summary><c>socks4a://host:port</c>: the proxy resolves host names, as upstream.</summary>
    Socks4 = 3,

    /// <summary>
    /// <c>socks5://host:port</c>. .NET sends the host name to the proxy (address type 3) unless it is an IP
    /// literal, which is what upstream's <c>socks5h://</c> does.
    /// </summary>
    Socks5 = 4,
}

/// <summary>
/// Everything <see cref="HttpClientProvider"/> builds its client from. Immutable; a settings change makes a new
/// instance and calls <see cref="HttpClientProvider.Rebuild"/>. Secrets (proxy password, GitHub token) live here
/// only in memory and are never logged; <see cref="ToString"/> masks them.
/// </summary>
public sealed partial record ConnectionOptions
{
    /// <summary>Upstream's default <c>proxy/host</c>.</summary>
    public const string DefaultProxyHost = "255.255.255.255";

    /// <summary>Upstream's default <c>proxy/port</c>.</summary>
    public const int DefaultProxyPort = 9999;

    /// <summary>Options with no proxy, no token and no user ID.</summary>
    public static ConnectionOptions Default { get; } = new();

    public ProxyType ProxyType { get; init; } = ProxyType.None;

    /// <summary>Host name or IP address (an IPv6 literal may be given with or without brackets).</summary>
    public string ProxyHost { get; init; } = DefaultProxyHost;

    public int ProxyPort { get; init; } = DefaultProxyPort;

    /// <summary>Proxy user name; credentials are used only when this is non-empty.</summary>
    public string? ProxyUser { get; init; }

    public string? ProxyPassword { get; init; }

    /// <summary>GitHub personal access token, sent as <c>Authorization: token …</c> to <c>api.github.com</c> only.</summary>
    public string? GitHubToken { get; init; }

    /// <summary>
    /// The optional <c>user_id</c> setting. Appended to the User-Agent only when set and valid
    /// (<see cref="IsValidUserId"/>); unlike upstream, none is generated.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Upstream's <c>use_custom_tls_certificates</c>, kept for settings parity. It has no effect: upstream's
    /// <c>true</c> means "verify against the OS trust store" (truststore) and its <c>false</c> still verifies
    /// against OpenSSL's default store. .NET on Windows always verifies with SChannel against the OS trust store
    /// (including the disallowed list and revocation pushed by Windows Update), which is what <c>true</c> asked
    /// for, so both values behave the same. Certificate checks are never turned off.
    /// </summary>
    public bool UseCustomTlsCertificates { get; init; } = true;

    /// <summary>The proxy URI for <see cref="ProxyType"/>, host and port, or null for <see cref="ProxyType.None"/>.</summary>
    public Uri? ProxyUri
    {
        get
        {
            var scheme = ProxyType switch
            {
                ProxyType.None => null,
                ProxyType.Http => "http",
                ProxyType.Https => "https",
                ProxyType.Socks4 => "socks4a",
                ProxyType.Socks5 => "socks5",
                _ => throw new InvalidOperationException($"Unknown proxy type {(int)ProxyType}."),
            };
            if (scheme is null)
            {
                return null;
            }

            var host = ProxyHost.Trim();
            if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('['))
            {
                host = "[" + host + "]";
            }

            return new Uri($"{scheme}://{host}:{ProxyPort}/");
        }
    }

    /// <summary>True when <see cref="UserId"/> is set and matches upstream's UI rule <c>^[a-zA-Z0-9-]{8,64}$</c>.</summary>
    public bool HasValidUserId => IsValidUserId(UserId);

    public static bool IsValidUserId(string? userId) => userId is not null && UserIdPattern().IsMatch(userId);

    /// <summary>
    /// Returns a description of the first problem, or null when the options can be used: a known proxy type, and
    /// for a proxy a non-empty host that forms a valid URI and a port from 1 to 65535.
    /// </summary>
    public string? Validate()
    {
        if (!Enum.IsDefined(ProxyType))
        {
            return $"Unknown proxy type {(int)ProxyType}.";
        }

        if (ProxyType == ProxyType.None)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(ProxyHost))
        {
            return "The proxy host is empty.";
        }

        if (ProxyPort is < 1 or > 65535)
        {
            return $"The proxy port {ProxyPort} is outside 1-65535.";
        }

        try
        {
            _ = ProxyUri;
        }
        catch (UriFormatException)
        {
            return $"The proxy host '{ProxyHost}' is not a valid host name.";
        }

        return null;
    }

    /// <summary>Safe to log: secrets are replaced by whether they are set.</summary>
    public override string ToString() =>
        $"ConnectionOptions {{ ProxyType = {ProxyType}, ProxyHost = {ProxyHost}, ProxyPort = {ProxyPort}, " +
        $"ProxyUser = {(string.IsNullOrEmpty(ProxyUser) ? "(none)" : "(set)")}, " +
        $"ProxyPassword = {(string.IsNullOrEmpty(ProxyPassword) ? "(none)" : "(set)")}, " +
        $"GitHubToken = {(string.IsNullOrEmpty(GitHubToken) ? "(none)" : "(set)")}, " +
        $"UserId = {(UserId is null ? "(none)" : UserId)}, UseCustomTlsCertificates = {UseCustomTlsCertificates} }}";

    // \z rather than $, which would also accept a trailing line break (a header-injection risk).
    [GeneratedRegex(@"^[a-zA-Z0-9-]{8,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UserIdPattern();
}
