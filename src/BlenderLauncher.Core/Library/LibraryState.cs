using System.Text.Json;
using System.Text.Json.Serialization;
using BlenderLauncher.Core.IO;
using BlenderLauncher.Core.Logging;

namespace BlenderLauncher.Core.Library;

/// <summary>
/// The extras the port keeps for one installed build beyond <c>.blinfo</c>.
/// Times are UTC.
/// </summary>
public sealed record BuildState
{
    /// <summary>An instance with nothing set.</summary>
    public static BuildState Empty { get; } = new();

    /// <summary>When the port installed the build; null for builds it did not install (see <see cref="LibraryState.GetInstalledOn"/>).</summary>
    public DateTimeOffset? InstalledOn { get; init; }

    /// <summary>When the build was last launched from the port.</summary>
    public DateTimeOffset? LastUsed { get; init; }

    /// <summary>Folder size in bytes, as last measured.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>When <see cref="SizeBytes"/> was measured.</summary>
    public DateTimeOffset? SizeMeasuredAt { get; init; }

    /// <summary>The bundled Python's version, for example <c>3.11.9</c>; null when unknown.</summary>
    public string? PythonVersion { get; init; }

    /// <summary>Replace this build when a newer one of its kind is found.</summary>
    public bool ReplaceOnNewBuild { get; init; }

    /// <summary>True when nothing is set, so the entry need not be stored.</summary>
    public bool IsEmpty => this == Empty;
}

/// <summary>
/// The sidecar <c>&lt;state&gt;\library-state.json</c>: per library root, per build folder
/// relative to that root, a <see cref="BuildState"/>, plus the library's default build. Keying by root means
/// switching libraries neither loses nor mixes state. Roots and folders compare case-insensitively on Windows.
/// </summary>
/// <remarks>
/// Thread-safe. Changes are saved atomically after <see cref="SaveDelay"/> without further changes (debounced);
/// <see cref="FlushAsync"/> saves at once. A missing or unreadable file loads as empty (and is logged).
/// </remarks>
public sealed class LibraryState : IAsyncDisposable
{
    /// <summary>The sidecar's file name.</summary>
    public const string FileName = "library-state.json";

    /// <summary>The schema written by this version.</summary>
    public const int SchemaVersion = 1;

    private readonly object _gate = new();
    private readonly Dictionary<string, LibraryRecord> _libraries;
    private readonly TimeProvider _time;
    private readonly Log? _log;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private ITimer? _timer;
    private int _version;
    private int _savedVersion;

    private LibraryState(string path, Dictionary<string, LibraryRecord> libraries, TimeProvider time, Log? log)
    {
        FilePath = path;
        _libraries = libraries;
        _time = time;
        _log = log;
    }

    /// <summary>The sidecar file.</summary>
    public string FilePath { get; }

    /// <summary>The quiet period before a change is written.</summary>
    public TimeSpan SaveDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Raised on a worker thread after any change, with the library root and the build folder (null for the default pointer).</summary>
    public event EventHandler<LibraryStateChangedEventArgs>? Changed;

