using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BlenderLauncher.Core.Tests.Net;

/// <summary>
/// One recorded response in a <c>Fixtures/Net/&lt;source&gt;/manifest.json</c>. See <c>Fixtures/Net/README.md</c>.
/// </summary>
public sealed record FixtureEntry
{
    public string Method { get; init; } = "GET";

    /// <summary>Absolute URL, normalised by <see cref="Uri.AbsoluteUri"/> when matched.</summary>
    public required string Url { get; init; }

    /// <summary>Match on scheme, host and path only, ignoring the query string.</summary>
    public bool IgnoreQuery { get; init; }

    public int Status { get; init; } = 200;

    /// <summary>Body file beside the manifest, or null for an empty body (redirects, HEAD).</summary>
    public string? File { get; init; }

    /// <summary>Response headers, content headers included (Content-Type, Content-Length, Last-Modified …).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];

    /// <summary>Instead of a response: <c>"connection"</c> throws <see cref="HttpRequestException"/>, <c>"timeout"</c> a <see cref="TaskCanceledException"/>.</summary>
    public string? Fail { get; init; }

    /// <summary>When the recorder fetched it (informational).</summary>
    public string? Recorded { get; init; }

    public string? HeaderValue(string name) =>
        Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>Reads and writes <c>manifest.json</c> with <see cref="JsonDocument"/> and <see cref="Utf8JsonWriter"/> (no reflection).</summary>
public static class FixtureManifest
{
    public const string FileName = "manifest.json";

    public static IReadOnlyList<FixtureEntry> Load(string manifestPath)
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllBytes(manifestPath), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        var list = new List<FixtureEntry>();
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            var headers = new List<KeyValuePair<string, string>>();
            if (e.TryGetProperty("headers", out var h))
            {
                foreach (var p in h.EnumerateObject())
                {
                    headers.Add(new(p.Name, p.Value.GetString() ?? ""));
                }
            }

            list.Add(new FixtureEntry
            {
                Method = Str(e, "method") ?? "GET",
                Url = Str(e, "url") ?? throw new InvalidDataException($"{manifestPath}: an entry has no url"),
                IgnoreQuery = e.TryGetProperty("ignoreQuery", out var iq) && iq.GetBoolean(),
                Status = e.TryGetProperty("status", out var s) ? s.GetInt32() : 200,
                File = Str(e, "file"),
                Headers = headers,
                Fail = Str(e, "fail"),
                Recorded = Str(e, "recorded"),
            });
        }

        return list;
    }

    public static void Save(string manifestPath, IEnumerable<FixtureEntry> entries)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteStartArray("entries");
            foreach (var e in entries)
            {
                w.WriteStartObject();
                w.WriteString("method", e.Method);
                w.WriteString("url", e.Url);
                if (e.IgnoreQuery)
                {
                    w.WriteBoolean("ignoreQuery", true);
                }

                w.WriteNumber("status", e.Status);
                if (e.File is not null)
                {
                    w.WriteString("file", e.File);
                }

                if (e.Headers.Count > 0)
                {
                    w.WriteStartObject("headers");
                    foreach (var (k, v) in e.Headers)
                    {
                        w.WriteString(k, v);
                    }

                    w.WriteEndObject();
                }

                if (e.Fail is not null)
                {
                    w.WriteString("fail", e.Fail);
                }

                if (e.Recorded is not null)
                {
                    w.WriteString("recorded", e.Recorded);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        System.IO.File.WriteAllText(manifestPath, Encoding.UTF8.GetString(stream.ToArray()) + "\n");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
