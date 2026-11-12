using System.Collections.Immutable;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Logging;

namespace BlenderLauncher.Core.Library;

/// <summary>What changed in a <see cref="LibraryService.Changed"/> event.</summary>
public enum LibraryChangeKind
{
    /// <summary>The library root changed and the list was cleared.</summary>
    Reset,

    /// <summary>An entry appeared.</summary>
    Added,

    /// <summary>An entry was replaced (re-read, edited, initialised).</summary>
    Updated,

    /// <summary>An entry went away.</summary>
    Removed,

    /// <summary>A scan finished (successfully or not); entries it did not see have been removed.</summary>
    ScanCompleted,

    /// <summary>Sidecar state of a build (or the default build) changed; the list itself did not.</summary>
    StateChanged,
}

/// <summary>An immutable view of the installed list.</summary>
public sealed class LibrarySnapshot
{
    private readonly ImmutableSortedDictionary<string, LibraryEntry> _entries;

    internal LibrarySnapshot(string? root, ImmutableSortedDictionary<string, LibraryEntry> entries)
    {
        Root = root;
        _entries = entries;
        Entries = [.. entries.Values];
    }

    /// <summary>An empty snapshot with no library.</summary>
    public static LibrarySnapshot Empty { get; } = new(null, ImmutableSortedDictionary.Create<string, LibraryEntry>(LibraryLayout.PathComparer));

    /// <summary>The library root, or null before the first scan.</summary>
    public string? Root { get; }

    /// <summary>Every entry, ordered by path.</summary>
    public ImmutableArray<LibraryEntry> Entries { get; }

    /// <summary>The builds that were read successfully.</summary>
    public IEnumerable<BuildInfo> Builds => Entries.Where(e => e.Build is not null).Select(e => e.Build!);

    /// <summary>The entry at <paramref name="path"/>, or null.</summary>
    public LibraryEntry? Find(string path) =>
        _entries.TryGetValue(LibraryLayout.NormaliseRoot(path), out var entry) ? entry : null;

    internal ImmutableSortedDictionary<string, LibraryEntry> Map => _entries;
}

/// <summary>Arguments of <see cref="LibraryService.Changed"/>.</summary>
public sealed class LibraryChangedEventArgs(LibrarySnapshot snapshot, LibraryChangeKind kind, string? path, LibraryEntry? entry) : EventArgs
{
    /// <summary>The list after the change.</summary>
    public LibrarySnapshot Snapshot { get; } = snapshot;

    /// <summary>What changed.</summary>
    public LibraryChangeKind Kind { get; } = kind;

    /// <summary>The affected build folder, when one is.</summary>
    public string? Path { get; } = path;

    /// <summary>The new entry (null for <see cref="LibraryChangeKind.Removed"/> and the list-wide kinds).</summary>
    public LibraryEntry? Entry { get; } = entry;
}

/// <summary>
/// Owner of the installed list: scans, adds, updates and forgets entries, persists user edits
/// (favourite, name, frozen, custom build fields) to <c>.blinfo</c>, and fronts the <see cref="LibraryState"/>
/// sidecar and the default build. The list is exposed as immutable snapshots; <see cref="Changed"/> is raised on
/// worker threads. Removing a build from disk is the pipeline's job; it calls <see cref="Removed"/> afterwards.
/// </summary>
public sealed class LibraryService : IDisposable
{
    private readonly BuildInfoReader _reader;
    private readonly LibraryState _state;
    private readonly Log? _log;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Lazy<BuildSizeMeasurer> _measurer = new(() => new BuildSizeMeasurer());
    private LibrarySnapshot _snapshot = LibrarySnapshot.Empty;
    private CancellationTokenSource? _scanCts;
    private int _scans;

    /// <summary>Creates the service. Nothing is read until <see cref="ScanAsync"/>.</summary>
    public LibraryService(BuildInfoReader reader, LibraryState state, Log? log = null, TimeProvider? time = null)
    {
        _reader = reader;
        _state = state;
        _log = log;
        _time = time ?? TimeProvider.System;
        _state.Changed += OnStateChanged;
    }

    /// <summary>Raised on a worker thread after each change.</summary>
    public event EventHandler<LibraryChangedEventArgs>? Changed;

