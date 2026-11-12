using System.ComponentModel;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlenderLauncher.App.ViewModels;

/// <summary>
/// The shell. Owns navigation (which sidebar entry is shown) and selection, holds the four region view models
/// and the shared parts (rows, actions, toast), and is the one place regions talk through. Regions never reference
/// each other; they read and call the shell and listen to its events.
/// </summary>
/// <remarks>
/// <para>Regions (each a ContentControl in <c>MainWindow.axaml</c>, resolved by a typed DataTemplate):</para>
/// <list type="bullet">
/// <item><see cref="Sidebar"/> — <c>ViewModels/SidebarViewModel.cs</c>, <c>Views/Sidebar/</c></item>
/// <item><see cref="BuildList"/> — <c>ViewModels/BuildListViewModel*.cs</c>, <c>Views/Lists/</c> (header, filter, both lists)</item>
/// <item><see cref="Detail"/> — <c>ViewModels/DetailPanelViewModel.cs</c>, <c>Views/Detail/</c>; context menu in <c>ViewModels/BuildContextMenu*.cs</c></item>
/// <item><see cref="StatusBar"/> — <c>ViewModels/StatusBarViewModel.cs</c>, <c>Views/StatusBar/</c></item>
/// </list>
/// <para>Cross-region traffic:</para>
/// <list type="bullet">
/// <item>Navigation: <see cref="Navigate(SidebarView)"/> → <see cref="CurrentView"/> changes and <see cref="ViewChanged"/> fires.</item>
/// <item>Selection: set <see cref="SelectedInstalled"/> / <see cref="SelectedAvailable"/> (or call <see cref="Select"/>);
/// <see cref="SelectedBuild"/> is the one for the current view; <see cref="SelectionChanged"/> fires when it changes.</item>
/// <item>"Installed — show in library": <see cref="ShowInstalledBuild"/>.</item>
/// <item>Toast: <see cref="ShowToast"/> (or <see cref="Toasts"/>).</item>
/// <item>Check for builds (button, F5, Shift = forced): <see cref="CheckForBuildsCommand"/> / <see cref="CheckForBuildsAsync"/>.</item>
/// <item>Services arriving after the first frame: <see cref="ServicesAttached"/>; until then <see cref="Services"/> is null
/// and the catalog is empty (or holds sample data).</item>
/// </list>
/// </remarks>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(SettingsStore? settings = null)
    {
        Settings = settings;
        Catalog = new BuildCatalog();
        Toasts = new ToastService();
        Actions = new BuildActions(Catalog, Toasts);

        var appearance = settings?.Current.Appearance ?? new AppearanceSettings();
        CurrentView = SidebarView.TryParse(appearance.DefaultView, out var view) ? view.Value : SidebarView.Default;
        IsDetailPanelVisible = appearance.ShowDetailsPanel;
        Catalog.PropertyChanged += OnCatalogPropertyChanged;

        Sidebar = new SidebarViewModel(this);
        BuildList = new BuildListViewModel(this);
        Detail = new DetailPanelViewModel(this);
        StatusBar = new StatusBarViewModel(this);
    }

    /// <summary>The settings store; null in previews and tests without settings.</summary>
    public SettingsStore? Settings { get; }

    /// <summary>The live services once started (after the first frame), else null.</summary>
    public AppServices? Services { get; private set; }

    /// <summary>Installed and available rows shared by every region.</summary>
    public BuildCatalog Catalog { get; }

    /// <summary>Launch, download, cancel, favourite, default, remove… shared by every region.</summary>
    public BuildActions Actions { get; }

    /// <summary>The status-bar toast.</summary>
    public ToastService Toasts { get; }

    public SidebarViewModel Sidebar { get; }

    public BuildListViewModel BuildList { get; }

    public DetailPanelViewModel Detail { get; }

    public StatusBarViewModel StatusBar { get; }

    /// <summary>The window title.</summary>
    public string Title => AppIdentity.DisplayName;

    /// <summary>"v0.1.0", for the status bar.</summary>
    public string VersionText => "v" + AppIdentity.Version;

    /// <summary>The sidebar entry shown: <c>installed:all|favorites|&lt;channel&gt;</c> or <c>download:&lt;channel&gt;</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalledView), nameof(IsDownloadView), nameof(CurrentChannel), nameof(SelectedBuild))]
    public partial SidebarView CurrentView { get; private set; }

    public bool IsInstalledView => CurrentView.Section == SidebarSection.Installed;

    public bool IsDownloadView => CurrentView.Section == SidebarSection.Download;

    /// <summary>The channel of the current view, or null for "All builds" and "Favorites".</summary>
    public Channel? CurrentChannel => DisplayFormat.ChannelFromKey(CurrentView.Key);

    /// <summary>The selected row in the installed lists (kept while the download view is shown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedBuild))]
    public partial InstalledBuildRowViewModel? SelectedInstalled { get; set; }

    /// <summary>The selected row in the download lists (kept while the installed view is shown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedBuild))]
    public partial AvailableBuildRowViewModel? SelectedAvailable { get; set; }

    /// <summary>The selection of the current view: what the detail panel shows.</summary>
    public BuildRowViewModel? SelectedBuild => IsInstalledView ? SelectedInstalled : SelectedAvailable;

    /// <summary>The detail panel is shown (setting <c>appearance.showDetailsPanel</c>).</summary>
    [ObservableProperty]
    public partial bool IsDetailPanelVisible { get; set; }

    /// <summary>A check for builds is running.</summary>
    public bool IsChecking => Catalog.IsChecking || _sampleChecking;

    private bool _sampleChecking;

    /// <summary>The current view changed (after <see cref="CurrentView"/> is set).</summary>
    public event EventHandler<SidebarView>? ViewChanged;

    /// <summary><see cref="SelectedBuild"/> changed.</summary>
    public event EventHandler<BuildRowViewModel?>? SelectionChanged;

    /// <summary>Live services were attached (<see cref="Services"/> is set and the catalog is loading).</summary>
    public event EventHandler? ServicesAttached;

    /// <summary>Shows a sidebar entry. Selection of each section is kept.</summary>
    public void Navigate(SidebarView view)
    {
        if (view == CurrentView)
        {
            return;
        }

        var before = SelectedBuild;
        CurrentView = view;
        ViewChanged?.Invoke(this, view);
        if (!ReferenceEquals(before, SelectedBuild))
        {
            SelectionChanged?.Invoke(this, SelectedBuild);
        }
    }

    public void Navigate(SidebarSection section, string key) => Navigate(new SidebarView(section, key));

    /// <summary>Selects <paramref name="row"/> in its own section (null clears the current view's selection).</summary>
    public void Select(BuildRowViewModel? row)
    {
        switch (row)
        {
            case InstalledBuildRowViewModel installed:
                SelectedInstalled = installed;
                break;
            case AvailableBuildRowViewModel available:
                SelectedAvailable = available;
                break;
            case null when IsInstalledView:
                SelectedInstalled = null;
                break;
            case null:
                SelectedAvailable = null;
                break;
        }
    }

    /// <summary>"Installed — show in library": switches to all installed builds and selects <paramref name="row"/>.</summary>
    public void ShowInstalledBuild(InstalledBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        SelectedInstalled = row;
        Navigate(new SidebarView(SidebarSection.Installed, SidebarView.AllKey));
    }

    /// <summary>Shows <paramref name="text"/> in the status bar for three seconds.</summary>
    public void ShowToast(string text) => Toasts.Show(text);

    /// <summary>
    /// Checks for new builds (the "Check for builds" button and F5; Shift forces a check of every visible channel).
    /// Toasts the outcome.
    /// </summary>
    [RelayCommand]
    public async Task CheckForBuildsAsync(bool forced)
    {
        if (IsChecking)
        {
            return;
        }

        if (Services is not { } services)
        {
            _sampleChecking = true;
            OnPropertyChanged(nameof(IsChecking));
            await Task.Delay(1200).ConfigureAwait(true);
            _sampleChecking = false;
            OnPropertyChanged(nameof(IsChecking));
            ShowToast(Loc.Tr("ui.toast.up_to_date"));
            return;
        }

        if (services.IsOffline)
        {
            // --offline: no checks this session.
            ShowToast(Loc.Tr("ui.toast.offline"));
            return;
        }

        IsManualCheck = true;
        try
        {
            var snapshot = await Task.Run(() => services.Scrape.CheckAsync(forced)).ConfigureAwait(true);
            ShowToast(snapshot.NewCount > 0
                ? Loc.Plural("ui.toast.new_builds", snapshot.NewCount)
                : Loc.Tr("ui.toast.up_to_date"));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            services.Log.Error("The check for builds failed", e);
            ShowToast(Loc.Tr("ui.toast.check_failed", ("error", e.Message)));
        }
        finally
        {
            // After the check's own completion event, which was posted to the UI thread at a higher priority.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsManualCheck = false, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// A check the user started is running (or its completion is still being delivered). Its outcome is toasted here,
    /// so the notification router leaves it alone.
    /// </summary>
    public bool IsManualCheck { get; private set; }

    /// <summary>Connects live services (called once, on the UI thread, after the first frame).</summary>
    public void AttachServices(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
        Actions.Attach(services);
        Catalog.Attach(services);
        OnPropertyChanged(nameof(Services));
        ServicesAttached?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies the appearance settings that live in the shell (detail panel visibility).</summary>
    public void ApplyAppearance(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        IsDetailPanelVisible = appearance.ShowDetailsPanel;
    }

    partial void OnSelectedInstalledChanged(InstalledBuildRowViewModel? value)
    {
        if (IsInstalledView)
        {
            SelectionChanged?.Invoke(this, value);
        }
    }

    partial void OnSelectedAvailableChanged(AvailableBuildRowViewModel? value)
    {
        if (IsDownloadView)
        {
            SelectionChanged?.Invoke(this, value);
        }
    }

    private void OnCatalogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BuildCatalog.IsChecking))
        {
            OnPropertyChanged(nameof(IsChecking));
        }
    }
}
