using System.Net;

namespace BlenderLauncher.Core.Net;

/// <summary>
/// Follows redirects above the rest of the pipeline, so that every hop passes through
/// <see cref="GitHubTokenHandler"/> and the log again (the primary handler has <c>AllowAutoRedirect</c> off).
/// Rules, which match <see cref="SocketsHttpHandler"/>'s own:
/// <list type="bullet">
/// <item>301, 302, 303, 307 and 308 with a <c>Location</c> header are followed, at most <see cref="MaxRedirects"/> times;
/// past that, or without a usable <c>Location</c>, the redirect response itself is returned.</item>
/// <item>A relative <c>Location</c> resolves against the current URL. HTTPS to HTTP is never followed.</item>
/// <item>303 turns any method but HEAD into GET; 301 and 302 turn POST into GET; the body is dropped then.
/// 307 and 308 keep the method and body.</item>
/// <item>Request headers are copied to the next hop, except <c>Authorization</c> and <c>Cookie</c> when the origin
/// (scheme, host or port) changes.</item>
/// </list>
/// The returned response's <see cref="HttpResponseMessage.RequestMessage"/> is the last hop, so callers can read
/// the final URL.
/// </summary>
public sealed class RedirectHandler : DelegatingHandler
{
    public const int MaxRedirects = 10;

    public RedirectHandler(HttpMessageHandler inner)
        : base(inner)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = request;
        var response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
        for (var hop = 0; hop < MaxRedirects; hop++)
        {
            var next = NextRequest(current, response);
            if (next is null)
            {
                return response;
            }

            response.Dispose();
            current = next;
            response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    /// <summary>The request for the redirect in <paramref name="response"/>, or null when it is not one to follow.</summary>
    internal static HttpRequestMessage? NextRequest(HttpRequestMessage request, HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status is not (301 or 302 or 303 or 307 or 308) || request.RequestUri is not { } from)
        {
            return null;
        }

        var location = response.Headers.Location;
        if (location is null)
        {
            return null;
        }

        if (!location.IsAbsoluteUri)
        {
            location = new Uri(from, location);
        }

        if (location.Scheme != Uri.UriSchemeHttp && location.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        if (from.Scheme == Uri.UriSchemeHttps && location.Scheme == Uri.UriSchemeHttp)
        {
            return null;
        }

        var method = request.Method;
        var keepBody = true;
        if ((status == 303 && method != HttpMethod.Head) || (status is 301 or 302 && method == HttpMethod.Post))
        {
            method = HttpMethod.Get;
            keepBody = false;
        }

        var next = new HttpRequestMessage(method, location)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
            Content = keepBody ? request.Content : null,
        };

        var sameOrigin = Uri.Compare(from, location, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
        foreach (var header in request.Headers)
        {
            if (!sameOrigin &&
                (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                 header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            next.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in request.Options)
        {
            ((IDictionary<string, object?>)next.Options)[option.Key] = option.Value;
        }

        return next;
    }

    /// <summary>True for the status codes this handler follows.</summary>
    public static bool IsRedirect(HttpStatusCode status) => (int)status is 301 or 302 or 303 or 307 or 308;
}
