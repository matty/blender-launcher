using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Pipeline;
using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Core.Processes;
using BlenderLauncher.Core.Scraping;

namespace BlenderLauncher.Core.Builds;

/// <summary>One build replaced by <see cref="ReplaceOnNewBuild"/>.</summary>
/// <param name="OldFolder">The replaced build; removed once nothing from it runs.</param>
/// <param name="NewFolder">The newly installed build.</param>
/// <param name="Download">The download that was installed.</param>
public sealed record BuildReplacement(string OldFolder, string NewFolder, AvailableBuild Download);

/// <summary>
/// Replace on new build (new in the design): after each check, every installed build whose sidecar flag
/// <see cref="BuildState.ReplaceOnNewBuild"/> is set is replaced by its newer build:
/// <list type="number">
/// <item>The candidate comes from <see cref="BuildUpdateService.FindReplacement"/> (upstream's update rules and the
/// channel's behaviour). Frozen builds and builds in <c>custom</c> are skipped.</item>
/// <item>It is installed through the install callback (the app's <see cref="AppServices.InstallAsync"/>: the
/// <see cref="InstallService"/> job, then the library entry).</item>
/// <item>The favourite flag, the custom name, the default-build pointer, the replace flag itself and any portable
/// settings move to the new build.</item>
/// <item>The old build is removed (to the Recycle Bin or permanently, per the delete setting) only once none of its
/// processes run, waiting on <see cref="LaunchService"/> exits rather than polling; a removal that fails because
/// files are still in use waits for the next exit, or a short delay, and tries again.</item>
/// </list>
/// Runs one at a time; a build already being replaced is not picked again. A removal still waiting when the app
/// quits is dropped: the old build stays installed, without the flag.
/// </summary>
public sealed class ReplaceOnNewBuild : IAsyncDisposable
{
    private readonly LibraryService _library;
    private readonly BuildUpdateService _updates;
    private readonly LaunchService _launches;
    private readonly Func<AvailableBuild, CancellationToken, Task<LibraryEntry?>> _install;
    private readonly Remover _remover;
    private readonly Func<bool> _toRecycleBin;
    private readonly Log? _log;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task> _pending = new(LibraryLayout.PathComparer);
    private int _disposed;

    /// <param name="library">The installed list.</param>
    /// <param name="updates">The update rules.</param>
    /// <param name="launches">Running processes, to wait for before removing.</param>
    /// <param name="install">Installs a download and returns its library entry (null when it did not install).</param>
    /// <param name="remover">Removes the old build.</param>
    /// <param name="toRecycleBin">The delete setting, read per removal: true for the Recycle Bin.</param>
    /// <param name="log">Optional log.</param>
    public ReplaceOnNewBuild(
        LibraryService library,
        BuildUpdateService updates,
        LaunchService launches,
        Func<AvailableBuild, CancellationToken, Task<LibraryEntry?>> install,
        Remover remover,
        Func<bool> toRecycleBin,
        Log? log = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _launches = launches ?? throw new ArgumentNullException(nameof(launches));
        _install = install ?? throw new ArgumentNullException(nameof(install));
        _remover = remover ?? throw new ArgumentNullException(nameof(remover));
        _toRecycleBin = toRecycleBin ?? throw new ArgumentNullException(nameof(toRecycleBin));
        _log = log;
    }

    /// <summary>How long to wait before retrying a removal that failed with files in use. One second by default.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How many removal attempts are made before giving up (the old build then stays). Five by default.</summary>
    public int RemovalAttempts { get; init; } = 5;

    /// <summary>A build was replaced; its old folder is removed later (<see cref="OldBuildRemoved"/>).</summary>
    public event EventHandler<BuildReplacement>? Replaced;

    /// <summary>An old build's folder was removed, with its path.</summary>
    public event EventHandler<string>? OldBuildRemoved;

    /// <summary>Old builds waiting to be removed.</summary>
    public IReadOnlyCollection<string> PendingRemovals
    {
        get
        {
            lock (_gate)
            {
                return [.. _pending.Keys];
            }
        }
    }

    /// <summary>Replaces every flagged build that has a newer build in <paramref name="available"/>.</summary>
    public async Task<IReadOnlyList<BuildReplacement>> RunAsync(IReadOnlyList<AvailableBuild> available, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(available);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = linked.Token;
        await _runGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var results = new List<BuildReplacement>();
            if (_library.LibraryRoot is null)
            {
                return results;
            }

            foreach (var entry in _library.Current.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Build is not { } build || entry.ParentFolder == LibraryLayout.CustomFolderName || build.IsFrozen
                    || !_library.GetState(entry.Path).ReplaceOnNewBuild || IsPending(entry.Path))
                {
                    continue;
                }

                if (_updates.FindReplacement(build, available) is not { } candidate)
                {
                    continue;
                }

                if (await ReplaceAsync(entry.Path, candidate, token).ConfigureAwait(false) is { } replacement)
                {
                    results.Add(replacement);
                }
            }

