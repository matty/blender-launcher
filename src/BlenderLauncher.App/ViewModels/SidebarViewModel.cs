using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.Theme;
using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.App.ViewModels.Sidebar;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Settings;
using CommunityToolkit.Mvvm.Input;

namespace BlenderLauncher.App.ViewModels;

/// <summary>
/// The sidebar region. View: <c>Views/Sidebar/SidebarPanel.axaml</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Library group: "All builds", "Favorites", one entry per shown channel (settings <c>builds.show*</c>; Stable and
/// LTS separate; Patch only while patch builds are installed, as they share Experimental's folder), "Custom"; each
/// with its colour square and a count of <see cref="BuildCatalog.Installed"/> rows.</item>
/// <item>Download group: one entry per shown and scraped channel with the "N new" badge (rows with <c>IsNew</c> and not
/// <c>IsInstalled</c>).</item>
/// <item>The active entry follows <see cref="MainWindowViewModel.CurrentView"/>; <see cref="SelectCommand"/> navigates.</item>
/// <item>Disk card: <see cref="Disk"/>.</item>
/// <item>Settings entry at the foot: <see cref="OpenSettingsCommand"/> raises <see cref="SettingsRequested"/> (the app
/// controller opens the Settings window); with no handler it shows a toast.</item>
/// <item>Recounts on catalog collection and row changes; rebuilds the entries on settings changes.</item>
/// </list>
/// </remarks>
public sealed partial class SidebarViewModel : ViewModelBase
{
    /// <summary>Library channels in sidebar order (Custom is always shown).</summary>
    private static readonly Channel[] LibraryChannels =
    [
        Channel.Stable, Channel.Lts, Channel.Daily, Channel.Experimental, Channel.Patch, Channel.Bforartists,
        Channel.UpbgeStable, Channel.UpbgeWeekly, Channel.Custom,
    ];

    private readonly HashSet<InstalledBuildRowViewModel> _installedRows = [];
    private readonly HashSet<AvailableBuildRowViewModel> _availableRows = [];
    private int _lastInstalledCount = -1;

    public SidebarViewModel(MainWindowViewModel shell, Func<string, VolumeInfo?>? volumeProbe = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        Shell = shell;
        Disk = new DiskUsageViewModel(volumeProbe);

        var catalog = shell.Catalog;
        catalog.Installed.CollectionChanged += OnInstalledChanged;
        catalog.Available.CollectionChanged += OnAvailableChanged;
        catalog.PropertyChanged += OnCatalogPropertyChanged;
        shell.PropertyChanged += OnShellPropertyChanged;
        if (shell.Settings is { } settings)
        {
            settings.Changed += UiDispatcher.Marshal<SettingsChangedEventArgs>(OnSettingsChanged);
        }

        TrackAll();
        RebuildItems(shell.Settings?.Current.Builds ?? new BuildsSettings());
        Disk.LibraryRoot = catalog.LibraryRoot;
        _ = Disk.RefreshVolumeAsync();
    }

    public MainWindowViewModel Shell { get; }

    /// <summary>The Library group.</summary>
    public ObservableCollection<SidebarItemViewModel> LibraryItems { get; } = [];

    /// <summary>The Download group.</summary>
    public ObservableCollection<SidebarItemViewModel> DownloadItems { get; } = [];

    /// <summary>The Download group has entries (every download channel can be turned off).</summary>
    public bool HasDownloadItems => DownloadItems.Count > 0;

    /// <summary>The disk-usage card.</summary>
    public DiskUsageViewModel Disk { get; }

    /// <summary>The Settings entry was chosen; the app controller opens the Settings window.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Shows <paramref name="item"/>'s view.</summary>
    [RelayCommand]
    private void Select(SidebarItemViewModel? item)
    {
        if (item is not null)
        {
            Shell.Navigate(item.View);
        }
    }