    /// <summary>Loads <c>&lt;stateDirectory&gt;\library-state.json</c>; empty when missing or unreadable.</summary>
    public static async Task<LibraryState> LoadAsync(string stateDirectory, TimeProvider? time = null, Log? log = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        var path = Path.Combine(Path.GetFullPath(stateDirectory), FileName);
        var libraries = new Dictionary<string, LibraryRecord>(LibraryLayout.PathComparer);
        try
        {
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                var document = JsonSerializer.Deserialize(json, LibraryStateJsonContext.Default.LibraryStateDocument);
                foreach (var (root, library) in document?.Libraries ?? [])
                {
                    if (string.IsNullOrWhiteSpace(root) || library is null)
                    {
                        continue;
                    }

                    var record = libraries.TryGetValue(NormaliseKeyRoot(root), out var existing) ? existing : new LibraryRecord();
                    libraries[NormaliseKeyRoot(root)] = record;
                    record.DefaultBuild ??= library.DefaultBuild is { Length: > 0 } d ? NormaliseRelative(d) : null;
                    foreach (var (folder, build) in library.Builds ?? [])
                    {
                        if (!string.IsNullOrWhiteSpace(folder) && build is not null)
                        {
                            record.Builds.TryAdd(NormaliseRelative(folder), build.ToState());
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            log?.Warning($"Could not read {path}; starting with empty library state", e);
            libraries.Clear();
        }

        return new LibraryState(path, libraries, time ?? TimeProvider.System, log);
    }

    /// <summary>The state of <paramref name="buildFolder"/> (absolute, or relative to the root) in the library at <paramref name="libraryRoot"/>.</summary>
    public BuildState Get(string libraryRoot, string buildFolder)
    {
        var (root, folder) = Key(libraryRoot, buildFolder);
        lock (_gate)
        {
            return _libraries.TryGetValue(root, out var library) && library.Builds.TryGetValue(folder, out var state) ? state : BuildState.Empty;
        }
    }

    /// <summary>Applies <paramref name="change"/> to a build's state and schedules a save. Returns the new state.</summary>
    public BuildState Update(string libraryRoot, string buildFolder, Func<BuildState, BuildState> change)
    {
        var (root, folder) = Key(libraryRoot, buildFolder);
        BuildState next;
        lock (_gate)
        {
            var library = Library(root);
            var current = library.Builds.TryGetValue(folder, out var s) ? s : BuildState.Empty;
            next = change(current);
            if (next == current)
            {
                return current;
            }

            if (next.IsEmpty)
            {
                library.Builds.Remove(folder);
            }
            else
            {
                library.Builds[folder] = next;
            }

            Touched();
        }

        Changed?.Invoke(this, new LibraryStateChangedEventArgs(root, folder));
        return next;
    }

    /// <summary>Forgets a build (after it is removed). The default pointer is cleared when it pointed there.</summary>
    public void Remove(string libraryRoot, string buildFolder)
    {
        var (root, folder) = Key(libraryRoot, buildFolder);
        var changed = false;
        lock (_gate)
        {
            if (_libraries.TryGetValue(root, out var library))
            {
                changed = library.Builds.Remove(folder);
                if (library.DefaultBuild is { } d && LibraryLayout.PathComparer.Equals(d, folder))
                {
                    library.DefaultBuild = null;
                    changed = true;
                }

                if (changed)
                {
                    Touched();
                }
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, new LibraryStateChangedEventArgs(root, folder));
        }
    }

    /// <summary>Moves a build's state, and the default pointer, from one folder to another (after a rename).</summary>
    public void Move(string libraryRoot, string fromFolder, string toFolder)
    {
        var (root, from) = Key(libraryRoot, fromFolder);
        var (_, to) = Key(libraryRoot, toFolder);
        lock (_gate)
        {
            var library = Library(root);
            if (library.Builds.Remove(from, out var state))
            {
                library.Builds[to] = state;
            }

            if (library.DefaultBuild is { } d && LibraryLayout.PathComparer.Equals(d, from))
            {
                library.DefaultBuild = to;
            }

            Touched();
        }

        Changed?.Invoke(this, new LibraryStateChangedEventArgs(root, to));
    }

    /// <summary>The library's default build as an absolute path, or null when none is set.</summary>
    public string? GetDefaultBuild(string libraryRoot)
    {
        var root = NormaliseKeyRoot(libraryRoot);
        lock (_gate)
        {
            return _libraries.TryGetValue(root, out var library) && library.DefaultBuild is { } d
                ? Path.GetFullPath(Path.Combine(root, d.Replace('/', Path.DirectorySeparatorChar)))
                : null;
        }
    }

    /// <summary>Sets (or with null clears) the library's default build.</summary>
    public void SetDefaultBuild(string libraryRoot, string? buildFolder)
    {
        var root = NormaliseKeyRoot(libraryRoot);
        var folder = buildFolder is null ? null : Key(libraryRoot, buildFolder).Folder;
        lock (_gate)
        {
            var library = Library(root);
            if (string.Equals(library.DefaultBuild, folder, StringComparison.Ordinal))
            {
                return;
            }

            library.DefaultBuild = folder;
            Touched();
        }

        Changed?.Invoke(this, new LibraryStateChangedEventArgs(root, null));
    }

    /// <summary>
    /// When the build was installed: the sidecar value, else the folder's creation time (builds installed by
    /// upstream); null when neither is available.
    /// </summary>
    public DateTimeOffset? GetInstalledOn(string libraryRoot, string buildFolder)
    {
        if (Get(libraryRoot, buildFolder).InstalledOn is { } stored)
        {
            return stored;
        }

        try
        {
            var full = Path.GetFullPath(Path.Combine(NormaliseKeyRoot(libraryRoot), buildFolder));
            return Directory.Exists(full) ? new DateTimeOffset(Directory.GetCreationTimeUtc(full), TimeSpan.Zero) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The build folders with state in the library at <paramref name="libraryRoot"/>, relative with forward slashes.</summary>
    public IReadOnlyList<string> FoldersOf(string libraryRoot)
    {
        lock (_gate)
        {
            return _libraries.TryGetValue(NormaliseKeyRoot(libraryRoot), out var library) ? [.. library.Builds.Keys] : [];
        }
    }

    /// <summary>Writes any pending change now.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string json;
            int version;
            lock (_gate)
            {
                version = _version;
                if (version == _savedVersion)
                {
                    return;
                }

                json = JsonSerializer.Serialize(ToDocument(), LibraryStateJsonContext.Default.LibraryStateDocument);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await AtomicFile.WriteAllTextAsync(FilePath, json, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _savedVersion = Math.Max(_savedVersion, version);
            }
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>Flushes and stops the save timer.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        await FlushAsync().ConfigureAwait(false);
        _saveLock.Dispose();
    }

    /// <summary>
    /// The key of a library root: absolute, normalised, no trailing separator. The dictionary compares it
    /// case-insensitively on Windows.
    /// </summary>
    public static string NormaliseKeyRoot(string libraryRoot) => LibraryLayout.NormaliseRoot(libraryRoot);

    /// <summary>
    /// The key of a build within a root: relative to the root, forward slashes. An absolute folder outside the
    /// root is kept as its absolute path.
    /// </summary>
    public static (string Root, string Folder) Key(string libraryRoot, string buildFolder)
    {
        var root = NormaliseKeyRoot(libraryRoot);
        var full = LibraryLayout.NormaliseRoot(Path.Combine(root, buildFolder));
        var relative = LibraryLayout.IsSameOrInside(full, root) ? Path.GetRelativePath(root, full) : full;
        return (root, relative.Replace('\\', '/'));
    }

    private static string NormaliseRelative(string folder) => folder.Replace('\\', '/').Trim('/');

    private LibraryRecord Library(string root)
    {
        if (!_libraries.TryGetValue(root, out var library))
        {
            library = new LibraryRecord();
            _libraries[root] = library;
        }

        return library;
    }

    // Under _gate. Bumps the version and restarts the debounce timer.
    private void Touched()
    {
        _version++;
        _timer ??= _time.CreateTimer(_ => _ = SaveFromTimerAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    private async Task SaveFromTimerAsync()
    {
        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _log?.Warning($"Could not save {FilePath}", e);
        }
    }

    private LibraryStateDocument ToDocument() => new()
    {
        SchemaVersion = SchemaVersion,
        Libraries = _libraries
            .Where(l => l.Value.DefaultBuild is not null || l.Value.Builds.Count > 0)
            .ToDictionary(
                l => l.Key,
                LibraryStateLibrary? (l) => new LibraryStateLibrary
                {
                    DefaultBuild = l.Value.DefaultBuild,
                    Builds = l.Value.Builds.ToDictionary(b => b.Key, BuildStateRecord? (b) => BuildStateRecord.From(b.Value), StringComparer.Ordinal),
                },
                StringComparer.Ordinal),
    };

    private sealed class LibraryRecord
    {
        public string? DefaultBuild { get; set; }

        public Dictionary<string, BuildState> Builds { get; } = new(LibraryLayout.PathComparer);
    }
}

/// <summary>Arguments of <see cref="LibraryState.Changed"/>.</summary>
public sealed class LibraryStateChangedEventArgs(string libraryRoot, string? buildFolder) : EventArgs
{
    /// <summary>The normalised library root.</summary>
    public string LibraryRoot { get; } = libraryRoot;

    /// <summary>The relative build folder, or null when the default pointer changed.</summary>
    public string? BuildFolder { get; } = buildFolder;
}

/// <summary>On-disk shape of the sidecar.</summary>
internal sealed class LibraryStateDocument
{
    public int SchemaVersion { get; set; }

    public Dictionary<string, LibraryStateLibrary?>? Libraries { get; set; }
}

/// <summary>On-disk shape of one library in the sidecar.</summary>
internal sealed class LibraryStateLibrary
{
    public string? DefaultBuild { get; set; }

    public Dictionary<string, BuildStateRecord?>? Builds { get; set; }
}

/// <summary>On-disk shape of one build in the sidecar.</summary>
internal sealed class BuildStateRecord
{
    public DateTimeOffset? InstalledOn { get; set; }

    public DateTimeOffset? LastUsed { get; set; }

    public long? SizeBytes { get; set; }

    public DateTimeOffset? SizeMeasuredAt { get; set; }

    public string? PythonVersion { get; set; }

    public bool ReplaceOnNewBuild { get; set; }

    public static BuildStateRecord From(BuildState s) => new()
    {
        InstalledOn = s.InstalledOn,
        LastUsed = s.LastUsed,
        SizeBytes = s.SizeBytes,
        SizeMeasuredAt = s.SizeMeasuredAt,
        PythonVersion = s.PythonVersion,
        ReplaceOnNewBuild = s.ReplaceOnNewBuild,
    };

    public BuildState ToState() => new()
    {
        InstalledOn = InstalledOn,
        LastUsed = LastUsed,
        SizeBytes = SizeBytes,
        SizeMeasuredAt = SizeMeasuredAt,
        PythonVersion = PythonVersion,
        ReplaceOnNewBuild = ReplaceOnNewBuild,
    };
}

/// <summary>
/// JSON for <see cref="LibraryState"/>, kept beside it as <c>BuildSizeJsonContext</c> is beside its cache, so this
/// file set does not have to edit the shared <c>LauncherJsonContext</c>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(LibraryStateDocument))]
internal sealed partial class LibraryStateJsonContext : JsonSerializerContext;
