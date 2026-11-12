using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.IO;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Scraping.Stable;

/// <summary>
/// Stable and LTS releases from <c>https://download.blender.org/release/</c> (upstream
/// <c>threads/scraping/stable.py</c>).
/// </summary>
/// <remarks>
/// <para>The index lists one <c>BlenderX.Y/</c> folder per series with its modified date. Series below
/// <see cref="ScrapeContext.MinimumStableVersion"/> are skipped. A folder whose date matches the cache in
/// <c>&lt;CacheDirectory&gt;/stable_builds.json</c> is served from the cache without a request; any other folder is
/// fetched and its platform's archives (<see cref="BuildPlatform.FileNameFilter"/>) replace the cached ones. The
/// cache is upstream's <c>ScraperCache</c> format, so the hosted or bundled seed the API-data service copies there
/// loads directly, and it is saved (atomically, <c>api_file_version</c> minor bumped) only when a folder changed.</para>
/// <para>The cache holds every archive for the OS; the architecture rule (<see cref="PassesArchitecture"/>) is
/// applied as builds are yielded, so one cache serves x64 and arm64 machines, as the hosted seed does.</para>
/// <para>Differences from upstream:</para>
/// <list type="bullet">
/// <item>Each build is yielded once (upstream yielded freshly scraped builds and then the cached copies again).</item>
/// <item>The commit time comes from the file's own listing line; with no date there, the folder's date is used
/// (upstream raised <c>NameError</c> on an unassigned variable).</item>
/// <item>The listing's size column becomes <see cref="AvailableBuild.SizeBytes"/>. The upstream cache format has no
/// field for it, so sizes are kept beside it in <c>stable_sizes.json</c> (<see cref="BuildSizeCache"/>, as the
/// Bforartists source does).</item>
/// <item>A folder that fails to load (HTTP error or a Cloudflare challenge after three attempts) keeps its old
/// cache entry and date, so the next check retries it; upstream cached it as empty under the new date. The other
/// folders are still scraped and the first error is thrown at the end.</item>
/// <item>Index folders that map to the same series (<c>Blender2.48/</c> and <c>Blender2.48a/</c>) share one cache
/// entry dated with the newest of their dates, so they are not re-fetched on every check.</item>
/// <item>The Cloudflare wait uses <see cref="ScrapeContext.Time"/>, and a challenge on a folder page is retried
/// too.</item>
/// <item>With <see cref="ScrapeContext.RebuildStableCache"/> (<c>--build-cache</c>) every folder from 2.48 is
/// re-fetched for <see cref="ScrapeContext.Os"/> only and the <c>api_file_version</c> major is bumped; upstream
/// looped over all three platforms into separate files.</item>
/// </list>
/// </remarks>
public sealed partial class StableBuildSource : IBuildSource
{
    /// <summary>The cache file in <see cref="ScrapeContext.CacheDirectory"/> (upstream's name).</summary>
    public const string CacheFileName = "stable_builds.json";

    /// <summary>The size sidecar in <see cref="ScrapeContext.CacheDirectory"/>: download link to bytes.</summary>
    public const string SizesFileName = "stable_sizes.json";

    /// <summary>The release index.</summary>
    public static readonly Uri IndexUrl = new("https://download.blender.org/release/");

    /// <summary>The minimum series when rebuilding the cache, and for the setting value <c>"None"</c>.</summary>
    public static readonly SemVer AllVersionsMinimum = new(2, 48, 0);

    public string Id => BuildSourceIds.Stable;

    public IReadOnlyList<Channel> Channels { get; } = [Channel.Stable, Channel.Lts];

