using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Threading;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.ViewModels.Lists;
using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlenderLauncher.App.ViewModels;

/// <summary>
/// The main area: the header (title, subtitle, filter box, "Check for builds") and
/// the installed or download list for <see cref="MainWindowViewModel.CurrentView"/>. View: <c>Views/Lists/BuildListPanel.axaml</c>.
/// Row actions are in <c>BuildListViewModel.Actions.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InstalledRows"/> and <see cref="AvailableRows"/> are the visible rows of the current view: the catalog's
/// rows for the view (channel, Favorites), filtered by <see cref="FilterText"/> and sorted by <see cref="Sort"/>
/// (<see cref="BuildListQuery"/>). They are updated in place (the same row objects, contiguous runs inserted and removed
/// together) so selection and realised containers survive filtering; a re-sort replaces the contents in one go.
/// </para>
/// <para>
/// Catalog changes are coalesced into one refresh per dispatcher turn; typing in the filter refreshes at once.
/// </para>
/// </remarks>
public sealed partial class BuildListViewModel : ViewModelBase
{
    private readonly BuildListQuery _query = new();
    private readonly Dictionary<string, ViewSort> _sessionSorts = new(StringComparer.Ordinal);
    private bool _refreshPosted;
    private bool _syncing;
    private IReadOnlyList<InstalledBuildRowViewModel> _selectedInstalledRows = [];

    // Visible rows before the filter: decides "no match" against "nothing here".
    private int _installedInView;
    private int _availableInView;

    public BuildListViewModel(MainWindowViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        Shell = shell;
        Catalog = shell.Catalog;

        Catalog.Installed.CollectionChanged += OnCatalogCollectionChanged;
        Catalog.Available.CollectionChanged += OnCatalogCollectionChanged;
        foreach (var row in Catalog.Installed)
        {
            row.PropertyChanged += OnRowPropertyChanged;
        }

        foreach (var row in Catalog.Available)
        {
            row.PropertyChanged += OnRowPropertyChanged;
        }

        Catalog.PropertyChanged += OnCatalogPropertyChanged;
        Shell.PropertyChanged += OnShellPropertyChanged;
        Sort = LoadSort(Shell.CurrentView);
        Refresh();
    }

    public MainWindowViewModel Shell { get; }

    public BuildCatalog Catalog { get; }

    /// <summary>The installed list's visible rows (filtered and sorted).</summary>
    public AvaloniaList<InstalledBuildRowViewModel> InstalledRows { get; } = [];

    /// <summary>The download list's visible rows (filtered and sorted).</summary>
    public AvaloniaList<AvailableBuildRowViewModel> AvailableRows { get; } = [];

    /// <summary>The filter box text (upstream's search rule; see <see cref="BuildListQuery"/>). Cleared on navigation.</summary>
    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    /// <summary>The current view's sort column and direction, remembered per view (<see cref="SortViews"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortedByVersion), nameof(IsSortedByBuilt), nameof(IsSortDescending))]
    public partial ViewSort Sort { get; private set; }

    public bool IsSortedByVersion => Sort.Column == SortColumn.Version;

    public bool IsSortedByBuilt => Sort.Column == SortColumn.Built;

    public bool IsSortDescending => Sort.Descending;

    /// <summary>"Installed builds", "Favorites", "Daily builds", "Download · Daily".</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    /// <summary>"6 of 7 builds · 2.85 GB on disk", or the channel's description with "checked 4 min ago".</summary>
    [ObservableProperty]
    public partial string Subtitle { get; private set; } = "";

