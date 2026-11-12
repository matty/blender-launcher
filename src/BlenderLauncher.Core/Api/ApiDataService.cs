using System.Text.Json;
using BlenderLauncher.Core.IO;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Scraping;

namespace BlenderLauncher.Core.Api;

/// <summary>Where <see cref="ApiDataService.Current"/> came from.</summary>
public enum ApiDataSource
{
    /// <summary>The copy embedded in Core.</summary>
    Bundled,

    /// <summary><c>&lt;data&gt;\api\blender_launcher_api.json</c>, fetched earlier.</summary>
    Stored,

    /// <summary>Fetched by the latest <see cref="ApiDataService.RefreshAsync"/>.</summary>
    Hosted,
}

/// <summary>What a refresh or load did to <c>&lt;data&gt;\cache\stable_builds.json</c>.</summary>
public enum StableSeedOutcome
{
    /// <summary>The file was written (it was missing or unreadable, or the candidate was newer).</summary>
    Written,

    /// <summary>The stored file is as new as the candidate or newer, so it was left alone.</summary>
    Kept,

    /// <summary>No candidate could be read, or the file could not be written; see the errors.</summary>
    Failed,
}

/// <summary>The outcome of <see cref="ApiDataService.RefreshAsync"/>. Failures are listed, never thrown.</summary>
/// <param name="ApiDataChanged">True when <see cref="ApiDataService.Current"/> changed (and <see cref="ApiDataService.Changed"/> was raised).</param>
/// <param name="ApiDataFetched">True when the hosted <c>blender_launcher_api.json</c> was fetched, valid and stored.</param>
/// <param name="StableSeed">What happened to the stable cache.</param>
/// <param name="StableSeedFromBundle">True when the stable cache candidate was the bundled copy because the fetch failed.</param>
/// <param name="Errors">One entry per failed step (source id <see cref="ApiDataService.SourceId"/>); empty on full success.</param>
public sealed record ApiRefreshResult(
    bool ApiDataChanged,
    bool ApiDataFetched,
    StableSeedOutcome StableSeed,
    bool StableSeedFromBundle,
    IReadOnlyList<ScrapeError> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>Raised when the API data is replaced.</summary>
public sealed class ApiDataChangedEventArgs(ApiData previous, ApiData current, ApiDataSource source) : EventArgs
{
    public ApiData Previous { get; } = previous;

    public ApiData Current { get; } = current;

    public ApiDataSource Source { get; } = source;
}

/// <summary>
/// Owns the LTS table (<c>blender_launcher_api.json</c>) and seeds the stable-build cache, porting
/// upstream's <c>bl_api_manager.py</c> and <c>launcher_updates.py:109-147, 211-223</c>.
/// <list type="bullet">
/// <item><b>Reading:</b> the stored copy in <c>&lt;data&gt;\api\</c>, else the bundled copy (also used when the stored
/// one is unreadable or lists no series). Until <see cref="LoadAsync"/> runs, <see cref="Current"/> is the bundled copy.</item>
/// <item><b>Refreshing:</b> both files come from the raw URLs under <see cref="AppIdentity.UpstreamApiBaseUrl"/>
/// (upstream used the contents API, which stops returning content above 1 MB). A valid API file is stored and
/// replaces <see cref="Current"/>; the stable seed is written to <c>&lt;data&gt;\cache\stable_builds.json</c> (the
/// file the stable scraper reads, one location on purpose, as upstream splits it by platform) when that file is missing or
/// unreadable, or the hosted <c>api_file_version</c> is newer by integer comparison (upstream compared floats, so
/// 29.10 lost to 29.9). When the seed cannot be fetched the bundled copy is the candidate under the same rule, as
/// upstream.</item>
/// <item><b>Failures</b> keep the previous data and are returned in <see cref="ApiRefreshResult.Errors"/> and logged,
/// never thrown. Only cancellation by the caller throws.</item>
/// </list>
/// The service is an <see cref="ILtsLookup"/> that always answers from the current data, so holders of it see a
/// refresh without a restart; <see cref="Changed"/> is raised (on the calling thread) when the data is replaced.
/// </summary>
public sealed class ApiDataService : ILtsLookup
{
    /// <summary>Source id on reported errors.</summary>
    public const string SourceId = "upstream-api";

    /// <summary>The stable scraper's cache file in <see cref="DataDirectories.Cache"/>.</summary>
    public const string StableCacheFileName = "stable_builds.json";

    private const string ApiFileVersionKey = "api_file_version";

    private readonly Func<HttpClient> _http;
    private readonly Log? _log;
    private readonly Uri _baseUrl;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private State _state;

    /// <summary>Creates the service over the provider's current client.</summary>
    public ApiDataService(DataDirectories directories, HttpClientProvider http, Log? log = null)
        : this(directories, () => http.Client, log)
    {
        ArgumentNullException.ThrowIfNull(http);
    }

    /// <summary>
    /// Creates the service. <paramref name="http"/> is called per request (so a rebuilt client is picked up).
    /// <paramref name="baseUrl"/> overrides <see cref="AppIdentity.UpstreamApiBaseUrl"/> (it must end in <c>/</c>).
    /// </summary>
    public ApiDataService(DataDirectories directories, Func<HttpClient> http, Log? log = null, string? baseUrl = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(http);
        Directories = directories;
        _http = http;
        _log = log;
        _time = time ?? TimeProvider.System;
        _baseUrl = new Uri(baseUrl ?? AppIdentity.UpstreamApiBaseUrl, UriKind.Absolute);
        _state = new State(ApiData.TryParse(BundledApiFiles.ReadApi(), out var bundled) ? bundled : ApiData.Empty, ApiDataSource.Bundled);
    }

    public DataDirectories Directories { get; }

    /// <summary><c>&lt;data&gt;\api\blender_launcher_api.json</c>.</summary>
    public string ApiFilePath => Path.Combine(Directories.Api, BundledApiFiles.ApiFileName);

    /// <summary><c>&lt;data&gt;\cache\stable_builds.json</c>.</summary>
    public string StableCachePath => Path.Combine(Directories.Cache, StableCacheFileName);

    /// <summary>The URL the API file is fetched from.</summary>
    public Uri ApiFileUrl => new(_baseUrl, BundledApiFiles.ApiFileName);

    /// <summary>The URL the Windows stable seed is fetched from.</summary>
    public Uri StableSeedUrl => new(_baseUrl, BundledApiFiles.StableSeedFileName);

    /// <summary>The current data. Read it at use; it is replaced, never mutated.</summary>
    public ApiData Current => Volatile.Read(ref _state).Data;

    /// <summary>Where <see cref="Current"/> came from.</summary>
    public ApiDataSource CurrentSource => Volatile.Read(ref _state).Source;

    /// <summary>Raised after <see cref="Current"/> is replaced by different data.</summary>
    public event EventHandler<ApiDataChangedEventArgs>? Changed;

    /// <inheritdoc />
    public bool IsLts(int major, int minor) => Current.IsLts(major, minor);

    /// <summary>
    /// Start-up: reads the stored API file (falling back to the bundled copy) and seeds the stable cache from the
    /// bundled copy when it is missing, unreadable or older. Makes no network requests. Failures are logged.
    /// </summary>
    public async Task<StableSeedOutcome> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        ApiDataChangedEventArgs? changed;
        StableSeedOutcome seed;
        try
        {
            changed = await LoadStoredAsync(ct).ConfigureAwait(false);
            var bundled = BundledApiFiles.ReadStableSeed();
            (seed, _) = await WriteSeedIfNewerAsync(bundled, ReadVersion(bundled), "bundled", ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Raise(changed);
        return seed;
    }

    /// <summary>
    /// Fetches both hosted files and applies them (see the class summary). Returns what happened; throws only
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task<ApiRefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var errors = new List<ScrapeError>();
        ApiDataChangedEventArgs? changed = null;
        var fetched = false;
        StableSeedOutcome seed;
        var fromBundle = false;
        try
        {
            var (apiText, apiError) = await FetchAsync(ApiFileUrl, ct).ConfigureAwait(false);
            if (apiError is not null)
            {
                errors.Add(apiError);
            }
            else if (!ApiData.TryParse(apiText!, out var hosted) || hosted.Series.Count == 0)
            {
                errors.Add(Invalid(ApiFileUrl, "not an API file with at least one Blender series"));
            }
            else
            {
                try
                {
                    Directory.CreateDirectory(Directories.Api);
                    await AtomicFile.WriteAllTextAsync(ApiFilePath, apiText!, ct).ConfigureAwait(false);
                    fetched = true;
                    _log?.Info($"API data {hosted.FileVersion?.ToString() ?? "(no version)"} fetched and stored in {ApiFilePath}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // The data is still good for this session; only the stored copy is stale.
                    errors.Add(ScrapeError.FromException(SourceId, e, _time));
                    _log?.Warning($"Could not store API data in {ApiFilePath}", e);
                }

                changed = Swap(hosted, ApiDataSource.Hosted);
            }

            var (seedText, seedError) = await FetchAsync(StableSeedUrl, ct).ConfigureAwait(false);
            if (seedError is not null)
            {
                errors.Add(seedError);
            }

            var origin = StableSeedUrl.AbsoluteUri;
            string? incoming = null;
            if (seedText is not null && !TryReadSeedVersion(seedText, origin, out incoming, out var invalid))
            {
                errors.Add(invalid!);
                seedText = null;
            }

            if (seedText is null)
            {
                // Upstream falls back to the bundled seed when the hosted one is unavailable.
                fromBundle = true;
                origin = "bundled";
                seedText = BundledApiFiles.ReadStableSeed();
                incoming = ReadVersion(seedText);
            }

            ScrapeError? writeError;
            (seed, writeError) = await WriteSeedIfNewerAsync(seedText, incoming, origin, ct).ConfigureAwait(false);
            if (writeError is not null)
            {
                errors.Add(writeError);
            }
        }
        finally
        {
            _gate.Release();
        }

        Raise(changed);
        return new ApiRefreshResult(changed is not null, fetched, seed, fromBundle, errors);
    }

    private async Task<ApiDataChangedEventArgs?> LoadStoredAsync(CancellationToken ct)
    {
        string? text = null;
        try
        {
            if (File.Exists(ApiFilePath))
            {
                text = await File.ReadAllTextAsync(ApiFilePath, ct).ConfigureAwait(false);
            }
            else
            {
                _log?.Info($"No stored API file at {ApiFilePath}; using the bundled one.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Warning($"Could not read {ApiFilePath}; using the bundled API file.", e);
        }

        if (text is not null)
        {
            if (ApiData.TryParse(text, out var stored) && stored.Series.Count > 0)
            {
                return Swap(stored, ApiDataSource.Stored);
            }

            _log?.Warning($"Stored API file {ApiFilePath} is invalid; using the bundled one.");
        }

        return ApiData.TryParse(BundledApiFiles.ReadApi(), out var bundled)
            ? Swap(bundled, ApiDataSource.Bundled)
            : null;
    }

    /// <summary>
    /// True when <paramref name="text"/> is a valid stable cache; <paramref name="version"/> is its
    /// <c>api_file_version</c> (null when absent).
    /// </summary>
    private bool TryReadSeedVersion(string text, string origin, out string? version, out ScrapeError? error)
    {
        try
        {
            version = ScraperCache.FromJson(text).ApiFileVersion;
            error = null;
            return true;
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            _log?.Warning($"Stable cache seed from {origin} is invalid", e);
            version = null;
            error = new ScrapeError(SourceId, ScrapeMessageKeys.Generic, null, $"{origin}: {e.GetType().Name}: {e.Message}", [new("err", e.Message)]);
            return false;
        }
    }

    /// <summary>
    /// Writes the valid seed <paramref name="candidateText"/> to the stable cache when the stored file is missing or
    /// unreadable, or <paramref name="incoming"/> is newer than its version (integer comparison).
    /// </summary>
    private async Task<(StableSeedOutcome, ScrapeError?)> WriteSeedIfNewerAsync(string candidateText, string? incoming, string origin, CancellationToken ct)
    {
        var path = StableCachePath;
        var stored = await ReadStoredVersionAsync(path, ct).ConfigureAwait(false);
        if (stored.Exists && !ApiFileVersion.ShouldReplace(stored.Version, incoming))
        {
            _log?.Info($"Stable cache {stored.Version} is as new as the {origin} seed ({incoming ?? "no version"}) or newer; kept.");
            return (StableSeedOutcome.Kept, null);
        }

        try
        {
            Directory.CreateDirectory(Directories.Cache);
            await AtomicFile.WriteAllTextAsync(path, candidateText, ct).ConfigureAwait(false);
            _log?.Info($"Stable cache {(stored.Exists ? $"{stored.Version ?? "(unreadable)"} replaced" : "created")} with {incoming ?? "(no version)"} from {origin}");
            return (StableSeedOutcome.Written, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Warning($"Could not write the stable cache {path}", e);
            return (StableSeedOutcome.Failed, ScrapeError.FromException(SourceId, e, _time));
        }
    }

    /// <summary>
    /// The <c>api_file_version</c> of the bundled seed, read without building the whole cache (the bundle ships
    /// with the app and is checked by the tests, so it needs no validation at each start).
    /// </summary>
    private static string? ReadVersion(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return VersionOf(doc.RootElement);
    }

    private static string? VersionOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(ApiFileVersionKey, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>The stored cache's <c>api_file_version</c>; <c>Version</c> is null when the file is unreadable or has none.</summary>
    private static async Task<(bool Exists, string? Version)> ReadStoredVersionAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return (false, null);
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return (true, VersionOf(doc.RootElement));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return (true, null);
        }
    }

    private async Task<(string? Body, ScrapeError? Error)> FetchAsync(Uri url, CancellationToken ct)
    {
        using var cts = NetTimeouts.Linked(ct, NetTimeouts.Request);
        try
        {
            using var response = await _http().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = GitHubRateLimit.IsRateLimited(response)
                    ? ScrapeError.GitHubRateLimited(SourceId, response)
                    : ScrapeError.HttpStatusError(SourceId, response.StatusCode, $"GET {url} returned {(int)response.StatusCode}");
                _log?.Warning($"Fetching {url} failed: HTTP {(int)response.StatusCode}");
                return (null, error);
            }

            return (await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException ||
                                  (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            _log?.Warning($"Fetching {url} failed", e);
            return (null, ScrapeError.FromException(SourceId, e, _time));
        }
    }

    private ScrapeError Invalid(Uri url, string why)
    {
        _log?.Warning($"Fetched {url} is {why}; keeping the current data.");
        return new ScrapeError(SourceId, ScrapeMessageKeys.Generic, null, $"{url}: {why}", [new("err", why)]);
    }

    /// <summary>Replaces the state; returns the event to raise when the data differs, else null.</summary>
    private ApiDataChangedEventArgs? Swap(ApiData data, ApiDataSource source)
    {
        var previous = Volatile.Read(ref _state);
        Volatile.Write(ref _state, new State(data, source));
        return SameData(previous.Data, data) ? null : new ApiDataChangedEventArgs(previous.Data, data, source);
    }

    private void Raise(ApiDataChangedEventArgs? args)
    {
        if (args is not null)
        {
            Changed?.Invoke(this, args);
        }
    }

    private static bool SameData(ApiData a, ApiData b) => a.FileVersion == b.FileVersion && a.Series.SequenceEqual(b.Series);

    private sealed record State(ApiData Data, ApiDataSource Source);
}
