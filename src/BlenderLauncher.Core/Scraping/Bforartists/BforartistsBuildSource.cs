using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.IO;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Scraping.Bforartists;

/// <summary>
/// Bforartists builds, a port of upstream's <c>ScraperBfa</c> (<c>threads/scraping/bfa.py</c>):
/// the stable index <see cref="DownloadsUrl"/> and the developer index <see cref="DevBuildsUrl"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Stable sections give the version (<c>Bforartists M.m.p</c> in the summary); the date comes from the
/// previous cache when it has the link, otherwise from a <c>HEAD</c> request's <c>Last-Modified</c>, which also
/// gives the size (<c>Content-Length</c>). An entry with no date is skipped.</item>
/// <item>Developer sections give only a date, so the version is <c>0.0.YYYYMMDD-dev</c> and the date is the
/// commit time; such builds are identified by commit time (<see cref="BuildInfo.IsBfaDev"/>).</item>
/// <item>The cache <see cref="CacheFileName"/> is rebuilt on every scrape from what the indexes list now, so
/// stale links (expired developer builds, legacy Nextcloud links) drop out.</item>
/// </list>
/// Differences from upstream: a failed index is reported as a <see cref="ScrapeSourceException"/> (after the other
/// index has been scraped) rather than only logged, and its entries are carried over from the previous cache instead
/// of being dropped, so the next scrape does not send every <c>HEAD</c> again. Sizes seen in <c>HEAD</c> responses are
/// kept in <see cref="SizesFileName"/> (<see cref="BuildSizeCache"/>, as the stable source does), because the cache
/// format has nowhere to keep them; like the cache, it is pruned to the links listed now.
/// </remarks>
public sealed partial class BforartistsBuildSource : IBuildSource
{
    public static readonly Uri DownloadsUrl = new("https://downloads.bforartists.com/");

    public static readonly Uri DevBuildsUrl = new("https://devbuilds.bforartists.com/");

    /// <summary>The cache file in <see cref="ScrapeContext.CacheDirectory"/> (upstream's <c>bfa_cache_path()</c>).</summary>
    public const string CacheFileName = "bforartists_builds.json";

    /// <summary>The size sidecar in <see cref="ScrapeContext.CacheDirectory"/>: download link to bytes.</summary>
    public const string SizesFileName = "bforartists_sizes.json";

    /// <summary>The branch of every build this source yields.</summary>
    public const string Branch = "bforartists";

    private static readonly string[] MacArmKeywords = ["silicon", "arm64"];
    private static readonly string[] MacIntelKeywords = ["intel", "x86_64", "x64"];

    public string Id => BuildSourceIds.Bforartists;

    public IReadOnlyList<Channel> Channels { get; } = [Channel.Bforartists];

