using System.Globalization;
using System.Text.Json;
using BlenderLauncher.Core.Net;

namespace BlenderLauncher.Core.Tests.Net;

/// <summary>
/// Fetches live URLs and writes them as fixtures: for each request in a source folder's
/// <c>record.json</c>, every redirect hop and the final body go into the folder and its <c>manifest.json</c>.
/// Run only on purpose, through <see cref="FixtureRecorderTests.RecordLiveFixtures"/>; see <c>Fixtures/Net/README.md</c>.
/// </summary>
public static class FixtureRecorder
{
    public const string RequestsFileName = "record.json";

    /// <summary>Headers kept in the manifest. Everything else (cookies, CF ids, server noise) is dropped.</summary>
    public static IReadOnlyList<string> KeptHeaders { get; } =
    [
        "Content-Type", "Content-Length", "Last-Modified", "Location", "ETag", "Retry-After",
        "X-RateLimit-Limit", "X-RateLimit-Remaining", "X-RateLimit-Reset", "X-RateLimit-Resource", "X-RateLimit-Used",
    ];

    /// <summary>The source-tree folder <c>tests/BlenderLauncher.Core.Tests/Fixtures/Net</c>, found from the test assembly upwards.</summary>
    public static string SourceFixturesRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BlenderLauncher.slnx")))
            {
                return Path.Combine(dir.FullName, "tests", "BlenderLauncher.Core.Tests", "Fixtures", "Net");
            }
        }

        throw new DirectoryNotFoundException("BlenderLauncher.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Records every request in <paramref name="folder"/>'s <c>record.json</c>, merging into its manifest. Returns lines for the test output.</summary>
    public static async Task<IReadOnlyList<string>> RecordAsync(string folder, string? gitHubToken, CancellationToken ct)
    {
        var requests = LoadRequests(Path.Combine(folder, RequestsFileName));
        var manifestPath = Path.Combine(folder, FixtureManifest.FileName);
        var entries = File.Exists(manifestPath) ? FixtureManifest.Load(manifestPath).ToList() : [];
        var report = new List<string>();

        var options = ConnectionOptions.Default with { GitHubToken = gitHubToken };

        // The launcher's handlers without RedirectHandler, so each hop is seen and recorded.
        using var client = new HttpClient(new GitHubTokenHandler(options.GitHubToken, HttpClientProvider.CreatePrimaryHandler(options)))
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent.For(null));

        foreach (var req in requests)
        {
            var url = new Uri(req.Url);
            for (var hop = 0; hop <= RedirectHandler.MaxRedirects; hop++)
            {
                using var message = new HttpRequestMessage(new HttpMethod(req.Method), url);
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
                var body = message.Method == HttpMethod.Head ? [] : await response.Content.ReadAsByteArrayAsync(ct);
                var isRedirect = RedirectHandler.IsRedirect(response.StatusCode) && response.Headers.Location is not null;
                string? file = null;
                if (body.Length > 0)
                {
                    file = hop == 0 ? req.File ?? FileNameFor(url) : FileNameFor(url);
                    await File.WriteAllBytesAsync(Path.Combine(folder, file), body, ct);
                }

                var headers = new List<KeyValuePair<string, string>>();
                foreach (var name in KeptHeaders)
                {
                    if (name == "Content-Length" && body.Length > 0)
                    {
                        continue; // the body was stored decompressed; its length is its own
                    }

                    if (response.Headers.TryGetValues(name, out var v) || response.Content.Headers.TryGetValues(name, out v))
                    {
                        headers.Add(new(name, string.Join(", ", v)));
                    }
                }

                var entry = new FixtureEntry
                {
                    Method = req.Method,
                    Url = url.AbsoluteUri,
                    IgnoreQuery = hop == 0 && req.IgnoreQuery,
                    Status = (int)response.StatusCode,
                    File = file,
                    Headers = headers,
                    Recorded = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                };
                entries.RemoveAll(e => e.Method.Equals(entry.Method, StringComparison.OrdinalIgnoreCase) && e.Url == entry.Url);
                entries.Add(entry);
                report.Add($"{req.Method} {url} -> {(int)response.StatusCode}, {body.Length} bytes{(file is null ? "" : " -> " + file)}");

                if (!isRedirect)
                {
                    break;
                }

                url = response.Headers.Location!.IsAbsoluteUri ? response.Headers.Location : new Uri(url, response.Headers.Location);
            }
        }

        FixtureManifest.Save(manifestPath, entries);
        return report;
    }

    /// <summary>A file name from the URL's last path segment and query, safe on Windows.</summary>
    public static string FileNameFor(Uri url)
    {
        var name = url.Segments.LastOrDefault(s => s.Trim('/').Length > 0)?.Trim('/') ?? url.Host;
        if (url.Query.Length > 1)
        {
            name += "_" + url.Query[1..];
        }

        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray();
        var safe = new string(chars);
        if (safe.Length > 80)
        {
            safe = safe[..80];
        }

        var extension = Path.GetExtension(safe);
        return extension.Length is >= 2 and <= 5 && extension.Skip(1).All(char.IsAsciiLetter) ? safe : safe + ".body";
    }

    private static List<(string Method, string Url, string? File, bool IgnoreQuery)> LoadRequests(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var list = new List<(string, string, string?, bool)>();
        foreach (var e in doc.RootElement.GetProperty("requests").EnumerateArray())
        {
            list.Add((
                e.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET",
                e.GetProperty("url").GetString() ?? throw new InvalidDataException($"{path}: request without url"),
                e.TryGetProperty("file", out var f) ? f.GetString() : null,
                e.TryGetProperty("ignoreQuery", out var q) && q.GetBoolean()));
        }

        return list;
    }
}
