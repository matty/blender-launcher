using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using BlenderLauncher.App.DesignData;
using BlenderLauncher.App.Tests.Headless;
using BlenderLauncher.App.Theme;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.App.Views;
using BlenderLauncher.App.Views.Detail;
using BlenderLauncher.App.Views.Lists;
using BlenderLauncher.App.Views.Sidebar;
using BlenderLauncher.App.Views.StatusBar;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.Tests.Shell;

/// <summary>The shell with the design's sample data, on the headless platform.</summary>
public class MainWindowTests
{
    [AvaloniaFact]
    public void Shell_resolves_each_region_and_renders_the_design_surfaces()
    {
        var window = new MainWindow(SampleData.CreateMainWindowViewModel()) { Width = 1400, Height = 820 };
        window.Show();

        Assert.IsType<SidebarPanel>(RegionView(window, "SidebarRegion"));
        Assert.IsType<BuildListPanel>(RegionView(window, "MainRegion"));
        Assert.IsType<DetailPanel>(RegionView(window, "DetailRegion"));
        Assert.IsType<StatusBarPanel>(RegionView(window, "StatusBarRegion"));

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(Color.Parse("#212121"), HeadlessRenderTests.PixelAt(frame, 100, 400));
        Assert.Equal(Color.Parse("#2a2a2a"), HeadlessRenderTests.PixelAt(frame, 700, 700));
        Assert.Equal(Color.Parse("#242424"), HeadlessRenderTests.PixelAt(frame, 1380, 700));
        Assert.Equal(Color.Parse("#181818"), HeadlessRenderTests.PixelAt(frame, 700, 815));
        window.Close();
    }

    [AvaloniaFact]
    public void Detail_panel_hides_with_the_setting()
    {
        var settings = new AppSettings { Appearance = new AppearanceSettings { ShowDetailsPanel = false } };
        var shell = SampleData.CreateMainWindowViewModel(settings: settings);
        var window = new MainWindow(shell) { Width = 1400, Height = 820 };
        window.Show();

        var detailHost = window.FindControl<ContentControl>("DetailRegion")!.GetVisualParent<Border>()!;
        Assert.False(detailHost.IsVisible);
        shell.ApplyAppearance(new AppearanceSettings { ShowDetailsPanel = true });
        Assert.True(detailHost.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Accent_and_density_switch_at_runtime()
    {
        var resources = Application.Current!.Resources;
        try
        {
            ThemeManager.ApplyAccent(Accent.Orange);
            ThemeManager.ApplyDensity(Density.Compact);
            Assert.Equal(Color.Parse("#b8620f"), ((ISolidColorBrush)resources["BlAccentBrush"]!).Color);
            Assert.Equal(Color.Parse("#4a3a2a"), ((ISolidColorBrush)resources["BlAccentRowBrush"]!).Color);
            Assert.Equal(34d, resources["BlRowHeight"]);
            Assert.Equal(false, resources["BlRowSecondLineVisible"]);

            Assert.True(Application.Current.TryGetResource("BlPrimaryButton", null, out var theme));
            var button = new Button { Theme = (Avalonia.Styling.ControlTheme)theme!, Content = "x" };
            var window = new Window { Content = button };
            window.Show();
            Assert.Equal(Color.Parse("#b8620f"), ((ISolidColorBrush)button.Background!).Color);
            ThemeManager.ApplyAccent(Accent.Blue);
            Assert.Equal(Color.Parse("#4772b3"), ((ISolidColorBrush)button.Background!).Color);
            window.Close();
        }
        finally
        {
            ThemeManager.ApplyAccent(Accent.Blue);
            ThemeManager.ApplyDensity(Density.Comfortable);
        }
    }

    [AvaloniaFact]
    public void Navigation_keeps_a_selection_per_section_and_raises_events()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var views = new List<SidebarView>();
        var selections = new List<BuildRowViewModel?>();
        shell.ViewChanged += (_, v) => views.Add(v);
        shell.SelectionChanged += (_, s) => selections.Add(s);
        var installed = shell.SelectedInstalled;
        Assert.NotNull(installed);
        Assert.Same(installed, shell.SelectedBuild);

        shell.Navigate(SidebarSection.Download, "daily");
        Assert.True(shell.IsDownloadView);
        Assert.Equal(Channel.Daily, shell.CurrentChannel);
        Assert.Same(shell.SelectedAvailable, shell.SelectedBuild);

        var lts = shell.Catalog.Available.First(r => r.Channel == Channel.Lts && r.Version == "5.2.2");
        Assert.True(lts.IsInstalled);
        shell.ShowInstalledBuild(lts.InstalledRow!);
        Assert.Equal(SidebarView.Default, shell.CurrentView);
        Assert.Same(lts.InstalledRow, shell.SelectedBuild);
        Assert.Equal([new SidebarView(SidebarSection.Download, "daily"), SidebarView.Default], views);
        Assert.Same(lts.InstalledRow, selections[^1]);
    }

    [AvaloniaFact]
    public void Sample_data_matches_the_design()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var catalog = shell.Catalog;

        Assert.Equal(7, catalog.Installed.Count);
        Assert.Equal(21, catalog.Available.Count);
        var first = catalog.Installed[0];
        Assert.Equal("5.2.2", first.Version);
        Assert.Equal(Channel.Lts, first.Channel);
        Assert.Equal("Blender 5.2 LTS", first.Name);
        Assert.Equal("v5.2.2", first.Branch);
        Assert.Equal("a3f81c0", first.ShortHash);
        Assert.True(first.IsDefault && first.IsFavourite);
        Assert.Same(first, catalog.DefaultBuild);
        Assert.Equal(1, catalog.TotalRunning);
        Assert.Equal(2, catalog.Installed.Count(r => r.IsFavourite));
        Assert.Equal(2, catalog.Installed.Count(r => r.ReplaceOnNewBuild));

        var daily = catalog.Installed.Single(r => r.Hash == "f6d2a8810c3e");
        Assert.Equal(("5.4.0", "Alpha", "Blender 5.4 Alpha", "main"), (daily.Version, daily.Label, daily.Name, daily.Branch));
        Assert.True(daily.IsRunning);
        var beta = catalog.Installed.Single(r => r.Hash == "e01b7f3a9c42");
        Assert.Equal(("Beta", "blender-v5.3-release"), (beta.Label, beta.Branch));
        var rc = catalog.Available.Single(r => r.Hash == "4c90e7d2a15b");
        Assert.Equal(("RC", "blender-v5.2-release", "413 MB"), (rc.Label, rc.Branch, rc.SizeText));

        // "N new": new and not installed.
        Assert.Equal(3, catalog.Available.Count(r => r.IsNew && !r.IsInstalled));
        Assert.Equal(7, catalog.Available.Count(r => r.IsInstalled));
        Assert.All(catalog.Available.Where(r => !r.IsInstalled), r => Assert.True(r.IsIdle));
    }

    private static Control? RegionView(Window window, string name)
    {
        var host = window.FindControl<ContentControl>(name);
        Assert.NotNull(host);
        return host.GetVisualDescendants().OfType<ContentPresenter>().First().Child;
    }
}