    public async IAsyncEnumerable<AvailableBuild> ScrapeAsync(ScrapeContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var cachePath = Path.Combine(context.CacheDirectory, CacheFileName);
        var previous = await ScraperCache.LoadOrEmptyAsync(cachePath, time: context.Time, ct: cancellationToken).ConfigureAwait(false);
        var sizes = await BuildSizeCache.LoadAsync(Path.Combine(context.CacheDirectory, SizesFileName), cancellationToken).ConfigureAwait(false);
        var previousByLink = new Dictionary<string, BuildInfo>(StringComparer.Ordinal);
        foreach (var asset in previous.Folders.Values.SelectMany(f => f.Assets))
        {
            previousByLink.TryAdd(asset.Link, asset);
        }

        var fresh = new ScraperCache();
        ScrapeError? firstError = null;
        foreach (var (indexUrl, developer) in new[] { (DownloadsUrl, false), (DevBuildsUrl, true) })
        {
            var (html, error) = await FetchIndexAsync(context, indexUrl, cancellationToken).ConfigureAwait(false);
            if (html is null)
            {
                firstError ??= error;
                CarryOver(previous, fresh, indexUrl);
                continue;
            }

            foreach (var section in BfaIndexParser.Parse(html, indexUrl))
            {
                if (!TryReadHeading(section.Title, developer, out var version, out var date))
                {
                    if (section.Title.Length > 0 && (developer ? DevDateRegex() : StableTitleRegex()).IsMatch(section.Title))
                    {
                        context.Log?.Warning($"Invalid Bforartists index heading: {section.Title}");
                    }

                    continue;
                }

                var candidates = section.Links.Where(l => MatchesPlatform(context.Os, FileNameOf(l))).ToList();
                if (context.Os == HostOs.MacOS)
                {
                    candidates = [.. FilterMacCandidates(candidates, FileNameOf, context.Machine)];
                }

                foreach (var link in candidates)
                {
                    var linkText = link.AbsoluteUri;
                    var timestamp = date;
                    var size = sizes.Get(linkText);
                    if (timestamp is null)
                    {
                        if (previousByLink.TryGetValue(linkText, out var cached))
                        {
                            timestamp = cached.CommitTime;
                        }
                        else
                        {
                            (timestamp, size) = await ReleaseInfoAsync(context, link, cancellationToken).ConfigureAwait(false);
                            if (size is not null)
                            {
                                sizes.Set(linkText, size);
                            }
                        }
                    }

                    if (timestamp is not { } commitTime)
                    {
                        continue;
                    }

                    var build = new BuildInfo
                    {
                        Link = linkText,
                        Subversion = version.ToString(),
                        BuildHash = null,
                        CommitTime = commitTime,
                        Branch = Branch,
                        CustomExecutable = ExecutableFor(context.Os),
                    };
                    if (!fresh.Contains(version))
                    {
                        fresh.NewBuild(version, commitTime);
                    }

                    fresh[version].Assets.Add(build);
                    yield return new AvailableBuild(build) { SizeBytes = size };
                }
            }
        }

        try
        {
            Directory.CreateDirectory(context.CacheDirectory);

            // Upstream writes this cache compact (json.dump with no indent).
            await AtomicFile.WriteAllTextAsync(cachePath, fresh.ToJson(indent: 0), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            context.Log?.Error("Failed to write Bforartists cache", e);
        }

        await sizes.SaveAsync(fresh.Folders.Values.SelectMany(f => f.Assets).Select(b => b.Link), context.Log, cancellationToken)
            .ConfigureAwait(false);

        if (firstError is not null)
        {
            throw new ScrapeSourceException(firstError);
        }
    }

    /// <summary>
    /// Upstream's <c>matches_platform</c> on a file name, lowercased: Windows takes <c>.zip</c> files named with
    /// <c>windows</c> or <c>win64</c>, or the legacy <c>bforartists2_NNN.zip</c>/<c>bforartists3_NNN.zip</c>; macOS
    /// takes <c>.dmg</c>; Linux takes <c>.tar.xz</c> or <c>.tar.gz</c> named with <c>linux</c>.
    /// </summary>
    public static bool MatchesPlatform(HostOs os, string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var name = fileName.ToLowerInvariant();
        return os switch
        {
            HostOs.Windows => name.EndsWith(".zip", StringComparison.Ordinal) &&
                              (name.Contains("windows", StringComparison.Ordinal) || name.Contains("win64", StringComparison.Ordinal) ||
                               LegacyWindowsZipRegex().IsMatch(name)),
            HostOs.MacOS => name.EndsWith(".dmg", StringComparison.Ordinal),
            _ => name.Contains("linux", StringComparison.Ordinal) &&
                 (name.EndsWith(".tar.xz", StringComparison.Ordinal) || name.EndsWith(".tar.gz", StringComparison.Ordinal)),
        };
    }

    /// <summary>
    /// Upstream's <c>filter_macos_candidates</c>: drops packages named for the other architecture, then returns the
    /// first native package, or else the first remaining (universal or unlabelled) one. At most one result.
    /// </summary>
    public static IReadOnlyList<T> FilterMacCandidates<T>(IEnumerable<T> candidates, Func<T, string> fileName, string machine)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(fileName);
        var isArm = machine == "arm64";
        var own = isArm ? MacArmKeywords : MacIntelKeywords;
        var other = isArm ? MacIntelKeywords : MacArmKeywords;
        bool Has(T c, string[] keywords)
        {
            var name = fileName(c).ToLowerInvariant();
            return keywords.Any(k => name.Contains(k, StringComparison.Ordinal));
        }

        var filtered = candidates.Where(c => !Has(c, other)).ToList();
        var native = filtered.Where(c => Has(c, own)).ToList();
        return native.Count > 0 ? [native[0]] : filtered.Count > 0 ? [filtered[0]] : [];
    }

