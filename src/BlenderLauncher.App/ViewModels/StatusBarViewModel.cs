using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlenderLauncher.App.ViewModels;

/// <summary>
/// The status bar region. View: <c>Views/StatusBar/StatusBarPanel.axaml</c>. 26 px, 11 px text, gap 18:
/// "Default" and the default build; the running count (hidden at 0); download progress (one: its name, several: a
/// count; a 90 px bar with the average progress); then, on the right, the toast (three seconds) or else the check
/// status, the "Update to X" button and the version.
/// </summary>
/// <remarks>
/// State only: "Check for builds" lives in the list header (<see cref="MainWindowViewModel.CheckForBuildsCommand"/>).
/// The status text is upstream's: "Reading local builds" while the library loads, "Checking for new builds" during a
/// check, "Error: connection failed at HH:MM" when a check reached no source, otherwise "Last check at HH:MM".
/// </remarks>
public sealed partial class StatusBarViewModel : ViewModelBase
{
    private readonly HashSet<AvailableBuildRowViewModel> _rows = [];
    private InstalledBuildRowViewModel? _default;

    public StatusBarViewModel(MainWindowViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        Shell = shell;
        shell.PropertyChanged += OnShellPropertyChanged;
        shell.Toasts.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Services.ToastService.HasToast))
            {
                OnPropertyChanged(nameof(ShowStatus));
            }
        };
        shell.Catalog.PropertyChanged += OnCatalogPropertyChanged;
        shell.Catalog.Available.CollectionChanged += OnAvailableChanged;
        foreach (var row in shell.Catalog.Available)
        {
            Track(row);
        }

        WatchDefault(shell.Catalog.DefaultBuild);
        RefreshDownloads();
        RefreshStatus();
    }

    public MainWindowViewModel Shell { get; }

    /// <summary>The repository the version label opens; <see cref="AppIdentity.UpdateRepo"/> (settable for tests).</summary>
    internal static string? UpdateRepo { get; set; } = AppIdentity.UpdateRepo;

    // ------------------------------------------------------------------ default and running

    /// <summary>"Blender 5.2 LTS (5.2.2)", or "None".</summary>
    public string DefaultText => _default is { } row
        ? Loc.Tr("ui.status.default_value", ("name", row.Name), ("version", row.Version))
        : Loc.Tr("ui.status.default_none");

    public bool HasRunning => Shell.Catalog.TotalRunning > 0;

    /// <summary>"1 instance running", "3 instances running".</summary>
    public string RunningText => Loc.Plural("ui.status.instances_running", Shell.Catalog.TotalRunning);

    // ------------------------------------------------------------------ downloads

    [ObservableProperty]
    public partial bool HasDownloads { get; private set; }

    /// <summary>"Downloading Blender 5.4 Alpha" or "Downloading 3 builds".</summary>
    [ObservableProperty]
    public partial string DownloadText { get; private set; } = "";

    /// <summary>The average progress of the running downloads, 0 to 1.</summary>
    [ObservableProperty]
    public partial double DownloadProgress { get; private set; }

    /// <summary>True while no download knows its size.</summary>
    [ObservableProperty]
    public partial bool IsDownloadIndeterminate { get; private set; }

    /// <summary>"42%" (blank while indeterminate).</summary>
    [ObservableProperty]
    public partial string DownloadPercentText { get; private set; } = "";

    // ------------------------------------------------------------------ status, update, version

    /// <summary>The check status; empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus), nameof(ShowStatus))]
    public partial string StatusText { get; private set; } = "";

    public bool HasStatus => StatusText.Length > 0;

    /// <summary>The status is shown when there is one and no toast covers it (they share the slot).</summary>
    public bool ShowStatus => HasStatus && !Shell.Toasts.HasToast;

    /// <summary>A newer launcher release, when the last check found one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateText))]
    public partial AppRelease? AvailableUpdate { get; private set; }

    public bool HasUpdate => AvailableUpdate is not null;

    /// <summary>"Update to 1.2.0".</summary>
    public string UpdateText => AvailableUpdate is { } release
        ? Loc.Tr("ui.status.update_to", ("version", release.Version.ToString()))
        : "";

    /// <summary>"v0.1.0".</summary>
    public string VersionText => Shell.VersionText;

    /// <summary>The version label opens the release page (only when an update repository is configured).</summary>
    public bool CanOpenReleasePage => ReleasePageUrl is not null;

    /// <summary>The releases page of <see cref="UpdateRepo"/>, or null.</summary>
    public static string? ReleasePageUrl => string.IsNullOrEmpty(UpdateRepo) ? null : $"https://github.com/{UpdateRepo}/releases";

    /// <summary>The "Update to X" button was pressed; the app controller opens the update dialog.</summary>
    public event EventHandler<AppRelease>? UpdateRequested;

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (ReleasePageUrl is not { } url)
        {
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            Shell.Services?.Log.Warning($"Could not open {url}", e);
        }
    }

    [RelayCommand]
    private void Update()
    {
        if (AvailableUpdate is not { } release)
        {
            return;
        }

        if (UpdateRequested is { } handler)
        {
            handler(this, release);
        }
        else
        {
            OpenReleasePage();
        }
    }

    // ------------------------------------------------------------------ computing

    /// <summary>Recomputes <see cref="StatusText"/> and <see cref="AvailableUpdate"/> from the shell and catalog.</summary>
    private void RefreshStatus()
    {
        var catalog = Shell.Catalog;
        StatusText = StatusFor(
            isChecking: Shell.IsChecking,
            isReading: catalog.IsScanning || (Shell.Services is null && !catalog.IsLive && catalog.Installed.Count == 0 && catalog.LastChecked is null),
            lastCheck: catalog.LastCheck,
            lastChecked: catalog.LastChecked);
        AvailableUpdate = catalog.LastCheck?.LauncherUpdate is { Status: UpdateCheckStatus.UpdateAvailable, Latest: { } latest }
            ? latest
            : null;
    }

    /// <summary>Upstream's status-bar text for the given state (see the class remarks).</summary>
    internal static string StatusFor(bool isChecking, bool isReading, ScrapeSnapshot? lastCheck, DateTimeOffset? lastChecked)
    {
        if (isChecking)
        {
            return Loc.Tr("act.prog.checking");
        }

        if (isReading)
        {
            return Loc.Tr("act.prog.reading_local");
        }

        if (lastCheck is { Sources.Count: > 0 } check && check.Sources.All(s => !s.Succeeded)
            && check.Errors.FirstOrDefault(e => e.MessageKey == ScrapeMessageKeys.ConnectionFailed) is { } failure)
        {
            var time = failure.MessageArgs.FirstOrDefault(a => a.Key == "time").Value
                       ?? check.CompletedAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
            return Loc.Tr(ScrapeMessageKeys.ConnectionFailed, ("time", time));
        }

        return lastChecked is { } at && at != DateTimeOffset.MinValue
            ? Loc.Tr("act.prog.last_check", ("time", at.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)))
            : "";
    }

    /// <summary>Recomputes the download summary from the rows with a job queued or running.</summary>
    private void RefreshDownloads()
    {
        var busy = Shell.Catalog.Busy.ToList();
        HasDownloads = busy.Count > 0;
        if (busy.Count == 0)
        {
            DownloadText = "";
            DownloadProgress = 0;
            DownloadPercentText = "";
            IsDownloadIndeterminate = false;
            return;
        }

        DownloadText = busy.Count == 1
            ? Loc.Tr("ui.status.downloading_one", ("name", busy[0].Name))
            : Loc.Tr("ui.status.downloading_many", ("count", busy.Count));
        IsDownloadIndeterminate = busy.All(r => r.IsIndeterminate);
        DownloadProgress = busy.Average(r => r.IsIndeterminate ? 0 : Math.Clamp(r.Progress, 0, 1));
        DownloadPercentText = IsDownloadIndeterminate
            ? ""
            : (DownloadProgress * 100).ToString("0", CultureInfo.CurrentCulture) + "%";
    }

    private void WatchDefault(InstalledBuildRowViewModel? row)
    {
        if (_default is not null)
        {
            _default.PropertyChanged -= OnDefaultRowChanged;
        }

        _default = row;
        if (row is not null)
        {
            row.PropertyChanged += OnDefaultRowChanged;
        }

        OnPropertyChanged(nameof(DefaultText));
    }

    // ------------------------------------------------------------------ events

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsChecking) or nameof(MainWindowViewModel.Services))
        {
            RefreshStatus();
        }
    }

    private void OnCatalogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BuildCatalog.DefaultBuild):
                WatchDefault(Shell.Catalog.DefaultBuild);
                break;
            case nameof(BuildCatalog.TotalRunning):
                OnPropertyChanged(nameof(HasRunning));
                OnPropertyChanged(nameof(RunningText));
                break;
            case nameof(BuildCatalog.IsChecking) or nameof(BuildCatalog.IsScanning) or nameof(BuildCatalog.LastCheck)
                or nameof(BuildCatalog.LastChecked):
                RefreshStatus();
                break;
        }
    }

    private void OnDefaultRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BuildRowViewModel.Name) or nameof(BuildRowViewModel.Version))
        {
            OnPropertyChanged(nameof(DefaultText));
        }
    }

    private void OnAvailableChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var row in _rows)
            {
                row.PropertyChanged -= OnRowChanged;
            }

            _rows.Clear();
            foreach (var row in Shell.Catalog.Available)
            {
                Track(row);
            }
        }
        else
        {
            foreach (var row in e.OldItems?.OfType<AvailableBuildRowViewModel>() ?? [])
            {
                if (_rows.Remove(row))
                {
                    row.PropertyChanged -= OnRowChanged;
                }
            }

            foreach (var row in e.NewItems?.OfType<AvailableBuildRowViewModel>() ?? [])
            {
                Track(row);
            }
        }

        RefreshDownloads();
    }

    private void Track(AvailableBuildRowViewModel row)
    {
        if (_rows.Add(row))
        {
            row.PropertyChanged += OnRowChanged;
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AvailableBuildRowViewModel.IsBusy) or nameof(AvailableBuildRowViewModel.JobState)
            or nameof(AvailableBuildRowViewModel.InstalledRow) or nameof(AvailableBuildRowViewModel.Progress)
            or nameof(AvailableBuildRowViewModel.IsIndeterminate))
        {
            RefreshDownloads();
        }
    }
}
