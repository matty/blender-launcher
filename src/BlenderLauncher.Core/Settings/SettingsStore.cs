using System.Text.Json;
using System.Text.Json.Nodes;
using BlenderLauncher.Core.IO;
using BlenderLauncher.Core.Logging;

namespace BlenderLauncher.Core.Settings;

/// <summary>How <see cref="SettingsStore"/> found <c>settings.json</c>.</summary>
public enum SettingsLoadOutcome
{
    /// <summary>No file: every default.</summary>
    New,

    /// <summary>Read as is.</summary>
    Loaded,

    /// <summary>Read and brought up to the current schema; the old file was kept as <c>settings.v&lt;N&gt;.bak</c>.</summary>
    Migrated,

    /// <summary>
    /// The file was not a JSON object, so the defaults are used. The file is copied to
    /// <c>settings.json.corrupt</c> before anything overwrites it.
    /// </summary>
    Corrupt,
}

/// <summary>Where and how <see cref="SettingsStore"/> keeps its file.</summary>
public sealed record SettingsStoreOptions
{
    /// <summary>The settings file (<see cref="DataDirectories.SettingsFile"/>), or null for a store that never writes.</summary>
    public string? FilePath { get; init; }

    public Log? Log { get; init; }

    /// <summary>Clock for the save debounce.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How long after the last change the file is written.</summary>
    public TimeSpan SaveDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The schema this build writes.</summary>
    public int CurrentSchemaVersion { get; init; } = AppSettings.CurrentSchemaVersion;

    /// <summary>The migration steps (<see cref="SettingsMigrations.All"/>).</summary>
    public IReadOnlyList<SettingsMigration> Migrations { get; init; } = SettingsMigrations.All;
}

/// <summary>
/// The single in-memory <see cref="AppSettings"/>. Changes go through <see cref="Update"/>, which
/// validates, raises <see cref="Changed"/> with the keys that changed, and schedules a debounced atomic write.
/// <para>
/// On load the JSON is migrated step by step to the current <c>schemaVersion</c> before binding, keeping the
/// original as <c>settings.v&lt;N&gt;.bak</c> once. Members this build does not know are kept and written back.
/// A file from a newer build keeps its higher <c>schemaVersion</c>, so that build does not migrate it twice.
/// </para>
/// Thread-safe. <see cref="Changed"/> is raised on the thread that called <see cref="Update"/>.
/// </summary>
public sealed class SettingsStore : IAsyncDisposable
{
    private readonly SettingsStoreOptions _options;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly ITimer? _timer;
    private AppSettings _current;
    private long _version;
    private long _savedVersion;
    private Task _pendingSave = Task.CompletedTask;
    private int _disposed;

    private SettingsStore(SettingsStoreOptions options, AppSettings initial, SettingsLoadOutcome outcome, bool dirty)
    {
        _options = options;
        _current = initial;
        LoadOutcome = outcome;
        if (options.FilePath is not null)
        {
            _timer = options.Time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        if (dirty)
        {
            _version = 1;
            ScheduleSave();
        }
    }

    /// <summary>Raised after <see cref="Update"/> changed at least one value.</summary>
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>The current settings. Never null; replace through <see cref="Update"/>.</summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public string? FilePath => _options.FilePath;

    public SettingsLoadOutcome LoadOutcome { get; }

    /// <summary>Number of completed writes (tests).</summary>
    internal int SaveCount { get; private set; }

    /// <summary>A store that holds settings in memory only (tests, tools).</summary>
    public static SettingsStore InMemory(AppSettings? initial = null) =>
        new(new SettingsStoreOptions(), (initial ?? AppSettings.Default).Normalised(), SettingsLoadOutcome.New, dirty: false);

    /// <summary>Reads, migrates and validates the file in <paramref name="options"/>; a missing file gives the defaults.</summary>
    public static async Task<SettingsStore> LoadAsync(SettingsStoreOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.FilePath is not { } path)
        {
            return new SettingsStore(options, AppSettings.Default, SettingsLoadOutcome.New, dirty: false);
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new SettingsStore(options, AppSettings.Default, SettingsLoadOutcome.New, dirty: false);
        }

        // An empty file is how portable mode is switched on (README): treat it as new settings, not as corrupt.
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SettingsStore(options, AppSettings.Default, SettingsLoadOutcome.New, dirty: true);
        }