    /// <summary>
    /// Reads a section summary: on the developer index, the first <c>YYYY-MM-DD</c> date gives version
    /// <c>0.0.YYYYMMDD-dev</c> and the date (UTC midnight); on the stable index, <c>Bforartists M.m.p</c> gives the
    /// version and no date. False when the heading has neither or the date or version is invalid.
    /// </summary>
    public static bool TryReadHeading(string title, bool developer, out SemVer version, out IsoTimestamp? date)
    {
        ArgumentNullException.ThrowIfNull(title);
        version = default;
        date = null;
        if (developer)
        {
            var match = DevDateRegex().Match(title);
            if (!match.Success ||
                !DateTime.TryParseExact(match.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                return false;
            }

            version = new SemVer(0, 0, (day.Year * 10000) + (day.Month * 100) + day.Day, "dev");
            date = IsoTimestamp.Aware(day, TimeSpan.Zero);
            return true;
        }

        var stable = StableTitleRegex().Match(title);
        return stable.Success && SemVer.TryParse(stable.Groups[1].Value, out version);
    }

    /// <summary>Upstream's <c>custom_executable</c> for Bforartists: <c>bforartists.exe</c>, <c>bforartists</c>, or none on macOS.</summary>
    public static string? ExecutableFor(HostOs os) => os switch
    {
        HostOs.Windows => "bforartists.exe",
        HostOs.Linux => "bforartists",
        _ => null,
    };

    /// <summary>The unescaped last path segment of <paramref name="link"/> (upstream's <c>unquote(PurePosixPath(path).name)</c>).</summary>
    public static string FileNameOf(Uri link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var path = link.AbsolutePath;
        return Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
    }

    /// <summary>The index page, or null and the error (logged) when it cannot be fetched or is not a 200.</summary>
    private static async Task<(string? Html, ScrapeError? Error)> FetchIndexAsync(ScrapeContext context, Uri url, CancellationToken ct)
    {
        try
        {
            using var cts = NetTimeouts.Linked(ct, NetTimeouts.Request);
            using var response = await context.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                context.Log?.Warning($"Failed to fetch Bforartists index {url}: HTTP {(int)response.StatusCode}");
                return (null, ScrapeError.HttpStatusError(BuildSourceIds.Bforartists, response.StatusCode, $"{url} answered {(int)response.StatusCode}"));
            }

            return (await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException || NetTimeouts.IsTimeout(e, ct))
        {
            context.Log?.Warning($"Failed to fetch Bforartists index {url}", e);
            return (null, ScrapeError.FromException(BuildSourceIds.Bforartists, e, context.Time));
        }
    }

    /// <summary>Upstream's <c>release_timestamp</c>: <c>HEAD</c> the file for its <c>Last-Modified</c> date (UTC when naive), plus its size.</summary>
    private static async Task<(IsoTimestamp? Date, long? Size)> ReleaseInfoAsync(ScrapeContext context, Uri link, CancellationToken ct)
    {
        try
        {
            using var cts = NetTimeouts.Linked(ct, NetTimeouts.Probe);
            using var request = new HttpRequestMessage(HttpMethod.Head, link);
            using var response = await context.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return (null, null);
            }

            var size = response.Content.Headers.ContentLength;
            if (response.Content.Headers.LastModified is not { } modified)
            {
                context.Log?.Warning($"Missing or invalid Last-Modified for {link}");
                return (null, size);
            }

            return (IsoTimestamp.Aware(modified), size);
        }
        catch (Exception e) when (e is HttpRequestException or IOException || NetTimeouts.IsTimeout(e, ct))
        {
            context.Log?.Warning($"HEAD {link} failed", e);
            return (null, null);
        }
    }

    /// <summary>Keeps the previous cache's entries for an index that could not be read, so its dates are not lost.</summary>
    private static void CarryOver(ScraperCache previous, ScraperCache fresh, Uri indexUrl)
    {
        foreach (var (version, folder) in previous.Folders)
        {
            foreach (var asset in folder.Assets)
            {
                if (!Uri.TryCreate(asset.Link, UriKind.Absolute, out var link) ||
                    !string.Equals(link.Authority, indexUrl.Authority, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!fresh.Contains(version))
                {
                    fresh.NewBuild(version, folder.ModifiedDate);
                }

                fresh[version].Assets.Add(asset);
            }
        }
    }

    [GeneratedRegex(@"\b[0-9]{4}-[0-9]{2}-[0-9]{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex DevDateRegex();

    [GeneratedRegex(@"Bforartists\s+([0-9]+\.[0-9]+\.[0-9]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StableTitleRegex();

    [GeneratedRegex(@"^bforartists[23]_[0-9]+\.zip\z",RegexOptions.CultureInvariant)]
    private static partial Regex LegacyWindowsZipRegex();
}
