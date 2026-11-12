using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Scraping;

namespace BlenderLauncher.Core.Settings;

/// <summary>
/// <see cref="IScrapeStateStore"/> backed by <see cref="InternalSettings.LastTimeChecked"/>. On first load, an
/// old <c>&lt;data&gt;\state\scrape-state.json</c> is read into the settings (when they have no time yet),
/// the settings are written, and the old file is deleted.
/// </summary>
public sealed class SettingsScrapeStateStore : IScrapeStateStore
{
    private readonly SettingsStore _settings;
    private readonly DataDirectories? _directories;
    private readonly string? _legacyFile;
    private readonly Log? _log;
    private int _migrated;

    /// <param name="settings">The settings store.</param>
    /// <param name="directories">Where to look for the legacy <c>scrape-state.json</c>; null skips the migration.</param>
    /// <param name="log">Optional log.</param>
    public SettingsScrapeStateStore(SettingsStore settings, DataDirectories? directories, Log? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _directories = directories;
        _legacyFile = directories is null ? null : Path.Combine(directories.State, FileScrapeStateStore.FileName);
        _log = log;
    }

    public async Task<DateTimeOffset?> LoadLastCheckedAsync(CancellationToken ct = default)
    {
        await MigrateLegacyFileAsync(ct).ConfigureAwait(false);
        return _settings.Current.Internal.LastTimeChecked;
    }

    public async Task SaveLastCheckedAsync(DateTimeOffset time, CancellationToken ct = default)
    {
        await MigrateLegacyFileAsync(ct).ConfigureAwait(false);
        _settings.Update(s => s with { Internal = s.Internal with { LastTimeChecked = time } });
    }

    private async Task MigrateLegacyFileAsync(CancellationToken ct)
    {
        if (_legacyFile is null || Interlocked.Exchange(ref _migrated, 1) != 0 || !File.Exists(_legacyFile))
        {
            return;
        }

        var legacy = await new FileScrapeStateStore(_directories!, _log)
            .LoadLastCheckedAsync(ct).ConfigureAwait(false);
        if (legacy is { } time && _settings.Current.Internal.LastTimeChecked is null)
        {
            _settings.Update(s => s with { Internal = s.Internal with { LastTimeChecked = time } });
            _log?.Info($"Moved the last check time from {_legacyFile} into the settings.");
        }

        await _settings.FlushAsync(ct).ConfigureAwait(false);
        try
        {
            File.Delete(_legacyFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Warning($"Could not delete {_legacyFile}", e);
        }
    }
}
