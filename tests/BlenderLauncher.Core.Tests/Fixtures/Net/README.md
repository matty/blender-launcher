# Network fixtures

Recorded HTTP responses for the scraper tests. Each source has its own folder:

```
Fixtures/Net/<source>/
  record.json     what to fetch when recording (kept, so the fixtures can be refreshed)
  manifest.json   what the tests serve: method + URL -> status, headers, body file
  <body files>    response bodies, stored decompressed
```

These folders are copied beside the test assembly (the test project includes `Fixtures\**`). They are **not** embedded in the app: `BlenderLauncher.App.csproj` embeds `Fixtures\*`, which does not descend into subfolders.

## Using fixtures in a test

```csharp
var handler = RecordedResponseHandler.FromFixtures("builder");      // one or more source folders
using var http = handler.CreateClient();                          // the app's real pipeline over the handler
var ctx = new ScrapeContext { Http = http, CacheDirectory = tempDir };
var builds = await source.ScrapeAsync(ctx, ct).ToListAsync(ct);
handler.AssertAllMatched();                                       // fails if any URL had no recording
```

- `CreateClient(options)` uses `HttpClientProvider.CreateClient`, so redirects (`Location`), the User-Agent and GitHub token scoping behave exactly as in the app. `handler.Requests` lists what was sent, headers included.
- Matching is by method plus exact URL; an entry with `"ignoreQuery": true` also matches the same scheme, host and path with any query.
- Several entries with the same method and URL are served in order, and the last one repeats (for example a Cloudflare challenge, then the listing).
- A request with no entry throws `RecordedResponseMissingException` (not an `HttpRequestException`, so scraper error handling does not hide it) and is listed in `handler.Unmatched`.
- Small cases can be built inline: `new RecordedResponseHandler().AddText(url, body)`, `.AddRedirect(from, to)`, `.Add(new FixtureEntry { … })`.

## manifest.json

```json
{
  "entries": [
    {
      "method": "GET",
      "url": "https://builder.blender.org/download/daily/?format=json&v=1",
      "status": 200,
      "file": "daily.json",
      "headers": { "Content-Type": "application/json", "Last-Modified": "Tue, 10 Nov 2026 08:00:00 GMT" },
      "recorded": "2026-11-11T13:03:10Z"
    },
    { "method": "GET", "url": "https://api.github.com/x", "status": 302, "headers": { "Location": "https://objects.githubusercontent.com/y" } },
    { "method": "HEAD", "url": "https://example.org/a.zip", "status": 200, "headers": { "Content-Length": "123", "Last-Modified": "Thu, 01 Oct 2026 12:30:00 GMT" } },
    { "method": "GET", "url": "https://example.org/down", "fail": "connection" }
  ]
}
```

| Field | Default | Meaning |
|---|---|---|
| `method` | `GET` | HTTP method |
| `url` | required | Absolute URL |
| `ignoreQuery` | `false` | Match any query string |
| `status` | `200` | Status code |
| `file` | none | Body file in this folder; none means an empty body |
| `headers` | none | Response and content headers. `Content-Length` is used only when the body is empty (HEAD, redirects); otherwise the body's own length applies |
| `fail` | none | `connection` throws `HttpRequestException`; `timeout` throws `TaskCanceledException` |
| `recorded` | none | When the recorder fetched it |

`Fixtures/Net/sample/` holds hand-written entries for the handler's own tests and is a working example.

## Recording

1. Create `Fixtures/Net/<source>/record.json` in the source tree:

   ```json
   { "requests": [
     { "url": "https://builder.blender.org/download/daily/?format=json&v=1", "file": "daily.json" },
     { "method": "HEAD", "url": "https://www.bforartists.de/…/file.zip" },
     { "url": "https://api.github.com/repos/UPBGE/upbge/releases", "file": "releases.json", "ignoreQuery": true }
   ] }
   ```

   `file` is optional (a name is derived from the URL); `ignoreQuery` is copied to the entry.

2. Build, then run the explicit recorder test (it never runs in a normal test run):

   ```powershell
   dotnet build tests/BlenderLauncher.Core.Tests
   $env:BL_RECORD_SOURCES = 'builder'          # optional; comma-separated; default is every folder with record.json
   $env:BL_GITHUB_TOKEN = '…'                  # optional; authenticates api.github.com only; never written out
   dotnet test --project tests/BlenderLauncher.Core.Tests --no-build -- --explicit only --filter-method "*RecordLiveFixtures"
   ```

The recorder fetches each URL with the app's User-Agent and token handler, follows redirects itself so each hop becomes its own entry, writes the bodies (decompressed) into the source-tree folder and merges the entries into `manifest.json`, replacing entries with the same method and URL. Only these headers are kept: `Content-Type`, `Content-Length` (empty bodies only), `Last-Modified`, `Location`, `ETag`, `Retry-After` and the `X-RateLimit-*` headers. Cookies and other server headers are dropped.

Review the diff before committing: trim large bodies if a test does not need them, and check nothing personal was recorded.