    /// <summary>The current list.</summary>
    public LibrarySnapshot Current => Volatile.Read(ref _snapshot);

    /// <summary>The library root, or null before the first scan.</summary>
    public string? LibraryRoot => Current.Root;

    /// <summary>True while a scan runs.</summary>
    public bool IsScanning => Volatile.Read(ref _scans) > 0;

    /// <summary>
    /// Scans <paramref name="libraryRoot"/> (all branch folders, or only <paramref name="folders"/>). A different
    /// root clears the list first. Entries are added or updated as they are read; when the scan completes, entries
    /// in the scanned folders that it did not see are removed. A new scan cancels one still running.
    /// </summary>
    public async Task ScanAsync(string libraryRoot, IReadOnlyCollection<string>? folders = null, CancellationToken ct = default)
    {
        var root = LibraryLayout.NormaliseRoot(libraryRoot);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var previous = Interlocked.Exchange(ref _scanCts, cts);
        TryCancel(previous);

        LibraryChangedEventArgs? reset = null;
        lock (_gate)
        {
            if (!LibraryLayout.PathComparer.Equals(_snapshot.Root, root))
            {
                _snapshot = new LibrarySnapshot(root, LibrarySnapshot.Empty.Map);
                reset = new(_snapshot, LibraryChangeKind.Reset, null, null);
            }
        }

        Raise(reset);
        Interlocked.Increment(ref _scans);
        var seen = new HashSet<string>(LibraryLayout.PathComparer);
        var completed = false;
        try
        {
            await foreach (var entry in LibraryScanner.ScanAsync(root, _reader, folders, ct: cts.Token).ConfigureAwait(false))
            {
                seen.Add(entry.Path);
                Upsert(root, entry);
            }

            completed = true;
        }
        finally
        {
            Interlocked.Decrement(ref _scans);
            Interlocked.CompareExchange(ref _scanCts, null, cts);
            if (completed)
            {
                Prune(root, folders ?? LibraryLayout.BuildFolders, seen);
            }

            Raise(new(Current, LibraryChangeKind.ScanCompleted, null, null));
        }
    }

    /// <summary>"Reload" on the custom page: rescans <c>custom</c> only.</summary>
    public Task ReloadCustomAsync(CancellationToken ct = default) =>
        ScanAsync(RequireRoot(), [LibraryLayout.CustomFolderName], ct);

    /// <summary>
    /// Adds a freshly installed (or imported) build: reads it with <paramref name="info"/> (scraped data) as the
    /// fallback, writes its <c>.blinfo</c>, and stamps the installed-on time when <paramref name="newInstall"/>.
    /// </summary>
    public async Task<LibraryEntry> AddAsync(string buildFolder, BuildInfo? info = null, string? archiveName = null, bool newInstall = true, CancellationToken ct = default)
    {
        var root = RequireRoot();
        var path = LibraryLayout.NormaliseRoot(buildFolder);
        LibraryEntry entry;
        try
        {
            var build = await _reader.ReadAsync(path, archiveName, info, autoWrite: true, ct).ConfigureAwait(false);
            entry = new LibraryEntry(path, ParentOf(path), LibraryEntryKind.Build, build);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _log?.Error($"Failed to read build info for {path}", e);
            entry = LibraryScanner.IsRecognised(path)
                ? new LibraryEntry(path, ParentOf(path), LibraryEntryKind.Damaged, Error: e)
                : new LibraryEntry(path, ParentOf(path), LibraryEntryKind.Unrecognised);
        }

        if (newInstall && entry.Kind == LibraryEntryKind.Build)
        {
            var now = _time.GetUtcNow();
            _state.Update(root, path, s => s.InstalledOn is null ? s with { InstalledOn = now } : s);
        }

        Upsert(root, entry);
        return entry;
    }

    /// <summary>Reads one folder again (for example after a template install or an edit made elsewhere).</summary>
    public async Task<LibraryEntry> RefreshAsync(string buildFolder, CancellationToken ct = default)
    {
        var root = RequireRoot();
        var entry = await LibraryScanner.ReadEntryAsync(_reader, buildFolder, ct: ct).ConfigureAwait(false);
        if (Directory.Exists(entry.Path))
        {
            Upsert(root, entry);
        }
        else
        {
            Removed(entry.Path);
        }

        return entry;
    }

