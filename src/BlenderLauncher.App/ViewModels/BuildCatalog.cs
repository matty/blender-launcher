using System.Collections.ObjectModel;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Pipeline;
using BlenderLauncher.Core.Processes;
using BlenderLauncher.Core.Scraping;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlenderLauncher.App.ViewModels;

/// <summary>
/// The rows every region shares: one <see cref="InstalledBuildRowViewModel"/> per library entry and one
/// <see cref="AvailableBuildRowViewModel"/> per scraped build, kept in step with the services (the
/// services own the state; this is a projection and holds nothing of its own beyond it). Each row object lives as long
/// as its build does, so selection and bindings survive updates. UI thread only; service events are marshalled.
/// </summary>
/// <remarks>
/// The collections are in service order (library order; scrape order). Filtering and sorting belong to the list
/// view models, which build their own views over these collections.
/// </remarks>
public sealed partial class BuildCatalog : ViewModelBase
{
    private readonly Dictionary<string, InstalledBuildRowViewModel> _installedByPath = new(LibraryLayout.PathComparer);
    private readonly Dictionary<string, AvailableBuildRowViewModel> _availableByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<InstallJob, AvailableBuildRowViewModel> _jobs = [];
    private AppServices? _services;

    /// <summary>Installed builds, in library order.</summary>
    public ObservableCollection<InstalledBuildRowViewModel> Installed { get; } = [];

    /// <summary>Available builds from the last check, in scrape order.</summary>
    public ObservableCollection<AvailableBuildRowViewModel> Available { get; } = [];

    /// <summary>The library folder, or null when none is set.</summary>
    [ObservableProperty]
    public partial string? LibraryRoot { get; private set; }

    /// <summary>A library scan is running.</summary>
    [ObservableProperty]
    public partial bool IsScanning { get; private set; }

    /// <summary>A check for builds is running.</summary>
    [ObservableProperty]
    public partial bool IsChecking { get; private set; }

    /// <summary>When the last check finished; null before the first one.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? LastChecked { get; private set; }

    /// <summary>The last completed check (errors per source, new count, launcher update).</summary>
    [ObservableProperty]
    public partial ScrapeSnapshot? LastCheck { get; private set; }

    /// <summary>The library's default build, or null.</summary>
    [ObservableProperty]
    public partial InstalledBuildRowViewModel? DefaultBuild { get; private set; }

    /// <summary>Running Blender instances across all builds.</summary>
    [ObservableProperty]
    public partial int TotalRunning { get; private set; }

    /// <summary>Rows with an install job queued or running.</summary>
    public IEnumerable<AvailableBuildRowViewModel> Busy => Available.Where(r => r.IsBusy);

    /// <summary>True once <see cref="Attach"/> has connected live services.</summary>
    public bool IsLive => _services is not null;

    public InstalledBuildRowViewModel? FindInstalled(string path) =>
        _installedByPath.TryGetValue(path, out var row) ? row : null;

    public AvailableBuildRowViewModel? FindAvailable(string key) =>
        _availableByKey.TryGetValue(key, out var row) ? row : null;

    /// <summary>
    /// Connects to live services: loads their current state and follows their events. Call once, on the UI thread.
    /// </summary>
    public void Attach(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (_services is not null)
        {
            throw new InvalidOperationException("The catalog is already attached.");
        }

        _services = services;
        services.Library.Changed += UiDispatcher.Marshal<LibraryChangedEventArgs>(OnLibraryChanged);
        services.LibraryState.Changed += UiDispatcher.Marshal<LibraryStateChangedEventArgs>(OnLibraryStateChanged);
        services.Launches.RunningCountChanged += UiDispatcher.Marshal<RunningCountChangedEventArgs>(OnRunningCountChanged);
        services.Scrape.CheckStarted += UiDispatcher.Marshal<ScrapeCheckStarted>((_, _) => IsChecking = true);
        services.Scrape.CheckCompleted += UiDispatcher.Marshal<ScrapeSnapshot>((_, snapshot) => ApplyCheck(snapshot));
        services.Install.JobsChanged += UiDispatcher.Marshal<InstallJob>(TrackJob);
        services.Updates.UpdatesChanged += UiDispatcher.Marshal<IReadOnlyDictionary<string, BuildUpdate>>((_, updates) => ApplyUpdates(updates));

        SyncInstalled(services.Library.Current);
        IsScanning = services.Library.IsScanning;
        IsChecking = services.Scrape.IsChecking;
        if (services.Scrape.Current is { Builds.Count: > 0 } current)
        {
            ApplyCheck(current);
        }

        foreach (var job in services.Install.Jobs)
        {
            TrackJob(job);
        }

        ApplyUpdates(services.Updates.Current);
    }

