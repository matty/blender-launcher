using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Platform.Windows.Tray;

namespace BlenderLauncher.App.Lifetime;

/// <summary>What the tray asks the app to do; implemented by <see cref="AppController"/>.</summary>
internal interface ITrayHost
{
    void ShowWindow();

    void ShowFavorites();

    void HideToTray();

    Task QuitAsync();

    void Launch(InstalledBuildRowViewModel row);

    void LaunchDefault();
}

/// <summary>
/// The tray icon, through the launcher's own <see cref="ShellTrayIcon"/> (spike S6): shown while the "minimise
/// to tray" setting is on, and created or removed live when it changes. Tooltip "Blender Launcher". Menu, top to bottom:
/// "Launch" the default build, the favourite builds, a separator, Favorites (the window on the Favorites view), Show,
/// Hide, Quit. Left click shows the window, middle click launches the default build, a balloon click shows the window.
/// The icon's events arrive on its own thread and are posted to the UI thread here.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private const int FavoritesId = 1;
    private const int ShowId = 2;
    private const int HideId = 3;
    private const int QuitId = 4;
    private const int FirstBuildId = 100;

    private readonly ITrayHost _host;
    private readonly Log? _log;
    private readonly Dictionary<int, InstalledBuildRowViewModel> _menuBuilds = [];
    private readonly HashSet<InstalledBuildRowViewModel> _watched = [];
    private BuildCatalog? _catalog;
    private ShellTrayIcon? _icon;
    private bool _menuQueued;

    public TrayController(ITrayHost host, Log? log)
    {
        _host = host;
        _log = log;
    }

    /// <summary>The icon is in the notification area, so a hidden window can be brought back.</summary>
    public bool IsActive => _icon is { IsAdded: true };

    /// <summary>Shows or removes the icon (the "minimise to tray" setting). Failures are logged; the icon stays off.</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled == (_icon is not null))
        {
            return;
        }

        if (!enabled)
        {
            RemoveIcon();
            return;
        }

        try
        {
            var icon = ShellTrayIcon.Create(AppIdentity.DisplayName);
            icon.Clicked += (_, e) => UiDispatcher.Post(() => OnClicked(e.Click));
            icon.MenuItemInvoked += (_, id) => UiDispatcher.Post(() => OnMenuItem(id));
            icon.BalloonClicked += (_, _) => UiDispatcher.Post(_host.ShowWindow);
            _icon = icon;
            _log?.Info($"Tray icon added (icon: {icon.IconSource})");
            RebuildMenu();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log?.Error("The tray icon could not be created", e);
            _icon = null;
        }
    }

    /// <summary>Follows the catalog: the default build and favourites become menu entries.</summary>
    public void Attach(BuildCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        catalog.PropertyChanged += OnCatalogPropertyChanged;
        catalog.Installed.CollectionChanged += OnInstalledChanged;
        foreach (var row in catalog.Installed)
        {
            Watch(row);
        }

        QueueMenu();
    }

    /// <summary>A notification balloon; false when there is no icon or Explorer refused it.</summary>
    public Task<bool> ShowBalloonAsync(string text, TrayBalloonIcon icon, bool silent = false) =>
        _icon is { } tray ? tray.ShowBalloonAsync(AppIdentity.DisplayName, text, icon, silent) : Task.FromResult(false);

    public void Dispose()
    {
        if (_catalog is { } catalog)
        {
            catalog.PropertyChanged -= OnCatalogPropertyChanged;
            catalog.Installed.CollectionChanged -= OnInstalledChanged;
        }

        foreach (var row in _watched)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        _watched.Clear();
        RemoveIcon();
    }

    /// <summary>The menu entries for the current catalog (also used by the tests).</summary>
    internal static IReadOnlyList<(InstalledBuildRowViewModel Row, string Text)> BuildEntries(BuildCatalog? catalog)
    {
        var builds = new List<(InstalledBuildRowViewModel, string)>();
        if (catalog is null)
        {
            return builds;
        }

        if (catalog.DefaultBuild is { IsRecognised: true } main)
        {
            builds.Add((main, Loc.Tr("ui.tray.launch_default", ("name", main.Name), ("version", main.Version))));
        }

        foreach (var row in catalog.Installed.Where(r => r is { IsFavourite: true, IsRecognised: true } && !ReferenceEquals(r, catalog.DefaultBuild))
                     .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Version, StringComparer.Ordinal))
        {
            builds.Add((row, Loc.Tr("ui.tray.build", ("name", row.Name), ("version", row.Version))));
        }

        return builds;
    }

    private void RemoveIcon()
    {
        if (_icon is { } icon)
        {
            _icon = null;
            icon.Dispose();
            _log?.Info("Tray icon removed");
        }
    }

    private void OnClicked(TrayClick click)
    {
        switch (click)
        {
            case TrayClick.Left:
                _host.ShowWindow();
                break;
            case TrayClick.Middle:
                _host.LaunchDefault();
                break;
        }
    }

    private void OnMenuItem(int id)
    {
        switch (id)
        {
            case FavoritesId:
                _host.ShowFavorites();
                break;
            case ShowId:
                _host.ShowWindow();
                break;
            case HideId:
                _host.HideToTray();
                break;
            case QuitId:
                _ = _host.QuitAsync();
                break;
            default:
                if (_menuBuilds.TryGetValue(id, out var row))
                {
                    _host.Launch(row);
                }

                break;
        }
    }

    private void QueueMenu()
    {
        if (_menuQueued)
        {
            return;
        }

        _menuQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _menuQueued = false;
            RebuildMenu();
        }, DispatcherPriority.Background);
    }

    private void RebuildMenu()
    {
        if (_icon is not { } icon)
        {
            return;
        }

        _menuBuilds.Clear();
        var items = new List<TrayMenuItem>();
        var id = FirstBuildId;
        foreach (var (row, text) in BuildEntries(_catalog))
        {
            _menuBuilds[id] = row;
            items.Add(new TrayMenuItem(id, text) { IsDefault = id == FirstBuildId && ReferenceEquals(row, _catalog?.DefaultBuild) });
            id++;
        }

        if (items.Count > 0)
        {
            items.Add(TrayMenuItem.Separator);
        }

        items.Add(new TrayMenuItem(FavoritesId, Loc.Tr("ui.tray.favorites")));
        items.Add(new TrayMenuItem(ShowId, Loc.Tr("ui.tray.show")));
        items.Add(new TrayMenuItem(HideId, Loc.Tr("ui.tray.hide")));
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(QuitId, Loc.Tr("ui.tray.quit")));
        icon.SetMenu(items);
        _log?.Debug("Tray menu: " + string.Join(" | ", items.Select(i => i.IsSeparator ? "—" : i.Text)));
    }

    private void OnCatalogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BuildCatalog.DefaultBuild))
        {
            QueueMenu();
        }
    }

    private void OnInstalledChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset && _catalog is { } catalog)
        {
            foreach (var row in _watched)
            {
                row.PropertyChanged -= OnRowChanged;
            }

            _watched.Clear();
            foreach (var row in catalog.Installed)
            {
                Watch(row);
            }
        }
        else
        {
            foreach (var row in e.OldItems?.OfType<InstalledBuildRowViewModel>() ?? [])
            {
                if (_watched.Remove(row))
                {
                    row.PropertyChanged -= OnRowChanged;
                }
            }

            foreach (var row in e.NewItems?.OfType<InstalledBuildRowViewModel>() ?? [])
            {
                Watch(row);
            }
        }

        QueueMenu();
    }

    private void Watch(InstalledBuildRowViewModel row)
    {
        if (_watched.Add(row))
        {
            row.PropertyChanged += OnRowChanged;
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstalledBuildRowViewModel.IsFavourite) or nameof(BuildRowViewModel.Name)
            or nameof(BuildRowViewModel.Version) or nameof(InstalledBuildRowViewModel.Kind))
        {
            QueueMenu();
        }
    }
}
