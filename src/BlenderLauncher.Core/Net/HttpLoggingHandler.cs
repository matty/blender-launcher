using System.Diagnostics;
using System.Globalization;
using BlenderLauncher.Core.Logging;

namespace BlenderLauncher.Core.Net;

/// <summary>
/// Logs each request hop: method, URL, status and time at Debug; failures at Warning; a 401 at Warning, as
/// upstream does (<c>connection_manager.py:196-197</c>). Header values are never logged (only whether the request
/// was authenticated), so neither the token nor proxy credentials reach the log; <see cref="Log"/> redacts the
/// text again regardless.
/// </summary>
public sealed class HttpLoggingHandler : DelegatingHandler
{
    private readonly Log _log;

    public HttpLoggingHandler(Log log, HttpMessageHandler inner)
        : base(inner)
    {
        _log = log;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var what = $"{request.Method} {request.RequestUri}";
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var authNote = request.Headers.Authorization is null ? "" : " (authenticated)";
            _log.Debug(string.Create(CultureInfo.InvariantCulture, $"HTTP {what}{authNote} -> {(int)response.StatusCode} in {ms:0} ms"));
            if ((int)response.StatusCode == 401)
            {
                _log.Warning($"HTTP {what} returned 401; the credentials are possibly expired.");
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _log.Debug($"HTTP {what} cancelled");
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _log.Warning(string.Create(CultureInfo.InvariantCulture, $"HTTP {what} failed after {ms:0} ms: {e.GetType().Name}: {e.Message}"));
            throw;
        }
    }
}