    /// <summary>Called once a build folder has been removed from disk: drops the entry and its sidecar state.</summary>
    public void Removed(string buildFolder)
    {
        var path = LibraryLayout.NormaliseRoot(buildFolder);
        LibraryChangedEventArgs? args = null;
        string? root;
        lock (_gate)
        {
            root = _snapshot.Root;
            if (_snapshot.Map.ContainsKey(path))
            {
                _snapshot = new LibrarySnapshot(root, _snapshot.Map.Remove(path));
                args = new(_snapshot, LibraryChangeKind.Removed, path, null);
            }
        }

        if (root is not null)
        {
            _state.Remove(root, path);
        }

        Raise(args);
    }

    /// <summary>Marks or unmarks a favourite, persisted to <c>.blinfo</c>.</summary>
    public Task<BuildInfo> SetFavouriteAsync(string buildFolder, bool favourite, CancellationToken ct = default) =>
        EditAsync(buildFolder, b => b with { IsFavorite = favourite }, ct);

    /// <summary>Sets the custom name ("Rename Branch"); empty clears it. Persisted to <c>.blinfo</c>.</summary>
    public Task<BuildInfo> SetCustomNameAsync(string buildFolder, string customName, CancellationToken ct = default) =>
        EditAsync(buildFolder, b => b with { CustomName = customName.Length > 256 ? customName[..256] : customName }, ct);

    /// <summary>Freezes or unfreezes updates, persisted to <c>.blinfo</c>.</summary>
    public Task<BuildInfo> SetFrozenAsync(string buildFolder, bool frozen, CancellationToken ct = default) =>
        EditAsync(buildFolder, b => b with { IsFrozen = frozen }, ct);

    /// <summary>
    /// Applies <paramref name="change"/> to a build's info and rewrites its <c>.blinfo</c> (re-read first).
    /// Used by the edits above and the "Edit Build..." dialog.
    /// </summary>
    public async Task<BuildInfo> EditAsync(string buildFolder, Func<BuildInfo, BuildInfo> change, CancellationToken ct = default)
    {
        var root = RequireRoot();
        var path = LibraryLayout.NormaliseRoot(buildFolder);
        var entry = Current.Find(path);
        if (entry?.Build is not { } build)
        {
            throw new InvalidOperationException($"{path} is not a readable build in the library");
        }

        var updated = await _reader.UpdateAsync(build, change, ct).ConfigureAwait(false);
        Upsert(root, entry with { Build = updated });
        return updated;
    }

    /// <summary>"Initialise" for an unrecognised (or damaged) folder: writes <paramref name="fields"/> as its <c>.blinfo</c>.</summary>
    public async Task<LibraryEntry> InitialiseAsync(string buildFolder, BuildInfo fields, CancellationToken ct = default)
    {
        var root = RequireRoot();
        var path = LibraryLayout.NormaliseRoot(buildFolder);
        var build = await _reader.WriteAsync(path, fields, ct).ConfigureAwait(false);
        var entry = new LibraryEntry(path, ParentOf(path), LibraryEntryKind.Build, build);
        Upsert(root, entry);
        return entry;
    }

    /// <summary>The sidecar state of a build in the current library.</summary>
    public BuildState GetState(string buildFolder) => _state.Get(RequireRoot(), buildFolder);

    /// <summary>Changes the sidecar state of a build in the current library.</summary>
    public BuildState UpdateState(string buildFolder, Func<BuildState, BuildState> change) =>
        _state.Update(RequireRoot(), buildFolder, change);

    /// <summary>Installed-on time with the folder-creation fallback.</summary>
    public DateTimeOffset? GetInstalledOn(string buildFolder) => _state.GetInstalledOn(RequireRoot(), buildFolder);

    /// <summary>Stamps the last-used time (called by the launch service).</summary>
    public void MarkLaunched(string buildFolder)
    {
        var now = _time.GetUtcNow();
        UpdateState(buildFolder, s => s with { LastUsed = now });
    }

    /// <summary>The current library's default build, or null.</summary>
    public string? DefaultBuild => LibraryRoot is { } root ? _state.GetDefaultBuild(root) : null;

