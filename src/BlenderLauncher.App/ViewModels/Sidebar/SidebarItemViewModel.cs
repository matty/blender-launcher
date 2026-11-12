using System.Globalization;
using Avalonia.Media;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlenderLauncher.App.ViewModels.Sidebar;

/// <summary>
/// One sidebar entry: "All builds", "Favorites" or a channel, in the Library or the Download group. Library entries
/// show a count of installed builds; download entries show the "N new" badge when there are new builds.
/// </summary>
public sealed partial class SidebarItemViewModel : ViewModelBase
{
    public SidebarItemViewModel(SidebarView view, string label, IBrush dot, Channel? channel)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(dot);
        View = view;
        Label = label;
        Dot = dot;
        Channel = channel;
    }

    /// <summary>The view this entry shows (<see cref="MainWindowViewModel.Navigate(SidebarView)"/>).</summary>
    public SidebarView View { get; }

    /// <summary>"All builds", "Favorites", "Stable", …</summary>
    public string Label { get; }

    /// <summary>The 8 px colour square.</summary>
    public IBrush Dot { get; }

    /// <summary>The channel, or null for "All builds" and "Favorites".</summary>
    public Channel? Channel { get; }

    public bool IsLibrary => View.Section == SidebarSection.Installed;

    public bool IsDownload => View.Section == SidebarSection.Download;

    /// <summary>Installed builds in this entry (library entries only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int Count { get; set; }

    public string CountText => Count.ToString(CultureInfo.CurrentCulture);

    /// <summary>New builds that are not installed (download entries only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNew), nameof(NewText))]
    public partial int NewCount { get; set; }

    public bool HasNew => NewCount > 0;

    /// <summary>"2 new".</summary>
    public string NewText => Loc.Tr("ui.sidebar.new_count", ("count", NewCount));

    /// <summary>This entry is the shown view.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>
    /// Shown in the sidebar. False hides an entry that exists for its channel's setting but has nothing to show
    /// (the Library group's Patch entry while no patch build is installed).
    /// </summary>
    [ObservableProperty]
    public partial bool IsShown { get; set; } = true;

    public override string ToString() => View.ToString();
}
