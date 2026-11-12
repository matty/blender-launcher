using System.Text;
using System.Text.Json;
using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.IO;
using BlenderLauncher.Core.Serialization;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Scraping;

/// <summary>One cached index folder: its assets and the folder's modified date as the index showed it.</summary>
public sealed class StableFolder
{
    public StableFolder(IsoTimestamp modifiedDate, IEnumerable<BuildInfo>? assets = null)
    {
        ModifiedDate = modifiedDate;
        Assets = assets is null ? [] : [.. assets];
    }

    public List<BuildInfo> Assets { get; }

    /// <summary>Naive or aware exactly as read; a naive date stays naive when saved.</summary>
    public IsoTimestamp ModifiedDate { get; set; }
}

/// <summary>
/// Upstream's <c>ScraperCache</c> (<c>modules/scraper_cache.py</c>): cached stable or
/// Bforartists index folders keyed by version, in insertion order. The hosted and bundled stable caches use
/// this format, so they load directly.
/// </summary>
public sealed class ScraperCache
{
    /// <summary>The <c>api_file_version</c> text, present on stable caches only. Written first when set.</summary>
    public string? ApiFileVersion { get; set; }

    /// <summary>Folders by version (build metadata is ignored in keys, as in python-semver), in file order.</summary>
    public OrderedDictionary<SemVer, StableFolder> Folders { get; } = [];

    public bool Contains(SemVer version) => Folders.ContainsKey(version);

    public StableFolder this[SemVer version] => Folders[version];

    /// <summary>Upstream's <c>new_build</c>: adds (or replaces) an empty folder dated <paramref name="modifiedDate"/>, or <see cref="IsoTimestamp.Epoch"/>.</summary>
    public StableFolder NewBuild(SemVer version, IsoTimestamp? modifiedDate = null)
    {
        var folder = new StableFolder(modifiedDate ?? IsoTimestamp.Epoch);
        Folders[version] = folder;
        return folder;
    }

    /// <summary>
    /// Upstream's <c>from_dict</c> on JSON text. Folder keys must be strict semver and dates ISO; assets go
    /// through <see cref="BlinfoCodec.ToBuildInfo"/> (with LTS promotion when <paramref name="lts"/> is given).
    /// Throws <see cref="JsonException"/> or <see cref="FormatException"/> on bad input.
    /// </summary>
    public static ScraperCache FromJson(string json, ILtsLookup? lts = null, TimeProvider? time = null)
    {
        var doc = JsonSerializer.Deserialize(json, LauncherJsonContext.Default.ScraperCacheDocument)
                  ?? throw new JsonException("Cache is the JSON literal null");
        var cache = new ScraperCache { ApiFileVersion = doc.ApiFileVersion };
        foreach (var (key, folder) in doc.Folders ?? [])
        {
            var assets = (folder.Assets ?? []).Select(a =>
                BlinfoCodec.ToBuildInfo(
                    a.Build.Blinfo.FirstOrDefault() ?? throw new FormatException($"Cached asset {a.Link} has no build entry."),
                    a.Link, lts, time));
            cache.Folders[SemVer.Parse(key)] = new StableFolder(
                IsoTimestamp.Parse(folder.ModifiedDate ?? throw new FormatException($"Folder {key} has no modified_date.")),
                assets);
        }

        return cache;
    }

    /// <summary>Upstream's <c>to_dict</c> as a document, plus <see cref="ApiFileVersion"/> when set.</summary>
    public ScraperCacheDocument ToDocument()
    {
        var doc = new ScraperCacheDocument { ApiFileVersion = ApiFileVersion };
        foreach (var (version, folder) in Folders)
        {
            doc.Folders[version.ToString()] = new StableFolderDocument
            {
                Assets = [.. folder.Assets.Select(b => new ScraperCacheAsset(b.Link, BlinfoCodec.ToDocument(b)))],
                ModifiedDate = folder.ModifiedDate.ToString(),
            };
        }

        return doc;
    }

    /// <summary>JSON text indented by <paramref name="indent"/> spaces (upstream saves with 1), or compact when 0.</summary>
    public string ToJson(int indent = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(indent);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indent > 0, IndentSize = Math.Max(indent, 1) }))
        {
            JsonSerializer.Serialize(writer, ToDocument(), LauncherJsonContext.Default.ScraperCacheDocument);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// Upstream's <c>from_file_or_default</c>: the cache in <paramref name="path"/>, or an empty cache when the
    /// file is missing, unreadable or malformed. (Upstream lets a bad folder key or date raise; the port treats
    /// that as malformed too.)
    /// </summary>
    public static async Task<ScraperCache> LoadOrEmptyAsync(string path, ILtsLookup? lts = null, TimeProvider? time = null, CancellationToken ct = default)
    {
        try
        {
            return FromJson(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), lts, time);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return new ScraperCache();
        }
    }

    /// <summary>Saves to <paramref name="path"/> atomically, indented by one space as upstream's <c>_save_cache</c>.</summary>
    public Task SaveAsync(string path, CancellationToken ct = default) => AtomicFile.WriteAllTextAsync(path, ToJson(), ct);
}