        JsonObject? document = null;
        try
        {
            document = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (JsonException)
        {
        }

        if (document is null)
        {
            options.Log?.Error($"Settings file {path} is not a JSON object; using defaults. A copy is kept as {Path.GetFileName(path)}.corrupt.");
            TryCopy(path, path + ".corrupt", overwrite: true, options.Log);
            return new SettingsStore(options, AppSettings.Default, SettingsLoadOutcome.Corrupt, dirty: false);
        }

        var fileVersion = ReadSchemaVersion(document);
        var outcome = SettingsLoadOutcome.Loaded;
        var dirty = false;
        if (fileVersion < options.CurrentSchemaVersion)
        {
            var backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $"settings.v{fileVersion}.bak");
            if (!File.Exists(backup))
            {
                TryCopy(path, backup, overwrite: false, options.Log);
            }

            var reached = SettingsMigrations.Run(document, fileVersion, options.CurrentSchemaVersion, options.Migrations);
            options.Log?.Info($"Settings migrated from schema {fileVersion} to {reached}.");
            fileVersion = reached;
            outcome = SettingsLoadOutcome.Migrated;
            dirty = true;
        }

        var settings = Bind(document, options.Log) with { SchemaVersion = fileVersion };
        return new SettingsStore(options, settings.Normalised(), outcome, dirty);
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the current settings, validates the result, and when anything changed
    /// raises <see cref="Changed"/> and schedules a save. Returns the keys that changed (empty for none).
    /// </summary>
    public IReadOnlyList<SettingKey> Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        AppSettings previous;
        AppSettings next;
        IReadOnlyList<SettingKey> changed;
        lock (_gate)
        {
            previous = _current;
            next = change(previous).Normalised() with { SchemaVersion = previous.SchemaVersion };
            changed = Diff(previous, next);
            if (changed.Count == 0)
            {
                return changed;
            }

            _current = next;
            _version++;
        }

