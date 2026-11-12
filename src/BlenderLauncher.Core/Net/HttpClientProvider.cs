using System.Net;
using BlenderLauncher.Core.Logging;

namespace BlenderLauncher.Core.Net;

/// <summary>
/// Owns the launcher's one <see cref="HttpClient"/> (port of upstream's <c>ConnectionManager</c>).
/// The pipeline, outermost first:
/// <list type="number">
/// <item><see cref="RedirectHandler"/>: follows redirects so that each hop is checked and logged on its own.</item>
/// <item><see cref="HttpLoggingHandler"/> (when a <see cref="Log"/> is given).</item>
/// <item><see cref="GitHubTokenHandler"/>: the token on <c>https://api.github.com</c> only.</item>
/// <item>One <see cref="SocketsHttpHandler"/>: proxy, 10 s connect timeout, automatic decompression (gzip,
/// deflate, brotli), its own redirects off.</item>
/// </list>
/// Default headers: the <see cref="UserAgent"/> only (<c>Accept-Encoding</c> comes from decompression).
/// </summary>
/// <remarks>
/// <para><b>Timeouts.</b> <see cref="HttpClient.Timeout"/> is infinite, because downloads stream for as long as
/// they take. Only connecting is bounded (<see cref="ConnectTimeout"/>). Every other call bounds itself with a
/// token, for example <c>using var cts = NetTimeouts.Linked(ct, NetTimeouts.Request)</c>, and must read the body
/// under the same token. Upstream set no timeout except the download's 10 s.</para>
/// <para><b>Rebuilding.</b> <see cref="Rebuild"/> swaps <see cref="Client"/> for one built from new options.
/// Requests already running on the old client finish normally: it is disposed only with the provider, as disposing
/// an <see cref="HttpClient"/> cancels its in-flight requests. Callers fetch <see cref="Client"/> per operation
/// instead of keeping it. Upstream needed a restart for proxy, TLS and user ID changes; here only the token,
/// proxy and user ID change the client, and the settings UI may still ask for a restart.</para>
/// <para><b>Proxy credentials.</b> HTTP and HTTPS proxies get the credentials as <see cref="ICredentials"/>, which
/// .NET sends after the proxy's 407 challenge (upstream sent <c>Proxy-Authorization</c> up front, even with empty
/// credentials). SOCKS proxies get them in the SOCKS handshake (255 bytes at most each).</para>
/// <para><b>No proxy.</b> <see cref="ProxyType.None"/> leaves .NET's default proxy in place (the Windows system
/// proxy settings and the <c>HTTP(S)_PROXY</c> variables), where upstream connected directly.</para>
/// </remarks>
public sealed class HttpClientProvider : IDisposable
{
    /// <summary>Bound on opening a connection (TCP, proxy handshake and TLS).</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a pooled connection is reused before it is replaced, so DNS changes are picked up.</summary>
    public static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(10);

    private readonly Log? _log;
    private readonly Func<ConnectionOptions, HttpMessageHandler> _primaryFactory;
    private readonly List<HttpClient> _retired = [];
    private readonly Lock _gate = new();
    private HttpClient _client;
    private bool _disposed;

    /// <summary>Builds the client over a real <see cref="SocketsHttpHandler"/>. Throws <see cref="ArgumentException"/> for invalid options.</summary>
    public HttpClientProvider(ConnectionOptions options, Log? log = null)
        : this(options, log, CreatePrimaryHandler)
    {
    }

    /// <summary>
    /// Builds the client over the handler <paramref name="primaryFactory"/> returns, for tests (for example a
    /// recorded-response handler). The factory is called again on <see cref="Rebuild"/>.
    /// </summary>
    public HttpClientProvider(ConnectionOptions options, Log? log, Func<ConnectionOptions, HttpMessageHandler> primaryFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(primaryFactory);
        _log = log;
        _primaryFactory = primaryFactory;
        Options = options;
        _client = Build(options);
    }

    /// <summary>The options the current client was built from.</summary>
    public ConnectionOptions Options { get; private set; }

    /// <summary>The current client. Fetch it per operation; do not cache it across a <see cref="Rebuild"/>.</summary>
    public HttpClient Client
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _client;
            }
        }
    }

    /// <summary>
    /// Replaces <see cref="Client"/> with one built from <paramref name="options"/>. The old client keeps serving
    /// requests already started on it and is disposed with the provider. Does nothing when the options are equal.
    /// Throws <see cref="ArgumentException"/> for invalid options, leaving the current client in place.
    /// </summary>
    public void Rebuild(ConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (options == Options)
            {
                return;
            }

            var client = Build(options);
            _retired.Add(_client);
            _client = client;
            Options = options;
        }
    }

    /// <summary>
    /// The pipeline of this class over <paramref name="primary"/>, as a client. For tests and tools that need the
    /// launcher's behaviour (User-Agent, token scoping, redirects) without a provider.
    /// </summary>
    public static HttpClient CreateClient(ConnectionOptions options, HttpMessageHandler primary, Log? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(primary);
        if (options.Validate() is { } problem)
        {
            throw new ArgumentException(problem, nameof(options));
        }

        HttpMessageHandler handler = new GitHubTokenHandler(options.GitHubToken, primary);
        if (log is not null)
        {
            handler = new HttpLoggingHandler(log, handler);
        }

        handler = new RedirectHandler(handler);
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent.For(options.UserId));
        return client;
    }

    /// <summary>
    /// The <see cref="SocketsHttpHandler"/> for <paramref name="options"/>: proxy and credentials, connect timeout,
    /// decompression, pooled connection lifetime, and <c>AllowAutoRedirect</c> off (see <see cref="RedirectHandler"/>).
    /// </summary>
    public static SocketsHttpHandler CreatePrimaryHandler(ConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = PooledConnectionLifetime,
            UseCookies = false,
        };

        if (options.ProxyUri is { } proxyUri)
        {
            var proxy = new WebProxy(proxyUri) { BypassProxyOnLocal = false };
            if (!string.IsNullOrEmpty(options.ProxyUser))
            {
                proxy.Credentials = new NetworkCredential(options.ProxyUser, options.ProxyPassword ?? "");
            }

            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        return handler;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _client.Dispose();
            foreach (var old in _retired)
            {
                old.Dispose();
            }

            _retired.Clear();
        }
    }

    private HttpClient Build(ConnectionOptions options)
    {
        if (options.Validate() is { } problem)
        {
            throw new ArgumentException(problem, nameof(options));
        }

        var client = CreateClient(options, _primaryFactory(options), _log);
        _log?.Info($"HTTP client built: {options}; User-Agent {UserAgent.For(options.UserId)}");
        return client;
    }
}