    /// <summary>The Settings entry.</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        if (SettingsRequested is { } handler)
        {
            handler(this, EventArgs.Empty);
        }
        else
        {
            Shell.ShowToast(Loc.Tr("ui.statusbar.settings_later"));
        }
    }

    /// <summary>Whether <paramref name="channel"/> has a Library entry under <paramref name="builds"/>.</summary>
    public static bool IsShown(Channel channel, BuildsSettings builds)
    {
        ArgumentNullException.ThrowIfNull(builds);
        return channel switch
        {
            Channel.Stable or Channel.Lts => builds.ShowStable,
            Channel.Daily => builds.ShowDaily,
            Channel.Experimental or Channel.Patch => builds.ShowExperimentalAndPatch,
            Channel.Bforartists => builds.ShowBforartists,
            Channel.UpbgeStable => builds.ShowUpbge,
            Channel.UpbgeWeekly => builds.ShowUpbgeWeekly,
            _ => true,
        };
    }

    /// <summary>
    /// Library entries listed only while they hold builds (or are the current view): Patch, which shares
    /// Experimental's folder, and the forks and Custom, which most libraries never use. This keeps the sidebar short
    /// enough for the default window height.
    /// </summary>
    public static bool HiddenWhenEmpty(Channel? channel) =>
        channel is Channel.Patch or Channel.Bforartists or Channel.UpbgeStable or Channel.UpbgeWeekly or Channel.Custom;

    /// <summary>Whether <paramref name="channel"/> has a Download entry: shown and scraped.</summary>
    public static bool IsScraped(Channel channel, BuildsSettings builds)
    {
        ArgumentNullException.ThrowIfNull(builds);
        var scraped = channel switch
        {
            Channel.Stable or Channel.Lts => builds.ScrapeStable,
            Channel.Daily => builds.ScrapeDaily,
            Channel.Experimental or Channel.Patch => builds.ScrapeExperimental,
            Channel.Bforartists => builds.ScrapeBforartists,
            Channel.UpbgeStable => builds.ScrapeUpbge,
            Channel.UpbgeWeekly => builds.ScrapeUpbgeWeekly,
            _ => false,
        };
        return scraped && IsShown(channel, builds);
    }

    // ------------------------------------------------------------------ entries

    private void RebuildItems(BuildsSettings builds)
    {
        LibraryItems.Clear();
        DownloadItems.Clear();

        LibraryItems.Add(new SidebarItemViewModel(new SidebarView(SidebarSection.Installed, SidebarView.AllKey),
            Loc.Tr("ui.sidebar.all_builds"), ChannelPalette.AllDot, null));
        LibraryItems.Add(new SidebarItemViewModel(new SidebarView(SidebarSection.Installed, SidebarView.FavoritesKey),
            Loc.Tr("ui.sidebar.favorites"), ChannelPalette.FavoritesDot, null));
        foreach (var channel in LibraryChannels.Where(c => IsShown(c, builds)))
        {
            LibraryItems.Add(Entry(SidebarSection.Installed, channel));
        }

        foreach (var channel in LibraryChannels.Where(c => IsScraped(c, builds)))
        {
            DownloadItems.Add(Entry(SidebarSection.Download, channel));
        }

        OnPropertyChanged(nameof(HasDownloadItems));
        UpdateActive();
        Recount();
    }

    private static SidebarItemViewModel Entry(SidebarSection section, Channel channel) =>
        new(new SidebarView(section, DisplayFormat.ChannelKey(channel)), DisplayFormat.ChannelName(channel),
            (IBrush)ChannelPalette.For(channel).Foreground, channel);

    private void UpdateActive()
    {
        var current = Shell.CurrentView;
        foreach (var item in LibraryItems.Concat(DownloadItems))
        {
            item.IsActive = item.View == current;
        }
    }

    // ------------------------------------------------------------------ counts

    /// <summary>Recomputes every count, badge and the disk total in one pass over the catalog.</summary>
    private void Recount()
    {
        var catalog = Shell.Catalog;
        var installed = new Dictionary<Channel, int>();
        var favourites = 0;
        long used = 0;
        foreach (var row in catalog.Installed)
        {
            installed[row.Channel] = installed.GetValueOrDefault(row.Channel) + 1;
            if (row.IsFavourite)
            {
                favourites++;
            }

            used += Math.Max(0, row.SizeBytes ?? 0);
        }

        var fresh = new Dictionary<Channel, int>();
        foreach (var row in catalog.Available)
        {
            if (row.IsNew && !row.IsInstalled)
            {
                fresh[row.Channel] = fresh.GetValueOrDefault(row.Channel) + 1;
            }
        }

        foreach (var item in LibraryItems)
        {
            item.Count = item.Channel is { } channel
                ? installed.GetValueOrDefault(channel)
                : item.View.Key == SidebarView.FavoritesKey ? favourites : catalog.Installed.Count;
            item.IsShown = !HiddenWhenEmpty(item.Channel) || item.Count > 0 || item.IsActive;
        }

        foreach (var item in DownloadItems)
        {
            item.NewCount = item.Channel is { } channel ? fresh.GetValueOrDefault(channel) : 0;
        }

        Disk.UsedBytes = used;
        if (catalog.Installed.Count != _lastInstalledCount)
        {
            // Installs and removals change the free space too.
            _lastInstalledCount = catalog.Installed.Count;
            _ = Disk.RefreshVolumeAsync();
        }
    }

    // ------------------------------------------------------------------ events

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentView))
        {
            UpdateActive();
            Recount();
        }
    }

    private void OnCatalogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BuildCatalog.LibraryRoot))
        {
            Disk.LibraryRoot = Shell.Catalog.LibraryRoot;
            _ = Disk.RefreshVolumeAsync();
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Builds != e.Current.Builds)
        {
            RebuildItems(e.Current.Builds);
        }
    }

    private void OnInstalledChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Retrack(e, _installedRows, OnInstalledRowChanged);
        Recount();
    }

    private void OnAvailableChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Retrack(e, _availableRows, OnAvailableRowChanged);
        Recount();
    }

    private void OnInstalledRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstalledBuildRowViewModel.IsFavourite) or nameof(BuildRowViewModel.Channel)
            or nameof(BuildRowViewModel.SizeBytes))
        {
            Recount();
        }
    }

    private void OnAvailableRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BuildRowViewModel.IsNew) or nameof(AvailableBuildRowViewModel.InstalledRow)
            or nameof(BuildRowViewModel.Channel))
        {
            Recount();
        }
    }

    private void TrackAll()
    {
        foreach (var row in Shell.Catalog.Installed)
        {
            if (_installedRows.Add(row))
            {
                row.PropertyChanged += OnInstalledRowChanged;
            }
        }

        foreach (var row in Shell.Catalog.Available)
        {
            if (_availableRows.Add(row))
            {
                row.PropertyChanged += OnAvailableRowChanged;
            }
        }
    }

    private void Retrack<T>(NotifyCollectionChangedEventArgs e, HashSet<T> tracked, PropertyChangedEventHandler handler)
        where T : BuildRowViewModel
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var row in tracked)
            {
                row.PropertyChanged -= handler;
            }

            tracked.Clear();
            TrackAll();
            return;
        }

        if (e.OldItems is not null)
        {
            foreach (var row in e.OldItems.OfType<T>())
            {
                if (tracked.Remove(row))
                {
                    row.PropertyChanged -= handler;
                }
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var row in e.NewItems.OfType<T>())
            {
                if (tracked.Add(row))
                {
                    row.PropertyChanged += handler;
                }
            }
        }
    }
}
