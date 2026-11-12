using System.Collections.Concurrent;
using System.Net;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Net;

namespace BlenderLauncher.Core.Tests.Net;

/// <summary>A request the handler received, with its headers as sent (Authorization included, for scoping tests).</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers)
{
    public string? Authorization => Headers.TryGetValue("Authorization", out var v) ? v : null;

    public string? UserAgent => Headers.TryGetValue("User-Agent", out var v) ? v : null;
}

/// <summary>Thrown for a request with no recorded response. Deliberately not an <see cref="HttpRequestException"/>, so a scraper's network-error handling cannot swallow it.</summary>
public sealed class RecordedResponseMissingException(string message) : Exception(message);

/// <summary>
/// Serves recorded responses by method and URL, in place of the network. Put it under the real pipeline
/// with <see cref="CreateClient"/>, so redirects, the User-Agent and token scoping behave as in the app.
/// <list type="bullet">
/// <item>Matching: method plus exact URL first, then entries marked <c>ignoreQuery</c> by scheme, host and path.</item>
/// <item>Several entries for the same method and URL are served in order; the last one repeats.</item>
/// <item>An unknown URL throws <see cref="RecordedResponseMissingException"/> naming the URL, and is listed in
/// <see cref="Unmatched"/>; <see cref="AssertAllMatched"/> fails the test even if a scraper caught it.</item>
/// </list>
/// Fixture folders and the manifest format are described in <c>Fixtures/Net/README.md</c>.
/// </summary>
public sealed class RecordedResponseHandler : HttpMessageHandler
{
    private readonly List<(FixtureEntry Entry, string? Folder, byte[]? Inline)> _entries = [];
    private readonly ConcurrentDictionary<string, int> _served = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly ConcurrentQueue<string> _unmatched = new();
    private readonly Lock _gate = new();

    /// <summary>The folder holding every source's fixtures, beside the test assembly.</summary>
    public static string FixturesRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Net");

    /// <summary>A handler loaded from <c>Fixtures/Net/&lt;source&gt;/manifest.json</c> for each source.</summary>
    public static RecordedResponseHandler FromFixtures(params string[] sources)
    {
        var handler = new RecordedResponseHandler();
        foreach (var source in sources)
        {
            handler.AddManifest(Path.Combine(FixturesRoot, source));
        }

        return handler;
    }

    public IReadOnlyCollection<RecordedRequest> Requests => _requests;

    public IReadOnlyCollection<string> Unmatched => _unmatched;

    /// <summary>Adds every entry of <c>manifest.json</c> in <paramref name="folder"/>.</summary>
    public RecordedResponseHandler AddManifest(string folder)
    {
        var path = Path.Combine(folder, FixtureManifest.FileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No fixture manifest at {path}", path);
        }

        foreach (var entry in FixtureManifest.Load(path))
        {
            Add(entry, folder);
        }

        return this;
    }

    /// <summary>Adds one entry; <paramref name="folder"/> is where its <see cref="FixtureEntry.File"/> lives.</summary>
    public RecordedResponseHandler Add(FixtureEntry entry, string? folder = null) => Add(entry, folder, inline: null);

    /// <summary>Adds an in-memory text response (for small inline tests).</summary>
    public RecordedResponseHandler AddText(string url, string body, string contentType = "text/html", int status = 200, string method = "GET",
        params KeyValuePair<string, string>[] headers)
    {
        var all = new List<KeyValuePair<string, string>> { new("Content-Type", contentType) };
        all.AddRange(headers);
        return Add(new FixtureEntry { Method = method, Url = url, Status = status, Headers = all }, folder: null, System.Text.Encoding.UTF8.GetBytes(body));
    }

    /// <summary>Adds a redirect from <paramref name="url"/> to <paramref name="location"/>.</summary>
    public RecordedResponseHandler AddRedirect(string url, string location, int status = 302, string method = "GET") =>
        Add(new FixtureEntry { Method = method, Url = url, Status = status, Headers = [new("Location", location)] });

