using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlenderLauncher.Core.IO;
using BlenderLauncher.Core.Logging;

namespace BlenderLauncher.Core.Scraping;

/// <summary>
/// Where the scrape service keeps the time of the last completed check (upstream's
/// <c>Internal/last_time_checked_utc</c>). The app uses <see cref="Settings.SettingsScrapeStateStore"/>.
/// </summary>
public interface IScrapeStateStore
{
    /// <summary>The last check time, or null when there has been none (upstream's default is the epoch).</summary>
    Task<DateTimeOffset?> LoadLastCheckedAsync(CancellationToken ct = default);

    /// <summary>Stores the last check time. Failures are the store's to log; they must not throw.</summary>
    Task SaveLastCheckedAsync(DateTimeOffset time, CancellationToken ct = default);
}

/// <summary>An <see cref="IScrapeStateStore"/> that only remembers in memory (tests, <c>--dev-scrape</c>).</summary>
public sealed class InMemoryScrapeStateStore(DateTimeOffset? lastChecked = null) : IScrapeStateStore
{
    private DateTimeOffset? _lastChecked = lastChecked;

    public DateTimeOffset? LastChecked => _lastChecked;

    public Task<DateTimeOffset?> LoadLastCheckedAsync(CancellationToken ct = default) => Task.FromResult(_lastChecked);

    public Task SaveLastCheckedAsync(DateTimeOffset time, CancellationToken ct = default)
    {
        _lastChecked = time;
        return Task.CompletedTask;
    }
}

/// <summary>
/// <see cref="IScrapeStateStore"/> as <c>&lt;data&gt;\state\scrape-state.json</c>:
/// <c>{"last_time_checked": "2026-11-11T13:05:00+00:00"}</c>, written atomically. A missing or unreadable file
/// reads as "never checked".
/// </summary>
/// <remarks>
/// Superseded by <see cref="Settings.SettingsScrapeStateStore"/>, which keeps the time in <c>settings.json</c>.
/// Kept only so that store can read an old <c>scrape-state.json</c> once and delete it; nothing else should use it.
/// </remarks>
public sealed class FileScrapeStateStore : IScrapeStateStore
{
    /// <summary>The file name in <see cref="DataDirectories.State"/>.</summary>
    public const string FileName = "scrape-state.json";

    private readonly Log? _log;

    public FileScrapeStateStore(DataDirectories directories, Log? log = null)
    {
        ArgumentNullException.ThrowIfNull(directories);
        FilePath = Path.Combine(directories.State, FileName);
        _log = log;
    }

    public string FilePath { get; }

    public async Task<DateTimeOffset?> LoadLastCheckedAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await File.ReadAllTextAsync(FilePath, ct).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize(json, ScrapeStateJsonContext.Default.ScrapeStateDocument);
            return document?.LastTimeChecked is { } text &&
                   DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                ? time
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public async Task SaveLastCheckedAsync(DateTimeOffset time, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var document = new ScrapeStateDocument { LastTimeChecked = time.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) };
            await AtomicFile.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(document, ScrapeStateJsonContext.Default.ScrapeStateDocument), ct)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Error($"Failed to write {FilePath}", e);
        }
    }
}

/// <summary>The JSON shape of <see cref="FileScrapeStateStore"/>.</summary>
internal sealed class ScrapeStateDocument
{
    [JsonPropertyName("last_time_checked")]
    public string? LastTimeChecked { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(ScrapeStateDocument))]
internal sealed partial class ScrapeStateJsonContext : JsonSerializerContext;
