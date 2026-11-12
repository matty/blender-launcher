using System.Text.Json;
using System.Text.Json.Serialization;
using BlenderLauncher.Core.IO;
using BlenderLauncher.Core.Logging;

namespace BlenderLauncher.Core.Scraping;

/// <summary>
/// A source's archive sizes, kept beside its index cache in <see cref="ScrapeContext.CacheDirectory"/> as a JSON
/// object of download link to bytes (<c>stable_sizes.json</c>, <c>bforartists_sizes.json</c>). Upstream's
/// <c>ScraperCache</c> format has no field for a size, so it cannot live in the cache itself, and sizes that cost a
/// request (a listing page, a <c>HEAD</c>) are worth keeping across restarts.
/// </summary>
/// <remarks>
/// One instance per scrape: <see cref="LoadAsync"/> at the start, <see cref="Set"/> as sizes are seen, then
/// <see cref="SaveAsync"/> with the links the source still knows, which drops the rest. A missing or unreadable file
/// loads as empty. Not thread-safe; a source scrapes on one logical flow.
/// </remarks>
public sealed class BuildSizeCache
{
    private readonly Dictionary<string, long> _sizes;
    private bool _dirty;

    private BuildSizeCache(string path, Dictionary<string, long> sizes)
    {
        Path = path;
        _sizes = sizes;
    }

    /// <summary>The sidecar file.</summary>
    public string Path { get; }

    /// <summary>How many links have a size.</summary>
    public int Count => _sizes.Count;

    /// <summary>The file name for source <paramref name="sourceId"/>: <c>{sourceId}_sizes.json</c>.</summary>
    public static string FileNameFor(string sourceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        return sourceId + "_sizes.json";
    }

    /// <summary>Reads <paramref name="path"/>; empty when it is missing or not a JSON object of numbers.</summary>
    public static async Task<BuildSizeCache> LoadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var sizes = JsonSerializer.Deserialize(json, BuildSizeJsonContext.Default.DictionaryStringInt64);
            return new BuildSizeCache(path, sizes is null ? new(StringComparer.Ordinal) : new(sizes, StringComparer.Ordinal));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new BuildSizeCache(path, new(StringComparer.Ordinal));
        }
    }

    /// <summary>The size of <paramref name="link"/>, or null when unknown.</summary>
    public long? Get(string link) => _sizes.TryGetValue(link, out var size) ? size : null;

    /// <summary>Records <paramref name="size"/> for <paramref name="link"/>; null forgets it.</summary>
    public void Set(string link, long? size)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (size is { } bytes)
        {
            if (!_sizes.TryGetValue(link, out var old) || old != bytes)
            {
                _sizes[link] = bytes;
                _dirty = true;
            }
        }
        else if (_sizes.Remove(link))
        {
            _dirty = true;
        }
    }

    /// <summary>
    /// Writes the file atomically, keeping only <paramref name="keep"/> (the links the source's cache still holds),
    /// sorted by link. Skips the write when nothing changed. A write failure is logged, not thrown.
    /// </summary>
    public async Task SaveAsync(IEnumerable<string> keep, Log? log = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keep);
        var keepSet = keep as IReadOnlySet<string> ?? keep.ToHashSet(StringComparer.Ordinal);
        foreach (var link in _sizes.Keys.Where(k => !keepSet.Contains(k)).ToList())
        {
            _sizes.Remove(link);
            _dirty = true;
        }

        if (!_dirty && File.Exists(Path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var sorted = _sizes.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            await AtomicFile.WriteAllTextAsync(Path, JsonSerializer.Serialize(sorted, BuildSizeJsonContext.Default.DictionaryStringInt64), ct)
                .ConfigureAwait(false);
            _dirty = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Error($"Failed to write the size cache {Path}", e);
        }
    }
}

/// <summary>JSON for <see cref="BuildSizeCache"/>: download link to bytes.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(Dictionary<string, long>))]
internal sealed partial class BuildSizeJsonContext : JsonSerializerContext;
