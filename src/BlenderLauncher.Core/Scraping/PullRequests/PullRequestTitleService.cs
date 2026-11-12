using System.Globalization;
using System.Net;
using System.Text.Json;
using BlenderLauncher.Core.IO;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Net;

namespace BlenderLauncher.Core.Scraping.PullRequests;

/// <summary>
/// Pull-request titles from projects.blender.org's Gitea API, with a file cache (a port of upstream's
/// <c>PrLabelFetcher</c>, <c>threads/scraping/pr_labels.py</c>). Used by the patch source while
/// scraping (setting <c>pr_names_fetch_during_scrape</c>) and on demand ("Fetch PR name").
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The cache is <see cref="CacheFileName"/> in the given folder, read on first use. A missing or unreadable
/// file is an empty cache. Saving merges with the file as it is on disk then (titles from another instance are
/// kept; this instance's titles win), and writes atomically.</item>
/// <item>Thread-safe: the scrape and an on-demand lookup can run at once.</item>
/// <item>Differences from upstream: list pages also stop once every wanted number is known, or when a page is
/// short (the end of the list); the cache is JSON; on-disk titles never overwrite fresher ones in memory.</item>
/// </list>
/// </remarks>
public sealed class PullRequestTitleService
{
    public const string CacheFileName = "pr_labels.json";

    /// <summary>Upstream's <c>PAGE_SIZE</c>.</summary>
    public const int PageSize = 50;

    /// <summary>Upstream's <c>MAX_PAGE_REQUESTS</c>.</summary>
    public const int MaxPageRequests = 4;

    /// <summary>Default number of single lookups in flight (upstream used up to 10 threads).</summary>
    public const int DefaultParallelism = 6;

    private const string ApiBase = "https://projects.blender.org/api/v1/repos/blender/blender/pulls";

    private readonly Func<HttpClient> _http;
    private readonly Log? _log;
    private readonly int _parallelism;
    private readonly Dictionary<int, string> _titles = [];
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _io = new(1, 1);
    private bool _loaded;
    private bool _dirty;

    /// <param name="http">The shared client.</param>
    /// <param name="cacheDirectory">Folder for <see cref="CacheFileName"/> (<c>&lt;data&gt;\cache</c>); created when saving if missing.</param>
    /// <param name="log">Null when logging is off.</param>
    /// <param name="parallelism">Single lookups in flight, 1 to 10.</param>
    public PullRequestTitleService(HttpClient http, string cacheDirectory, Log? log = null, int parallelism = DefaultParallelism)
        : this(ToFactory(http), cacheDirectory, log, parallelism)
    {
    }