    // ------------------------------------------------------------------ installed

    private void OnLibraryChanged(object? sender, LibraryChangedEventArgs e)
    {
        IsScanning = _services?.Library.IsScanning ?? false;
        SyncInstalled(e.Snapshot);
    }

    private void OnLibraryStateChanged(object? sender, LibraryStateChangedEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        if (e.BuildFolder is { } folder && FindInstalled(folder) is { } row)
        {
            ApplyState(row);
        }
        else
        {
            foreach (var each in Installed)
            {
                ApplyState(each);
            }
        }

        ApplyDefault(_services.Library.DefaultBuild);
    }

    private void OnRunningCountChanged(object? sender, RunningCountChangedEventArgs e)
    {
        if (FindInstalled(e.BuildFolder) is { } row)
        {
            row.RunningCount = e.Count;
            row.IsStarting = false;
        }

        TotalRunning = _services?.Launches.TotalRunning ?? Installed.Sum(r => r.RunningCount);
    }

    /// <summary>Brings <see cref="Installed"/> in line with <paramref name="snapshot"/>, reusing rows by path.</summary>
    private void SyncInstalled(LibrarySnapshot snapshot)
    {
        LibraryRoot = snapshot.Root;
        var seen = new HashSet<string>(LibraryLayout.PathComparer);
        var index = 0;
        foreach (var entry in snapshot.Entries)
        {
            seen.Add(entry.Path);
            if (!_installedByPath.TryGetValue(entry.Path, out var row))
            {
                row = new InstalledBuildRowViewModel(entry.Path);
                _installedByPath[entry.Path] = row;
                Installed.Insert(Math.Min(index, Installed.Count), row);
            }
            else
            {
                var at = Installed.IndexOf(row);
                if (at != index && at >= 0 && index < Installed.Count)
                {
                    Installed.Move(at, index);
                }
            }

            row.ApplyEntry(entry);
            ApplyState(row);
            index++;
        }

        for (var i = Installed.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Installed[i].Path))
            {
                _installedByPath.Remove(Installed[i].Path);
                Installed.RemoveAt(i);
            }
        }

        ApplyDefault(_services?.Library.DefaultBuild);
        LinkInstalledCopies();
    }

    private void ApplyState(InstalledBuildRowViewModel row)
    {
        if (_services is not { } services || services.Library.LibraryRoot is null)
        {
            return;
        }

        try
        {
            row.ApplyState(services.Library.GetState(row.Path), services.Library.GetInstalledOn(row.Path));
            row.RunningCount = services.Launches.GetRunningCount(row.Path);
            if (row.Build is { } build)
            {
                row.IsPortable = BuildConfigFolders.GetPortableState(build) == PortableState.Portable;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            services.Log.Warning($"Could not read the state of {row.Path}", e);
        }
    }

    private void ApplyDefault(string? defaultPath)
    {
        InstalledBuildRowViewModel? found = null;
        foreach (var row in Installed)
        {
            var isDefault = defaultPath is not null && LibraryLayout.PathComparer.Equals(row.Path, defaultPath);
            row.IsDefault = isDefault;
            if (isDefault)
            {
                found = row;
            }
        }

        DefaultBuild = found;
    }

    // ------------------------------------------------------------------ available

    /// <summary>Applies a completed check: rows reused by key, new ones added, vanished ones removed.</summary>
    private void ApplyCheck(ScrapeSnapshot snapshot)
    {
        IsChecking = _services?.Scrape.IsChecking ?? false;
        LastCheck = snapshot;
        LastChecked = snapshot.CompletedAt == default ? LastChecked : snapshot.CompletedAt;
        SyncAvailable(snapshot.Builds);
    }

    private void SyncAvailable(IReadOnlyList<AvailableBuild> builds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var build in builds)
        {
            var key = ScrapeService.DeduplicationKey(build.Build);
            if (!seen.Add(key))
            {
                continue;
            }

            if (_availableByKey.TryGetValue(key, out var row))
            {
                row.ApplySource(build);
                var at = Available.IndexOf(row);
                if (at != index && at >= 0 && index < Available.Count)
                {
                    Available.Move(at, index);
                }
            }
            else
            {
                row = new AvailableBuildRowViewModel(build);
                _availableByKey[key] = row;
                Available.Insert(Math.Min(index, Available.Count), row);
            }

            index++;
        }

        for (var i = Available.Count - 1; i >= 0; i--)
        {
            var row = Available[i];
            if (!seen.Contains(row.Key) && !row.IsBusy)
            {
                _availableByKey.Remove(row.Key);
                Available.RemoveAt(i);
            }
        }

        if (_services is { } services)
        {
            foreach (var job in services.Install.Jobs)
            {
                TrackJob(job);
            }
        }

        LinkInstalledCopies();
    }

    private void TrackJob(InstallJob job)
    {
        if (_jobs.ContainsKey(job))
        {
            return;
        }

        var key = ScrapeService.DeduplicationKey(job.Build.Build);
        if (!_availableByKey.TryGetValue(key, out var row))
        {
            row = new AvailableBuildRowViewModel(job.Build);
            _availableByKey[key] = row;
            Available.Add(row);
        }

        _jobs[job] = row;
        job.StateChanged += UiDispatcher.Marshal<InstallJob>(OnJobStateChanged);
        job.ProgressChanged += UiDispatcher.Marshal<InstallJob, TransferProgress>((j, p) =>
        {
            if (_jobs.TryGetValue(j, out var r))
            {
                r.ApplyProgress(p);
            }
        });
        row.ApplyJob(job);
    }

    private void OnJobStateChanged(InstallJob job)
    {
        if (!_jobs.TryGetValue(job, out var row))
        {
            return;
        }

        row.ApplyJob(job);
        if (job.IsFinished)
        {
            _jobs.Remove(job);
            if (job.State != InstallState.Failed)
            {
                row.ApplyJob(null);
            }
        }
    }

    private void ApplyUpdates(IReadOnlyDictionary<string, BuildUpdate> updates)
    {
        foreach (var row in Installed)
        {
            row.Update = updates.TryGetValue(row.Path, out var update) ? update : null;
        }
    }

    /// <summary>Links each download row to its installed copy (same build by upstream's equality rule).</summary>
    private void LinkInstalledCopies()
    {
        var installed = Installed.Where(r => r.Build is not null).ToList();
        foreach (var row in Available)
        {
            InstalledBuildRowViewModel? match = null;
            foreach (var candidate in installed)
            {
                if (candidate.Channel == row.Channel && candidate.Build!.Matches(row.Build))
                {
                    match = candidate;
                    break;
                }
            }

            row.InstalledRow = match;
        }
    }

    // ------------------------------------------------------------------ design-time data

    /// <summary>Fills the catalog without services (sample data, previews, render tests).</summary>
    internal void LoadSample(IEnumerable<InstalledBuildRowViewModel> installed, IEnumerable<AvailableBuildRowViewModel> available,
        string? libraryRoot, DateTimeOffset? lastChecked)
    {
        LibraryRoot = libraryRoot;
        LastChecked = lastChecked;
        foreach (var row in installed)
        {
            _installedByPath[row.Path] = row;
            Installed.Add(row);
        }

        foreach (var row in available)
        {
            _availableByKey[row.Key] = row;
            Available.Add(row);
        }

        DefaultBuild = Installed.FirstOrDefault(r => r.IsDefault);
        TotalRunning = Installed.Sum(r => r.RunningCount);
        LinkInstalledCopies();
    }

    /// <summary>Recomputes <see cref="TotalRunning"/> and <see cref="DefaultBuild"/> after a design-time change.</summary>
    internal void RefreshSampleTotals()
    {
        TotalRunning = Installed.Sum(r => r.RunningCount);
        DefaultBuild = Installed.FirstOrDefault(r => r.IsDefault);
    }
}