            return results;
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>Completes when every pending removal has finished (tests, orderly shutdown).</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll([.. _pending.Values]);
        }
    }

    /// <summary>Stops: a run in progress is cancelled and pending removals are dropped.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await WhenIdleAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();
    }

    private async Task<BuildReplacement?> ReplaceAsync(string oldFolder, AvailableBuild candidate, CancellationToken ct)
    {
        _log?.Info($"Replace on new build: installing {candidate.Build.Link} to replace {oldFolder}");
        LibraryEntry? added;
        try
        {
            added = await _install(candidate, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _log?.Error($"Replace on new build: installing {candidate.Build.Link} failed; {oldFolder} is kept", e);
            return null;
        }

        if (added?.Build is not { } newBuild || LibraryLayout.PathComparer.Equals(added.Path, oldFolder))
        {
            _log?.Warning($"Replace on new build: {candidate.Build.Link} did not install as a new build; {oldFolder} is kept");
            return null;
        }

        await TransferAsync(oldFolder, added.Path, ct).ConfigureAwait(false);
        var replacement = new BuildReplacement(oldFolder, added.Path, candidate);
        Raise(Replaced, replacement);
        ScheduleRemoval(oldFolder);
        return replacement;
    }

    // Moves the favourite flag, custom name, default pointer, replace flag and portable settings to the new build.
    private async Task TransferAsync(string oldFolder, string newFolder, CancellationToken ct)
    {
        var old = _library.Current.Find(oldFolder)?.Build;
        var created = _library.Current.Find(newFolder)?.Build;
        if (old is not null && (old.IsFavorite || old.CustomName.Length > 0))
        {
            try
            {
                await _library.EditAsync(newFolder, b => b with
                {
                    IsFavorite = b.IsFavorite || old.IsFavorite,
                    CustomName = old.CustomName.Length > 0 ? old.CustomName : b.CustomName,
                }, ct).ConfigureAwait(false);
                await _library.EditAsync(oldFolder, b => b with { IsFavorite = false, CustomName = "" }, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _log?.Warning($"Replace on new build: could not move the favourite or name from {oldFolder} to {newFolder}", e);
            }
        }

        if (_library.DefaultBuild is { } current && LibraryLayout.PathComparer.Equals(current, oldFolder))
        {
            _library.SetDefaultBuild(newFolder);
        }

        _library.UpdateState(newFolder, s => s with { ReplaceOnNewBuild = true });
        _library.UpdateState(oldFolder, s => s with { ReplaceOnNewBuild = false });

        if (old is not null && created is not null && BuildUpdateService.HasPortableSettings(old))
        {
            try
            {
                var result = await BuildUpdateService.MovePortableSettingsAsync(old, created with { Link = newFolder }, ct).ConfigureAwait(false);
                _log?.Info($"Replace on new build: portable settings of {oldFolder}: {result}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log?.Warning($"Replace on new build: could not copy the portable settings of {oldFolder}", e);
            }
        }
    }

    private void ScheduleRemoval(string oldFolder)
    {
        lock (_gate)
        {
            _pending[oldFolder] = Task.Run(() => RemoveWhenIdleAsync(oldFolder, _lifetime.Token));
        }
    }

    private async Task RemoveWhenIdleAsync(string oldFolder, CancellationToken ct)
    {
        try
        {
            for (var attempt = 1; attempt <= RemovalAttempts; attempt++)
            {
                if (_launches.GetRunningCount(oldFolder) > 0)
                {
                    _log?.Info($"Replace on new build: waiting for {oldFolder} to stop running before removing it");
                }

                await _launches.WhenNotRunningAsync(oldFolder, ct).ConfigureAwait(false);
                try
                {
                    var result = await _remover.RemoveAsync(oldFolder, _toRecycleBin(), ct).ConfigureAwait(false);
                    if (!result.IsGone)
                    {
                        // The bin refused it; deleting permanently needs the user's say, so the old build stays.
                        _log?.Warning($"Replace on new build: {oldFolder} was not removed: {result.Reason}");
                        return;
                    }

                    _library.Removed(oldFolder);
                    Raise(OldBuildRemoved, oldFolder);
                    return;
                }
                catch (PipelineException e) when (e.Kind == PipelineErrorKind.FileInUse && attempt < RemovalAttempts)
                {
                    _log?.Info($"Replace on new build: {oldFolder} is still in use (attempt {attempt}); retrying");
                    await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _log?.Error($"Replace on new build: removing {oldFolder} failed; it is kept", e);
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(oldFolder);
            }
        }
    }

    private bool IsPending(string folder)
    {
        lock (_gate)
        {
            return _pending.ContainsKey(folder);
        }
    }

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        try
        {
            handler?.Invoke(this, args);
        }
        catch (Exception e)
        {
            _log?.Error("A replace-on-new-build handler threw", e);
        }
    }
}
