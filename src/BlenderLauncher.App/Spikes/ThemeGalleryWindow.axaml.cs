using Avalonia.Controls;
using BlenderLauncher.App.Theme;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.Spikes;

/// <summary>Developer gallery of the control themes (<c>--spike theme</c>), with accent and density toggles.</summary>
public partial class ThemeGalleryWindow : Window
{
    public ThemeGalleryWindow()
        : this(openMenu: false)
    {
    }

    /// <param name="openMenu">Opens the list's context menu once shown (for a screenshot: <c>--spike theme --open-menu</c>).</param>
    public ThemeGalleryWindow(bool openMenu)
    {
        InitializeComponent();
        if (openMenu)
        {
            Opened += (_, _) => Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                if (Rows.ContextMenu is { } menu)
                {
                    menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
                    menu.Open(AccentButton);
                    if (menu.Items[2] is MenuItem debug)
                    {
                        debug.IsSubMenuOpen = true;
                    }
                }
            }, TimeSpan.FromMilliseconds(500));
        }

        Rows.ItemsSource = new[] { "Row (hover me)", "Selected row", "Another row", "Right-click for the context menu" };
        AccentButton.Click += (_, _) =>
            ThemeManager.ApplyAccent(ThemeManager.CurrentAccent == Accent.Blue ? Accent.Orange : Accent.Blue);
        DensityButton.Click += (_, _) =>
            ThemeManager.ApplyDensity(ThemeManager.CurrentDensity == Density.Comfortable ? Density.Compact : Density.Comfortable);
    }
}
