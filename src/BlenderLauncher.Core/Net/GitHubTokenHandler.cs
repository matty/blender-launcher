using System.Net.Http.Headers;

namespace BlenderLauncher.Core.Net;

/// <summary>
/// Adds <c>Authorization: token {token}</c> (GitHub's classic scheme, as upstream) to a request whose host is
/// exactly <c>api.github.com</c> over HTTPS, and to nothing else. Upstream tested <c>"api.github.com" in url</c>,
/// which also matched other hosts and query strings; this checks the parsed host.
/// </summary>
/// <remarks>
/// It sits inside <see cref="RedirectHandler"/>, so every redirect hop is checked on its own: a redirect from
/// <c>api.github.com</c> to <c>objects.githubusercontent.com</c> (release assets) goes out without the token.
/// A request that already carries an <c>Authorization</c> header is left alone. The token is never logged.
/// </remarks>
public sealed class GitHubTokenHandler : DelegatingHandler
{
    public const string ApiHost = "api.github.com";

    private readonly string? _token;

    public GitHubTokenHandler(string? token, HttpMessageHandler inner)
        : base(inner)
    {
        _token = IsUsableToken(token) ? token!.Trim() : null;
    }

    /// <summary>True when the token is set and, once trimmed, has no spaces or control characters (which would break the header).</summary>
    public static bool IsUsableToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        foreach (var c in token.AsSpan().Trim())
        {
            if (c <= ' ' || c == '\u007f')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when a request to <paramref name="uri"/> gets the token.</summary>
    public static bool IsGitHubApi(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.IdnHost, ApiHost, StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_token is not null && request.Headers.Authorization is null && IsGitHubApi(request.RequestUri))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("token", _token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