    /// <param name="http">Returns the client to use, called per request (so a client rebuilt by <c>HttpClientProvider</c> is picked up).</param>
    /// <param name="cacheDirectory">Folder for <see cref="CacheFileName"/> (<c>&lt;data&gt;\cache</c>); created when saving if missing.</param>
    /// <param name="log">Null when logging is off.</param>
    /// <param name="parallelism">Single lookups in flight, 1 to 10.</param>
    public PullRequestTitleService(Func<HttpClient> http, string cacheDirectory, Log? log = null, int parallelism = DefaultParallelism)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrEmpty(cacheDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parallelism, 10);
        _http = http;
        _log = log;
        _parallelism = parallelism;
        CachePath = Path.Combine(cacheDirectory, CacheFileName);
    }

    /// <summary>The cache file.</summary>
    public string CachePath { get; }

    private static Func<HttpClient> ToFactory(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return () => http;
    }

    /// <summary>The list URL for <paramref name="page"/> (1-based): upstream's <c>PULLS_LINK + "&amp;page=n"</c>.</summary>
    public static string ListUrl(int page) =>
        string.Create(CultureInfo.InvariantCulture, $"{ApiBase}?limit={PageSize}&sort=recentupdate&page={page}");

    /// <summary>The single pull-request URL: upstream's <c>INDIVIDUAL_PULLS</c>.</summary>
    public static string SingleUrl(int number) => string.Create(CultureInfo.InvariantCulture, $"{ApiBase}/{number}");

    /// <summary>The cached title of <paramref name="number"/>, or null. Reads the cache file on first use.</summary>
    public async Task<string?> GetCachedAsync(int number, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return _titles.GetValueOrDefault(number);
        }
    }

    /// <summary>
    /// Titles for <paramref name="numbers"/> (upstream's scrape-time algorithm): cached ones first; for the rest,
    /// list pages 1 to <see cref="MaxPageRequests"/>, stopping at a page that adds nothing new; then single lookups
    /// in parallel for any still missing. Then saves the cache when it changed. Never throws for a network or data
    /// problem (each is logged and the title is left out); cancellation propagates.
    /// </summary>
    /// <returns>The titles found, by number; numbers without one are absent.</returns>
    public async Task<IReadOnlyDictionary<int, string>> GetTitlesAsync(IEnumerable<int> numbers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var wanted = numbers.Where(n => n > 0).ToHashSet();

        if (Missing(wanted).Count > 0)
        {
            await FetchPagesAsync(wanted, cancellationToken).ConfigureAwait(false);
        }

        var stillMissing = Missing(wanted);
        if (stillMissing.Count > 0)
        {
            await Parallel.ForEachAsync(
                stillMissing,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(_parallelism, stillMissing.Count), CancellationToken = cancellationToken },
                async (n, ct) =>
                {
                    try
                    {
                        await FetchOneAsync(n, ct).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
                    {
                        _log?.Error($"Failed to fetch PR {n}: {e.GetType().Name}: {e.Message}");
                    }
                }).ConfigureAwait(false);
        }

        await SaveIfChangedAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            var found = new Dictionary<int, string>();
            foreach (var n in wanted)
            {
                if (_titles.TryGetValue(n, out var title))
                {
                    found[n] = title;
                }
            }

            return found;
        }
    }

    /// <summary>
    /// One title, for "Fetch PR name": from the cache, otherwise from the single-PR endpoint (then saved).
    /// Returns null when the pull request does not exist (404). Other failures throw
    /// (<see cref="HttpRequestException"/>, <see cref="JsonException"/>, or <see cref="OperationCanceledException"/>
    /// for a timeout), as upstream's <c>FetchPrTask</c> raised.
    /// </summary>
    public async Task<string?> GetTitleAsync(int number, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        if (await GetCachedAsync(number, cancellationToken).ConfigureAwait(false) is { } cached)
        {
            return cached;
        }

        var title = await FetchOneAsync(number, cancellationToken).ConfigureAwait(false);
        await SaveIfChangedAsync(cancellationToken).ConfigureAwait(false);
        return title;
    }

    /// <summary>Writes the cache now (merged with the file on disk), whether or not it changed.</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var onDisk = await ReadFileAsync(cancellationToken).ConfigureAwait(false);
            var doc = new PullRequestTitleCacheDocument { Titles = [] };
            lock (_gate)
            {
                foreach (var (n, t) in onDisk)
                {
                    _titles.TryAdd(n, t);
                }

                foreach (var (n, t) in _titles)
                {
                    doc.Titles[n] = t;
                }

                _dirty = false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var json = JsonSerializer.Serialize(doc, PullRequestJsonContext.Default.PullRequestTitleCacheDocument);
            await AtomicFile.WriteAllTextAsync(CachePath, json + "\n", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Error($"Failed to write PR title cache {CachePath}: {e.Message}");
        }
        finally
        {
            _io.Release();
        }
    }

    private async Task SaveIfChangedAsync(CancellationToken cancellationToken)
    {
        bool dirty;
        lock (_gate)
        {
            dirty = _dirty;
        }

        if (dirty)
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private List<int> Missing(HashSet<int> wanted)
    {
        lock (_gate)
        {
            return wanted.Where(n => !_titles.ContainsKey(n)).Order().ToList();
        }
    }

    private async Task FetchPagesAsync(HashSet<int> wanted, CancellationToken cancellationToken)
    {
        for (var page = 1; page <= MaxPageRequests; page++)
        {
            List<GiteaPullRequest> list;
            var url = ListUrl(page);
            try
            {
                list = await GetJsonAsync(url, PullRequestJsonContext.Default.ListGiteaPullRequest, cancellationToken).ConfigureAwait(false)
                       ?? throw new JsonException("The list is the JSON literal null");
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _log?.Error($"Failed to fetch PR labels from {url}: {e.GetType().Name}: {e.Message}");
                return;
            }

            var labels = Labels(list);
            bool anyNew;
            lock (_gate)
            {
                anyNew = false;
                foreach (var (n, t) in labels)
                {
                    if (!_titles.ContainsKey(n))
                    {
                        anyNew = true;
                    }

                    if (!_titles.TryGetValue(n, out var old) || old != t)
                    {
                        _titles[n] = t;
                        _dirty = true;
                    }
                }
            }

            // Upstream stops when a page holds nothing new; also stop at the end of the list or once all are known.
            if (!anyNew || list.Count < PageSize || Missing(wanted).Count == 0)
            {
                return;
            }
        }
    }

    private async Task<string?> FetchOneAsync(int number, CancellationToken cancellationToken)
    {
        GiteaPullRequest? pr;
        try
        {
            pr = await GetJsonAsync(SingleUrl(number), PullRequestJsonContext.Default.GiteaPullRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            _log?.Warning($"PR {number} not found");
            return null;
        }

        if (pr?.Title is not { } raw)
        {
            throw new JsonException($"PR {number}: the response has no title");
        }

        var title = raw.Trim();
        lock (_gate)
        {
            _titles[number] = title;
            _dirty = true;
        }

        return title;
    }

    private async Task<T?> GetJsonAsync<T>(string url, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        using var cts = NetTimeouts.Linked(cancellationToken, NetTimeouts.Request);
        using var response = await _http().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, type, cts.Token).ConfigureAwait(false);
    }

    private static Dictionary<int, string> Labels(IEnumerable<GiteaPullRequest> list)
    {
        var labels = new Dictionary<int, string>();
        foreach (var pr in list)
        {
            if (pr.Number > 0 && pr.Title is { } t)
            {
                labels[pr.Number] = t.Trim();
            }
        }

        return labels;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _loaded))
        {
            return;
        }

        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            var onDisk = await ReadFileAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                foreach (var (n, t) in onDisk)
                {
                    _titles.TryAdd(n, t);
                }
            }

            Volatile.Write(ref _loaded, true);
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>The cache file's titles; empty (and logged) when it is missing or unreadable.</summary>
    private async Task<SortedDictionary<int, string>> ReadFileAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(CachePath))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(CachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            var doc = await JsonSerializer.DeserializeAsync(stream, PullRequestJsonContext.Default.PullRequestTitleCacheDocument, cancellationToken).ConfigureAwait(false);
            return doc?.Titles ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _log?.Error($"Failed to load PR title cache {CachePath}: {e.Message}");
            return [];
        }
    }
}