        ScheduleSave();
        Changed?.Invoke(this, new SettingsChangedEventArgs(previous, next, changed));
        return changed;
    }

    /// <summary>Writes pending changes now. Write failures are logged, not thrown.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Task pending;
        lock (_gate)
        {
            pending = _pendingSave;
        }

        await pending.ConfigureAwait(false);
        await SaveAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Waits for a save the debounce timer has started (tests).</summary>
    internal Task WaitForPendingSaveAsync()
    {
        lock (_gate)
        {
            return _pendingSave;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await FlushAsync().ConfigureAwait(false);
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
        }

        _saveGate.Dispose();
    }

    /// <summary>The settings as written to the file.</summary>
    public static string Serialise(AppSettings settings) => JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);

    /// <summary>The keys whose JSON value differs between <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static IReadOnlyList<SettingKey> Diff(AppSettings a, AppSettings b)
    {
        var left = ToObject(a);
        var right = ToObject(b);
        var result = new List<SettingKey>();
        foreach (var name in left.Select(p => p.Key).Union(right.Select(p => p.Key), StringComparer.Ordinal))
        {
            var l = left[name];
            var r = right[name];
            if (l is JsonObject lo && r is JsonObject ro && IsGroup(name))
            {
                foreach (var member in lo.Select(p => p.Key).Union(ro.Select(p => p.Key), StringComparer.Ordinal))
                {
                    if (!JsonNode.DeepEquals(lo[member], ro[member]))
                    {
                        result.Add(new SettingKey(name, member));
                    }
                }
            }
            else if (!JsonNode.DeepEquals(l, r))
            {
                result.Add(new SettingKey(SettingKey.RootGroup, name));
            }
        }

        return result;
    }

    private static bool IsGroup(string name) => name is SettingsGroups.General or SettingsGroups.Appearance or SettingsGroups.Connection
        or SettingsGroups.Builds or SettingsGroups.Launching or SettingsGroups.Internal;

    private static JsonObject ToObject(AppSettings settings) =>
        JsonSerializer.SerializeToNode(settings, SettingsJsonContext.Default.AppSettings)!.AsObject();

    private static int ReadSchemaVersion(JsonObject document)
    {
        try
        {
            return document["schemaVersion"] is JsonValue v && v.TryGetValue<int>(out var n) ? Math.Max(n, 0) : 1;
        }
        catch (InvalidOperationException)
        {
            return 1;
        }
    }

    /// <summary>
    /// Binds the document. Members it lacks are first filled from the defaults (<see cref="FillDefaults"/>). When
    /// the whole fails (a member of the wrong JSON type), each group is bound on its own and a group that still
    /// fails falls back to its defaults, so one bad value does not lose every setting.
    /// </summary>
    private static AppSettings Bind(JsonObject document, Log? log)
    {
        var ctx = SettingsJsonContext.Default;
        FillDefaults(document, ToObject(AppSettings.Default));
        try
        {
            return document.Deserialize(ctx.AppSettings) ?? AppSettings.Default;
        }
        catch (JsonException e)
        {
            log?.Warning($"Settings file has a malformed value ({e.Message}); reading it group by group.");
        }

        var extra = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, node) in document)
        {
            if (!IsGroup(name) && name != "schemaVersion")
            {
                using var parsed = JsonDocument.Parse(node?.ToJsonString() ?? "null");
                extra[name] = parsed.RootElement.Clone();
            }
        }

        return new AppSettings
        {
            General = Group(document, SettingsGroups.General, ctx.GeneralSettings, log) ?? new(),
            Appearance = Group(document, SettingsGroups.Appearance, ctx.AppearanceSettings, log) ?? new(),
            Connection = Group(document, SettingsGroups.Connection, ctx.ConnectionSettings, log) ?? new(),
            Builds = Group(document, SettingsGroups.Builds, ctx.BuildsSettings, log) ?? new(),
            Launching = Group(document, SettingsGroups.Launching, ctx.LaunchingSettings, log) ?? new(),
            Internal = Group(document, SettingsGroups.Internal, ctx.InternalSettings, log) ?? new(),
            Extra = extra.Count == 0 ? null : extra,
        };
    }

    /// <summary>
    /// Copies every member of <paramref name="defaults"/> that <paramref name="target"/> lacks, recursing into
    /// objects. The generated deserialiser sets init-only members through the constructor, so a member missing
    /// from the file would otherwise get <c>default(T)</c> instead of its initialiser's value. Entries of
    /// <c>viewSorts</c> are filled from <see cref="ViewSort.Default"/>.
    /// </summary>
    private static void FillDefaults(JsonObject target, JsonObject defaults)
    {
        foreach (var (name, value) in defaults)
        {
            if (!target.ContainsKey(name) || target[name] is null && value is JsonObject)
            {
                target[name] = value?.DeepClone();
            }
            else if (target[name] is JsonObject child && value is JsonObject childDefaults)
            {
                FillDefaults(child, childDefaults);
            }
        }

        if (target["viewSorts"] is JsonObject sorts)
        {
            var sortDefaults = JsonSerializer.SerializeToNode(ViewSort.Default, SettingsJsonContext.Default.ViewSort)!.AsObject();
            foreach (var (_, entry) in sorts)
            {
                if (entry is JsonObject e)
                {
                    FillDefaults(e, sortDefaults);
                }
            }
        }
    }

    private static T? Group<T>(JsonObject document, string name, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, Log? log)
        where T : class
    {
        if (document[name] is not JsonObject node)
        {
            return null;
        }

        try
        {
            return node.Deserialize(info);
        }
        catch (JsonException e)
        {
            log?.Warning($"Settings group '{name}' is malformed ({e.Message}); using its defaults.");
            return null;
        }
    }

    private void ScheduleSave() => _timer?.Change(_options.SaveDelay, Timeout.InfiniteTimeSpan);

    private void OnTimer()
    {
        lock (_gate)
        {
            _pendingSave = _pendingSave.ContinueWith(_ => SaveAsync(CancellationToken.None), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        if (_options.FilePath is not { } path)
        {
            return;
        }

        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AppSettings snapshot;
            long version;
            lock (_gate)
            {
                if (_version == _savedVersion)
                {
                    return;
                }

                snapshot = _current;
                version = _version;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await AtomicFile.WriteAllTextAsync(path, Serialise(snapshot), ct).ConfigureAwait(false);
            lock (_gate)
            {
                _savedVersion = Math.Max(_savedVersion, version);
            }

            SaveCount++;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _options.Log?.Error($"Failed to write settings to {path}", e);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static void TryCopy(string from, string to, bool overwrite, Log? log)
    {
        try
        {
            File.Copy(from, to, overwrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Warning($"Could not copy {from} to {to}", e);
        }
    }
}