    /// <summary>What to show instead of rows; <see cref="ListEmptyState.None"/> when the list has rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowBrowseDownloads), nameof(ShowRetry), nameof(IsEmptyBusy),
        nameof(ShowInstalledList), nameof(ShowDownloadList))]
    public partial ListEmptyState EmptyState { get; private set; }

    public bool IsEmpty => EmptyState != ListEmptyState.None;

    /// <summary>The empty state's message.</summary>
    [ObservableProperty]
    public partial string EmptyText { get; private set; } = "";

    /// <summary>"Browse downloads" under "No builds installed."</summary>
    public bool ShowBrowseDownloads => EmptyState == ListEmptyState.NoBuilds;

    /// <summary>"Try again" under a connection or source error.</summary>
    public bool ShowRetry => EmptyState is ListEmptyState.Offline or ListEmptyState.SourceError;

    /// <summary>The spinner beside "Checking for builds…".</summary>
    public bool IsEmptyBusy => EmptyState == ListEmptyState.Checking;

    public bool ShowInstalledList => Shell.IsInstalledView && !IsEmpty;

    public bool ShowDownloadList => Shell.IsDownloadView && !IsEmpty;

    /// <summary>A source error for the current download channel while it still has rows (the banner above the list).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorBanner))]
    public partial string? ErrorBannerText { get; private set; }

    public bool HasErrorBanner => ErrorBannerText is not null;

    /// <summary>
    /// The installed list's selection (two-way with <see cref="MainWindowViewModel.SelectedInstalled"/>). While the list
    /// is being updated a row filtered out of view does not clear the shell's selection.
    /// </summary>
    public InstalledBuildRowViewModel? SelectedInstalledItem
    {
        get => Shell.SelectedInstalled;
        set
        {
            // A list only clears a selection it shows: ignore the null it writes while being updated, or while the
            // shell's selection is filtered out of view (or not loaded into it yet).
            if (value is null && (_syncing || (Shell.SelectedInstalled is { } current && !InstalledRows.Contains(current))))
            {
                return;
            }

            Shell.SelectedInstalled = value;
        }
    }

    /// <summary>The download list's selection (two-way with <see cref="MainWindowViewModel.SelectedAvailable"/>).</summary>
    public AvailableBuildRowViewModel? SelectedAvailableItem
    {
        get => Shell.SelectedAvailable;
        set
        {
            if (value is null && (_syncing || (Shell.SelectedAvailable is { } current && !AvailableRows.Contains(current))))
            {
                return;
            }

            Shell.SelectedAvailable = value;
        }
    }

    /// <summary>Every selected installed row (Ctrl/Shift multi-select), for delete and the multi-selection menu.</summary>
    public IReadOnlyList<InstalledBuildRowViewModel> SelectedInstalledRows => _selectedInstalledRows;

    /// <summary>Called by the view when the installed list's selection changes.</summary>
    public void SetSelectedInstalledRows(IEnumerable<InstalledBuildRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (_syncing)
        {
            return;
        }

        _selectedInstalledRows = [.. rows];
        OnPropertyChanged(nameof(SelectedInstalledRows));
    }

    /// <summary>Recomputes the visible rows, the header and the empty state now.</summary>
    public void Refresh()
    {
        _refreshPosted = false;
        var view = Shell.CurrentView;

        var installed = new List<InstalledBuildRowViewModel>();
        if (view.Section == SidebarSection.Installed)
        {
            foreach (var row in Catalog.Installed)
            {
                if (BuildListQuery.InInstalledView(row, view.Key))
                {
                    installed.Add(row);
                }
            }
        }

        var available = new List<AvailableBuildRowViewModel>();
        if (view.Section == SidebarSection.Download)
        {
            var channel = Shell.CurrentChannel;
            foreach (var row in Catalog.Available)
            {
                if (BuildListQuery.InDownloadView(row, channel))
                {
                    available.Add(row);
                }
            }
        }

        _installedInView = installed.Count;
        _availableInView = available.Count;
        installed = _query.Filter(installed, FilterText);
        available = _query.Filter(available, FilterText);
        BuildListQuery.Sort(installed, Sort);
        BuildListQuery.Sort(available, Sort);

        _syncing = true;
        try
        {
            Sync(InstalledRows, installed);
            Sync(AvailableRows, available);
        }
        finally
        {
            _syncing = false;
        }

        // Re-assert the shell's selection on the lists (a row filtered out and back in is selected again).
        OnPropertyChanged(nameof(SelectedInstalledItem));
        OnPropertyChanged(nameof(SelectedAvailableItem));
        UpdateHeader();
        UpdateEmptyState();
    }

    /// <summary>Re-reads the time-relative subtitle ("checked 4 min ago"); the view calls it every half minute.</summary>
    public void RefreshRelativeTimes()
    {
        if (Shell.IsDownloadView)
        {
            UpdateHeader();
        }
    }

    partial void OnFilterTextChanged(string value) => Refresh();

    // ------------------------------------------------------------------ header and empty state

    private void UpdateHeader()
    {
        var view = Shell.CurrentView;
        if (view.Section == SidebarSection.Installed)
        {
            Title = view.Key switch
            {
                SidebarView.AllKey => Loc.Tr("ui.list.title.installed"),
                SidebarView.FavoritesKey => Loc.Tr("ui.list.title.favorites"),
                _ => Loc.Tr("ui.list.title.channel", ("channel", ChannelNameOf(view.Key))),
            };

            long bytes = 0;
            foreach (var row in Catalog.Installed)
            {
                bytes += row.SizeBytes ?? 0;
            }

            var total = Catalog.Installed.Count;
            var size = DisplayFormat.SizeGigabytes(bytes);
            Subtitle = total == 0 && Shell.Services is not null && Catalog.LibraryRoot is null ? Loc.Tr("ui.list.subtitle.no_library")
                : total == 0 ? Loc.Tr("ui.list.subtitle.empty")
                : Loc.Tr("ui.list.subtitle.installed", ("shown", InstalledRows.Count), ("total", total), ("size", size));
            return;
        }

        Title = Loc.Tr("ui.list.title.download", ("channel", ChannelNameOf(view.Key)));
        var description = Loc.Tr("ui.list.download_subtitle." + view.Key);
        var status = Shell.IsChecking ? Loc.Tr("ui.list.subtitle.checking")
            : Catalog.LastChecked is { } checkedAt ? Loc.Tr("ui.list.subtitle.checked", ("time", DisplayFormat.Ago(checkedAt)))
            : null;
        Subtitle = status is null ? description : description + " · " + status;
    }

    private void UpdateEmptyState()
    {
        ErrorBannerText = null;
        var view = Shell.CurrentView;
        var filtered = FilterText.Trim().Length > 0;
        if (view.Section == SidebarSection.Installed)
        {
            EmptyState = InstalledRows.Count > 0 ? ListEmptyState.None
                : Catalog.IsLive && Catalog.LibraryRoot is null ? ListEmptyState.NoLibrary
                : filtered && _installedInView > 0 ? ListEmptyState.NoMatch
                : view.Key == SidebarView.FavoritesKey ? ListEmptyState.NoFavourites
                : ListEmptyState.NoBuilds;
        }
        else
        {
            var error = SourceErrorFor(Shell.CurrentChannel);
            if (AvailableRows.Count > 0)
            {
                EmptyState = ListEmptyState.None;
                ErrorBannerText = error is null ? null : ScrapeErrorText.Format(Loc.Translator, error);
            }
            else
            {
                EmptyState = filtered && _availableInView > 0 ? ListEmptyState.NoMatch
                    : Shell.IsChecking || (Catalog.IsLive && Catalog.LastCheck is null) ? ListEmptyState.Checking
                    : error?.MessageKey == ScrapeMessageKeys.ConnectionFailed ? ListEmptyState.Offline
                    : error is not null ? ListEmptyState.SourceError
                    : ListEmptyState.NoneAvailable;
            }

            if (EmptyState == ListEmptyState.NoMatch && error is not null)
            {
                ErrorBannerText = ScrapeErrorText.Format(Loc.Translator, error);
            }
        }

        var query = FilterText.Trim();
        EmptyText = EmptyState switch
        {
            ListEmptyState.NoLibrary => Loc.Tr("ui.list.empty.no_library"),
            ListEmptyState.NoBuilds => DisplayFormat.ChannelFromKey(view.Key) is not null
                ? Loc.Tr("ui.list.empty.no_channel_installed", ("channel", ChannelNameOf(view.Key)))
                : Loc.Tr("ui.list.empty.no_builds_installed"),
            ListEmptyState.NoFavourites => Loc.Tr("ui.list.empty.no_favorites"),
            ListEmptyState.NoMatch => view.Section == SidebarSection.Installed
                ? Loc.Tr("ui.list.empty.no_match_installed", ("query", query))
                : Loc.Tr("ui.list.empty.no_match", ("query", query)),
            ListEmptyState.Checking => Loc.Tr("ui.list.empty.checking"),
            ListEmptyState.Offline => Loc.Tr("ui.list.empty.offline"),
            ListEmptyState.SourceError => ScrapeErrorText.Format(Loc.Translator, SourceErrorFor(Shell.CurrentChannel)!),
            ListEmptyState.NoneAvailable => Loc.Tr("ui.list.empty.none_available", ("channel", ChannelNameOf(view.Key))),
            _ => "",
        };
    }

    /// <summary>The last check's error for the source that fills <paramref name="channel"/>, or null.</summary>
    private ScrapeError? SourceErrorFor(Core.Builds.Channel? channel)
    {
        if (channel is not { } c || Catalog.LastCheck is not { } check)
        {
            return null;
        }

        foreach (var source in check.Sources)
        {
            if (source.Error is { } error && source.Channels.Contains(c))
            {
                return error;
            }
        }

        return null;
    }

    private static string ChannelNameOf(string key) =>
        DisplayFormat.ChannelFromKey(key) is { } channel ? DisplayFormat.ChannelName(channel) : key;

    // ------------------------------------------------------------------ sort

    /// <summary>
    /// A column header was clicked: the active column flips direction; another column becomes active, highest or newest
    /// first. Remembered for the current view in <c>internal.viewSorts</c>.
    /// </summary>
    public void ToggleSort(SortColumn column)
    {
        var next = Sort.Column == column
            ? Sort with { Descending = !Sort.Descending }
            : new ViewSort { Column = column, Descending = true };
        var key = BuildListQuery.SortKeyOf(Shell.CurrentView);
        _sessionSorts[key] = next;
        if (Shell.Settings is { } settings)
        {
            settings.Update(s => s with
            {
                Internal = s.Internal with
                {
                    ViewSorts = new Dictionary<string, ViewSort>(s.Internal.ViewSorts, StringComparer.Ordinal) { [key] = next },
                },
            });
        }

        Sort = next;
        Refresh();
    }

    private ViewSort LoadSort(SidebarView view)
    {
        var key = BuildListQuery.SortKeyOf(view);
        return _sessionSorts.TryGetValue(key, out var sort) ? sort
            : Shell.Settings?.Current.Internal.SortFor(key) ?? ViewSort.Default;
    }

    // ------------------------------------------------------------------ change tracking

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.CurrentView):
                Sort = LoadSort(Shell.CurrentView);
                if (FilterText.Length > 0)
                {
                    FilterText = ""; // refreshes
                }
                else
                {
                    Refresh();
                }

                OnPropertyChanged(nameof(ShowInstalledList));
                OnPropertyChanged(nameof(ShowDownloadList));
                break;
            case nameof(MainWindowViewModel.SelectedInstalled):
                OnPropertyChanged(nameof(SelectedInstalledItem));
                break;
            case nameof(MainWindowViewModel.SelectedAvailable):
                OnPropertyChanged(nameof(SelectedAvailableItem));
                break;
            case nameof(MainWindowViewModel.IsChecking):
                UpdateHeader();
                UpdateEmptyState();
                break;
        }
    }

    private void OnCatalogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BuildCatalog.LastCheck) or nameof(BuildCatalog.LastChecked) or nameof(BuildCatalog.LibraryRoot):
                UpdateHeader();
                UpdateEmptyState();
                break;
        }
    }

    private void OnCatalogCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Not raised by the catalog today; resubscribing would need the old items, so refresh only.
            ScheduleRefresh();
            return;
        }

        if (e.OldItems is { } oldItems)
        {
            foreach (BuildRowViewModel row in oldItems)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
            }
        }

        if (e.NewItems is { } newItems)
        {
            foreach (BuildRowViewModel row in newItems)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
                row.PropertyChanged += OnRowPropertyChanged;
            }
        }

        ScheduleRefresh();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // Membership, order or the text the filter reads.
            case nameof(BuildRowViewModel.Build) or nameof(BuildRowViewModel.Channel) or nameof(BuildRowViewModel.BuiltAt)
                or nameof(BuildRowViewModel.Version) or nameof(BuildRowViewModel.Name)
                or nameof(InstalledBuildRowViewModel.IsFavourite) or nameof(InstalledBuildRowViewModel.Kind):
                ScheduleRefresh();
                break;
            case nameof(BuildRowViewModel.SizeBytes) when sender is InstalledBuildRowViewModel && Shell.IsInstalledView:
                UpdateHeader();
                break;
        }
    }

    /// <summary>Coalesces catalog changes into one <see cref="Refresh"/>, before the next frame.</summary>
    private void ScheduleRefresh()
    {
        if (_refreshPosted)
        {
            return;
        }

        _refreshPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (_refreshPosted)
            {
                Refresh();
            }
        }, DispatcherPriority.Normal);
    }

    // ------------------------------------------------------------------ collection sync

    /// <summary>
    /// Makes <paramref name="target"/> equal to <paramref name="desired"/>. When the rows that stay keep their order
    /// (filtering, rows added or removed) only contiguous runs are removed and inserted, so realised containers and
    /// selection survive; when the order changed (a re-sort) the contents are replaced in two notifications.
    /// </summary>
    internal static void Sync<T>(AvaloniaList<T> target, IReadOnlyList<T> desired)
        where T : class
    {
        if (SequenceEqual(target, desired))
        {
            return;
        }

        var wanted = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);

        // Remove runs of rows that left, from the end so indices stay valid.
        for (var i = target.Count - 1; i >= 0;)
        {
            if (wanted.Contains(target[i]))
            {
                i--;
                continue;
            }

            var end = i;
            while (i >= 0 && !wanted.Contains(target[i]))
            {
                i--;
            }

            target.RemoveRange(i + 1, end - i);
        }

        // The rows that stayed must be a subsequence of desired, in order; otherwise replace everything.
        var j = 0;
        foreach (var item in desired)
        {
            if (j < target.Count && ReferenceEquals(target[j], item))
            {
                j++;
            }
        }

        if (j != target.Count)
        {
            target.Clear();
            target.AddRange(desired);
            return;
        }

        // Insert runs of new rows.
        var at = 0;
        for (var d = 0; d < desired.Count;)
        {
            if (at < target.Count && ReferenceEquals(target[at], desired[d]))
            {
                at++;
                d++;
                continue;
            }

            var start = d;
            while (d < desired.Count && !(at < target.Count && ReferenceEquals(target[at], desired[d])))
            {
                d++;
            }

            var run = new T[d - start];
            for (var k = 0; k < run.Length; k++)
            {
                run[k] = desired[start + k];
            }

            target.InsertRange(at, run);
            at += run.Length;
        }
    }

    private static bool SequenceEqual<T>(AvaloniaList<T> a, IReadOnlyList<T> b)
        where T : class
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!ReferenceEquals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }
}