    /// <summary>Sets or clears the current library's default build.</summary>
    public void SetDefaultBuild(string? buildFolder) => _state.SetDefaultBuild(RequireRoot(), buildFolder);

    /// <summary>
    /// Fills in missing sizes and Python versions for the current builds in the background, one folder at a time
    /// at below-normal priority. With <paramref name="force"/>, sizes are measured again even when known.
    /// </summary>
    public async Task MeasureAsync(bool force = false, CancellationToken ct = default)
    {
        var root = RequireRoot();
        foreach (var entry in Current.Entries.Where(e => e.Kind == LibraryEntryKind.Build))
        {
            ct.ThrowIfCancellationRequested();
            var state = _state.Get(root, entry.Path);
            if (state.PythonVersion is null && BuildFolderInspector.ReadPythonVersion(entry.Path) is { } python)
            {
                _state.Update(root, entry.Path, s => s with { PythonVersion = python });
            }

            if (state.SizeBytes is not null && !force)
            {
                continue;
            }

            try
            {
                var size = await _measurer.Value.MeasureAsync(entry.Path, ct).ConfigureAwait(false);
                var at = _time.GetUtcNow();
                _state.Update(root, entry.Path, s => s with { SizeBytes = size, SizeMeasuredAt = at });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log?.Debug($"Could not measure {entry.Path}: {e.Message}");
            }
        }
    }

    /// <summary>Cancels a running scan and stops the size measurer.</summary>
    public void Dispose()
    {
        _state.Changed -= OnStateChanged;
        TryCancel(Volatile.Read(ref _scanCts));
        if (_measurer.IsValueCreated)
        {
            _measurer.Value.Dispose();
        }
    }

    private void Upsert(string root, LibraryEntry entry)
    {
        LibraryChangedEventArgs? args = null;
        lock (_gate)
        {
            if (!LibraryLayout.PathComparer.Equals(_snapshot.Root, root))
            {
                return; // A late result from a scan of another library.
            }

            var exists = _snapshot.Map.TryGetValue(entry.Path, out var old);
            if (exists && old == entry)
            {
                return;
            }

            _snapshot = new LibrarySnapshot(root, _snapshot.Map.SetItem(entry.Path, entry));
            args = new(_snapshot, exists ? LibraryChangeKind.Updated : LibraryChangeKind.Added, entry.Path, entry);
        }

        Raise(args);
    }

    private void Prune(string root, IEnumerable<string> folders, HashSet<string> seen)
    {
        var scanned = new HashSet<string>(folders, StringComparer.Ordinal);
        var removed = new List<LibraryChangedEventArgs>();
        lock (_gate)
        {
            if (!LibraryLayout.PathComparer.Equals(_snapshot.Root, root))
            {
                return;
            }

            foreach (var entry in _snapshot.Entries)
            {
                if (scanned.Contains(entry.ParentFolder) && !seen.Contains(entry.Path))
                {
                    _snapshot = new LibrarySnapshot(root, _snapshot.Map.Remove(entry.Path));
                    removed.Add(new(_snapshot, LibraryChangeKind.Removed, entry.Path, null));
                }
            }
        }

        foreach (var args in removed)
        {
            Raise(args);
        }
    }

    private void OnStateChanged(object? sender, LibraryStateChangedEventArgs e)
    {
        var snapshot = Current;
        if (!LibraryLayout.PathComparer.Equals(snapshot.Root, e.LibraryRoot))
        {
            return;
        }

        var path = e.BuildFolder is null ? null : Path.GetFullPath(Path.Combine(e.LibraryRoot, e.BuildFolder));
        Raise(new(snapshot, LibraryChangeKind.StateChanged, path, path is null ? null : snapshot.Find(path)));
    }

    private void Raise(LibraryChangedEventArgs? args)
    {
        if (args is null)
        {
            return;
        }

        try
        {
            Changed?.Invoke(this, args);
        }
        catch (Exception e)
        {
            _log?.Error("A library change handler threw", e);
        }
    }

    private static void TryCancel(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private string RequireRoot() => LibraryRoot ?? throw new InvalidOperationException("No library has been scanned yet");

    private static string ParentOf(string path) => Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
}