    /// <summary>Fails when any request had no recorded response.</summary>
    public void AssertAllMatched()
    {
        if (!_unmatched.IsEmpty)
        {
            Assert.Fail("Requests with no recorded response:\n  " + string.Join("\n  ", _unmatched));
        }
    }

    /// <summary>The launcher's real client pipeline (<see cref="HttpClientProvider.CreateClient"/>) over this handler.</summary>
    public HttpClient CreateClient(ConnectionOptions? options = null, Log? log = null) =>
        HttpClientProvider.CreateClient(options ?? ConnectionOptions.Default, this, log);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri ?? throw new InvalidOperationException("Request has no URI");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers.NonValidated)
        {
            headers[h.Key] = h.Value.ToString();
        }

        _requests.Enqueue(new RecordedRequest(request.Method, uri, headers));

        var (entry, folder, body) = Find(request.Method.Method, uri);
        if (entry is null)
        {
            var text = $"{request.Method} {uri.AbsoluteUri}";
            _unmatched.Enqueue(text);
            throw new RecordedResponseMissingException(
                $"No recorded response for {text}. Record it (see tests/BlenderLauncher.Core.Tests/Fixtures/Net/README.md) or add it to the manifest.");
        }

        switch (entry.Fail)
        {
            case null:
                break;
            case "connection":
                throw new HttpRequestException(HttpRequestError.ConnectionError, $"Recorded connection failure for {uri}");
            case "timeout":
                throw new TaskCanceledException($"Recorded timeout for {uri}", new TimeoutException());
            default:
                throw new InvalidDataException($"Unknown fail kind '{entry.Fail}' for {uri}");
        }

        if (body is null && entry.File is not null)
        {
            var path = Path.Combine(folder ?? throw new InvalidOperationException($"Entry for {uri} has a file but no folder"), entry.File);
            body = await File.ReadAllBytesAsync(path, cancellationToken);
        }

        var isHead = request.Method == HttpMethod.Head;
        var content = new ByteArrayContent(isHead ? [] : body ?? []);
        var response = new HttpResponseMessage((HttpStatusCode)entry.Status) { Content = content, RequestMessage = request };
        foreach (var (name, value) in entry.Headers)
        {
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                // A real body sets its own length; the recorded value stands for HEAD and other empty bodies.
                if (isHead || body is null or { Length: 0 })
                {
                    content.Headers.ContentLength = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                }

                continue;
            }

            if (!response.Headers.TryAddWithoutValidation(name, value))
            {
                content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return response;
    }

    private RecordedResponseHandler Add(FixtureEntry entry, string? folder, byte[]? inline)
    {
        lock (_gate)
        {
            _entries.Add((entry with { Url = Normalise(entry.Url) }, folder, inline));
        }

        return this;
    }

    private (FixtureEntry? Entry, string? Folder, byte[]? Body) Find(string method, Uri uri)
    {
        var exact = uri.AbsoluteUri;
        var path = uri.GetLeftPart(UriPartial.Path);
        lock (_gate)
        {
            var candidates = Indices(i => _entries[i].Entry.Url == exact);
            if (candidates.Count == 0)
            {
                candidates = Indices(i => _entries[i].Entry.IgnoreQuery && new Uri(_entries[i].Entry.Url).GetLeftPart(UriPartial.Path) == path);
            }

            if (candidates.Count == 0)
            {
                return (null, null, null);
            }

            // Serve same-key entries in order; the last repeats.
            var key = method + " " + _entries[candidates[0]].Entry.Url;
            var n = _served.AddOrUpdate(key, 1, (_, v) => v + 1) - 1;
            var index = candidates[Math.Min(n, candidates.Count - 1)];
            return _entries[index];

            List<int> Indices(Func<int, bool> urlMatches) =>
                Enumerable.Range(0, _entries.Count)
                    .Where(i => _entries[i].Entry.Method.Equals(method, StringComparison.OrdinalIgnoreCase) && urlMatches(i))
                    .ToList();
        }
    }

    private static string Normalise(string url) => new Uri(url, UriKind.Absolute).AbsoluteUri;
}