    public async IAsyncEnumerable<AvailableBuild> ScrapeAsync(ScrapeContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = cancellationToken;
        var cachePath = Path.Combine(context.CacheDirectory, CacheFileName);
        var sizesPath = Path.Combine(context.CacheDirectory, SizesFileName);
        var rebuild = context.RebuildStableCache;

        var cache = await ScraperCache.LoadOrEmptyAsync(cachePath, context.Lts, context.Time, ct).ConfigureAwait(false);
        var sizes = await BuildSizeCache.LoadAsync(sizesPath, ct).ConfigureAwait(false);

        var index = await FetchAsync(context, IndexUrl, ct).ConfigureAwait(false);
        if (index.Error is { } indexError)
        {
            throw new ScrapeSourceException(indexError);
        }

        IReadOnlyList<StableIndexFolder> folders = index.Challenged ? [] : StableIndexParser.ParseIndex(index.Body!);
        if (folders.Count == 0)
        {
            context.Log?.Info($"Failed to gather stable releases for {context.Os}");
            throw new ScrapeSourceException(index.Challenged
                ? new ScrapeError(Id, ScrapeMessageKeys.CloudflareBlocked, detail: $"{IndexUrl} returned a Cloudflare challenge {CloudflareChallenge.Attempts} times")
                : new ScrapeError(Id, ScrapeMessageKeys.NoStableReleases, detail: $"{IndexUrl} listed no Blender release folders"));
        }

        var minimum = rebuild ? AllVersionsMinimum : context.MinimumStableVersion;
        var fileFilter = BuildPlatform.FileNameFilter(context.Os);
        var modified = false;
        ScrapeError? firstError = null;

        foreach (var series in GroupBySeries(folders, context.Log))
        {
            if (series.Version < minimum)
            {
                continue;
            }

            var cached = cache.Contains(series.Version) ? cache[series.Version] : null;
            var date = series.ModifiedDate;
            if (!rebuild && date is { } d && cached is not null && cached.ModifiedDate == IsoTimestamp.Naive(d))
            {
                context.Log?.Debug($"Skipping {series.Version}: {d:s} unchanged");
                foreach (var build in Available(cached.Assets, context, sizes))
                {
                    yield return build;
                }

                continue;
            }

            var scraped = await ScrapeSeriesAsync(context, series, fileFilter, ct).ConfigureAwait(false);
            if (scraped.Error is { } error)
            {
                // Keep the old entry and its date, so the next check retries this folder.
                firstError ??= error;
                context.Log?.Warning($"Stable folder {series.Version} failed: {error.Detail}");
                if (cached is not null && !rebuild)
                {
                    foreach (var build in Available(cached.Assets, context, sizes))
                    {
                        yield return build;
                    }
                }

                continue;
            }

            if (date is { } newDate)
            {
                context.Log?.Debug($"Caching {series.Version}: {newDate:s} (previous was {cached?.ModifiedDate.ToString() ?? "none"})");
                var folder = cache.NewBuild(series.Version, IsoTimestamp.Naive(newDate));
                folder.Assets.AddRange(scraped.Builds.Select(b => b.Build));
                modified = true;
            }

            foreach (var (build, size) in scraped.Builds)
            {
                sizes.Set(build.Link, size);
            }

            foreach (var build in Available(scraped.Builds.Select(b => b.Build), context, sizes))
            {
                yield return build;
            }
        }

        if (modified)
        {
            await SaveAsync(context, cache, sizes, cachePath, rebuild, ct).ConfigureAwait(false);
        }

        if (firstError is not null)
        {
            throw new ScrapeSourceException(firstError);
        }
    }

