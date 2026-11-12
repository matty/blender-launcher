using BlenderLauncher.App.Localisation;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.ViewModels.Settings;

/// <summary>Settings → Appearance: every value applies live (the app controller follows the settings).</summary>
public sealed partial class SettingsViewModel
{
    private static readonly Channel[] ViewChannels =
    [
        Channel.Stable, Channel.Lts, Channel.Daily, Channel.Experimental, Channel.Patch, Channel.Bforartists,
        Channel.UpbgeStable, Channel.UpbgeWeekly,
    ];

    private IReadOnlyList<SidebarView>? _views;

    public bool UseSystemTitleBar
    {
        get => Current.Appearance.UseSystemTitleBar;
        set => Update(s => s with { Appearance = s.Appearance with { UseSystemTitleBar = value } });
    }

    public bool NotifyNewBuilds
    {
        get => Current.Appearance.NotifyNewBuilds;
        set => Update(s => s with { Appearance = s.Appearance with { NotifyNewBuilds = value } });
    }

    public bool NotifyDownloadFinished
    {
        get => Current.Appearance.NotifyDownloadFinished;
        set => Update(s => s with { Appearance = s.Appearance with { NotifyDownloadFinished = value } });
    }

    public bool NotifyErrors
    {
        get => Current.Appearance.NotifyErrors;
        set => Update(s => s with { Appearance = s.Appearance with { NotifyErrors = value } });
    }

    public IReadOnlyList<string> AccentNames { get; } = [Loc.Tr("ui.settings.appearance.accent_blue"), Loc.Tr("ui.settings.appearance.accent_orange")];

    public int AccentIndex
    {
        get => (int)Current.Appearance.Accent;
        set
        {
            if (Enum.IsDefined((Accent)value))
            {
                Update(s => s with { Appearance = s.Appearance with { Accent = (Accent)value } });
            }
        }
    }

    public IReadOnlyList<string> DensityNames { get; } =
        [Loc.Tr("ui.settings.appearance.density_comfortable"), Loc.Tr("ui.settings.appearance.density_compact")];

    public int DensityIndex
    {
        get => (int)Current.Appearance.Density;
        set
        {
            if (Enum.IsDefined((Density)value))
            {
                Update(s => s with { Appearance = s.Appearance with { Density = (Density)value } });
            }
        }
    }

    public bool ShowDetailsPanel
    {
        get => Current.Appearance.ShowDetailsPanel;
        set => Update(s => s with { Appearance = s.Appearance with { ShowDetailsPanel = value } });
    }

    /// <summary>The sidebar entries a start-up view can be, in sidebar order.</summary>
    public IReadOnlyList<SidebarView> Views => _views ??= BuildViews();

    public IReadOnlyList<string> ViewNames => _viewNames ??= Views.Select(ViewName).ToArray();

    private IReadOnlyList<string>? _viewNames;

    public int DefaultViewIndex
    {
        get
        {
            var current = SidebarView.TryParse(Current.Appearance.DefaultView, out var view) ? view.Value : SidebarView.Default;
            return Math.Max(0, Views.ToList().IndexOf(current));
        }
        set
        {
            if (value >= 0 && value < Views.Count)
            {
                Update(s => s with { Appearance = s.Appearance with { DefaultView = Views[value].ToString() } });
            }
        }
    }

    private static List<SidebarView> BuildViews()
    {
        var views = new List<SidebarView>
        {
            new(SidebarSection.Installed, SidebarView.AllKey),
            new(SidebarSection.Installed, SidebarView.FavoritesKey),
        };
        views.AddRange(ViewChannels.Append(Channel.Custom).Select(c => new SidebarView(SidebarSection.Installed, DisplayFormat.ChannelKey(c))));
        views.AddRange(ViewChannels.Select(c => new SidebarView(SidebarSection.Download, DisplayFormat.ChannelKey(c))));
        return views;
    }

    private static string ViewName(SidebarView view)
    {
        var name = view.Key switch
        {
            SidebarView.AllKey => Loc.Tr("ui.sidebar.all_builds"),
            SidebarView.FavoritesKey => Loc.Tr("ui.sidebar.favorites"),
            _ => DisplayFormat.ChannelFromKey(view.Key) is { } channel ? DisplayFormat.ChannelName(channel) : view.Key,
        };
        return Loc.Tr(view.Section == SidebarSection.Installed ? "ui.settings.appearance.view_library" : "ui.settings.appearance.view_download",
            ("name", name));
    }
}