    /// <summary>
    /// The architecture rule for stable archives: an ARM machine (<c>arm64</c>, <c>aarch64</c>) gets the links
    /// naming <c>arm64</c> or <c>aarch64</c>, any other machine the links that name neither. Upstream applied this
    /// only on macOS, and its Intel check compared against <c>x64</c> where Python reports <c>x86_64</c>, so Intel
    /// Macs saw both DMGs. On Windows the scrape service's post-filter applies the same
    /// split again.
    /// </summary>
    public static bool PassesArchitecture(string machine, string link)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(link);
        var armMachine = machine.Equals("arm64", StringComparison.OrdinalIgnoreCase) || machine.Equals("aarch64", StringComparison.OrdinalIgnoreCase);
        var armLink = link.Contains("arm64", StringComparison.OrdinalIgnoreCase) || link.Contains("aarch64", StringComparison.OrdinalIgnoreCase);
        return armMachine == armLink;
    }

    /// <summary>
    /// A build for one listed file (upstream's <c>new_blender_build</c>), or null when its name holds no version
    /// or no date is known. <paramref name="folderDate"/> stands in for a missing listing date.
    /// </summary>
    internal static BuildInfo? CreateBuild(ScrapeContext context, Uri folderUrl, StableFolderFile file, DateTime? folderDate)
    {
        var link = new Uri(folderUrl, file.Href).AbsoluteUri.TrimEnd('/');
        var stem = Path.GetFileNameWithoutExtension(link[(link.LastIndexOf('/') + 1)..]);

        if (!BlenderVersionParser.TryParse(stem, out var version, search: true))
        {
            context.Log?.Warning($"Skipping {link}: no version in the file name");
            return null;
        }

        if ((file.Modified ?? folderDate) is not { } gmt)
        {
            context.Log?.Warning($"Skipping {link}: no date in the listing");
            return null;
        }

        var hashes = HashPattern().Matches(stem);
        var hash = hashes.Count > 0 ? hashes[^1].Value.Replace("-", "", StringComparison.Ordinal) : null;

        // Upstream: dateparser.parse(f"{date} {time} GMT").astimezone(), so the listing's GMT time in local time.
        var commitTime = IsoTimestamp.Aware(gmt, TimeSpan.Zero).ToZone(context.Time.LocalTimeZone);
        return new BuildInfo
        {
            Link = link,
            Subversion = version.ToString(),
            BuildHash = hash,
            CommitTime = commitTime,
            Branch = "stable",
        }.PromoteLts(context.Lts);
    }

    private IEnumerable<AvailableBuild> Available(IEnumerable<BuildInfo> builds, ScrapeContext context, BuildSizeCache sizes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var build in builds)
        {
            if (!seen.Add(build.Link) || !PassesArchitecture(context.Machine, build.Link))
            {
                continue;
            }

            yield return new AvailableBuild(build) { SizeBytes = sizes.Get(build.Link) };
        }
    }

    private async Task<SeriesResult> ScrapeSeriesAsync(ScrapeContext context, Series series, Regex fileFilter, CancellationToken ct)
    {
        var builds = new List<(BuildInfo Build, long? Size)>();
        var links = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in series.Folders)
        {
            var folderUrl = new Uri(IndexUrl, folder.Href);
            var page = await FetchAsync(context, folderUrl, ct).ConfigureAwait(false);
            if (page.Error is not null || page.Challenged)
            {
                return new SeriesResult([], page.Error ?? new ScrapeError(Id, ScrapeMessageKeys.CloudflareBlocked,
                    detail: $"{folderUrl} returned a Cloudflare challenge {CloudflareChallenge.Attempts} times"));
            }

            var folderDate = StableIndexParser.TryParseListingDate(folder.Modified, out var d) ? d : (DateTime?)null;
            foreach (var file in StableIndexParser.ParseFolder(page.Body!, fileFilter))
            {
                if (CreateBuild(context, folderUrl, file, folderDate) is { } build && links.Add(build.Link))
                {
                    builds.Add((build, file.SizeBytes));
                }
            }
        }

        return new SeriesResult(builds, null);
    }

    /// <summary>GET with upstream's Cloudflare handling: up to three attempts, <see cref="CloudflareChallenge.RetryDelay"/> apart.</summary>
    private async Task<FetchResult> FetchAsync(ScrapeContext context, Uri url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            string body;
            HttpStatusCode status;
            try
            {
                // The body is read whatever the status: Cloudflare serves its challenge with 403 or 503, and
                // upstream (urllib3) looked at the body without checking the status.
                using var cts = NetTimeouts.Linked(ct, NetTimeouts.Request);
                using var response = await context.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                status = response.StatusCode;
                body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or IOException || NetTimeouts.IsTimeout(e, ct))
            {
                return new FetchResult(null, false, ScrapeError.ConnectionFailed(Id, context.Time, $"{url}: {e.GetType().Name}: {e.Message}"));
            }

            if (!CloudflareChallenge.IsChallenge(body))
            {
                return (int)status is >= 200 and < 300
                    ? new FetchResult(body, false, null)
                    : new FetchResult(null, false, ScrapeError.HttpStatusError(Id, status, $"{url} answered {(int)status} {status}"));
            }

            context.Log?.Warning($"{url.Host} returned a Cloudflare challenge page (attempt {attempt}/{CloudflareChallenge.Attempts}) for {url}");
            if (attempt >= CloudflareChallenge.Attempts)
            {
                return new FetchResult(body, true, null);
            }

            await Task.Delay(CloudflareChallenge.RetryDelay, context.Time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Index folders grouped by series, in index order; a series is dated with its newest folder's date.</summary>
    private static List<Series> GroupBySeries(IReadOnlyList<StableIndexFolder> folders, Logging.Log? log)
    {
        var bySeries = new OrderedDictionary<SemVer, List<StableIndexFolder>>();
        foreach (var folder in folders)
        {
            if (!BlenderVersionParser.TryParse(folder.Version, out var version))
            {
                log?.Warning($"Skipping index folder {folder.Href}: version {folder.Version} does not parse");
                continue;
            }

            if (!bySeries.TryGetValue(version, out var list))
            {
                bySeries[version] = list = [];
            }

            list.Add(folder);
        }

        var series = new List<Series>(bySeries.Count);
        foreach (var (version, list) in bySeries)
        {
            // A folder without a usable date cannot be checked against the cache, so its series is fetched every
            // time and not cached (upstream fetched it uncached when the date was missing, skipped it when unparsable).
            DateTime? newest = null;
            foreach (var folder in list)
            {
                if (!StableIndexParser.TryParseListingDate(folder.Modified, out var date))
                {
                    newest = null;
                    break;
                }

                newest = newest is { } n && n >= date ? n : date;
            }

            series.Add(new Series(version, list, newest));
        }

        return series;
    }

    private static async Task SaveAsync(ScrapeContext context, ScraperCache cache, BuildSizeCache sizes,
        string cachePath, bool rebuild, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(context.CacheDirectory);

            // Upstream re-reads the file's version at save time, then bumps the minor (major when rebuilding).
            var stored = await ReadApiFileVersionAsync(cachePath, ct).ConfigureAwait(false);
            var next = ApiFileVersion.TryParse(stored ?? "0.0", out var current)
                ? rebuild ? current.NextMajor() : current.NextMinor()
                : rebuild ? new ApiFileVersion(1, 0) : new ApiFileVersion(0, 1);
            cache.ApiFileVersion = next.ToString();
            await cache.SaveAsync(cachePath, ct).ConfigureAwait(false);
            context.Log?.Info($"Saved updated stable cache {next} to {cachePath}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            context.Log?.Error($"Failed to write the stable cache to {cachePath}", e);
        }

        // Sizes for links the cache no longer holds are dropped.
        await sizes.SaveAsync(cache.Folders.Values.SelectMany(f => f.Assets).Select(b => b.Link), context.Log, ct).ConfigureAwait(false);
    }

    /// <summary>The top-level <c>api_file_version</c> of the file at <paramref name="path"/>, or null when it is missing, unreadable or not a string.</summary>
    private static async Task<string?> ReadApiFileVersionAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("api_file_version", out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private sealed record Series(SemVer Version, IReadOnlyList<StableIndexFolder> Folders, DateTime? ModifiedDate);

    private sealed record SeriesResult(IReadOnlyList<(BuildInfo Build, long? Size)> Builds, ScrapeError? Error);

    private sealed record FetchResult(string? Body, bool Challenged, ScrapeError? Error);

    // Upstream: HASH_RE = re.compile(r"\w{12}").
    [GeneratedRegex(@"\w{12}", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
}
