using Avalonia;
using BlenderLauncher.App.Lifetime;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.Tests.Lifetime;

public class WindowPlacementTests
{
    private static readonly PixelRect Primary = new(0, 0, 1920, 1040);
    private static readonly PixelRect Right = new(1920, 0, 2560, 1400);

    [Fact]
    public void A_window_inside_a_screen_stays_where_it_was()
    {
        var saved = new PixelRect(100, 80, 1280, 800);
        Assert.Equal(saved, WindowPlacementService.Clamp(saved, [Primary, Right]));
    }

    [Fact]
    public void A_window_hanging_off_an_edge_is_pulled_in()
    {
        Assert.Equal(new PixelRect(640, 240, 1280, 800), WindowPlacementService.Clamp(new PixelRect(1500, 240, 1280, 800), [Primary]));
        Assert.Equal(new PixelRect(0, 0, 1280, 800), WindowPlacementService.Clamp(new PixelRect(-200, -10, 1280, 800), [Primary]));
    }

    [Fact]
    public void A_window_on_a_screen_that_is_gone_is_not_placed()
    {
        Assert.Null(WindowPlacementService.Clamp(new PixelRect(3000, 100, 1280, 800), [Primary]));
        Assert.Null(WindowPlacementService.Clamp(new PixelRect(100, 2000, 1280, 800), [Primary]));
    }

    [Fact]
    public void A_window_larger_than_its_screen_is_shrunk()
    {
        Assert.Equal(Primary, WindowPlacementService.Clamp(new PixelRect(0, 0, 2400, 1600), [Primary]));
    }

    [Fact]
    public void The_screen_showing_most_of_the_title_bar_wins()
    {
        var saved = new PixelRect(1800, 100, 1280, 800);
        Assert.Equal(new PixelRect(1920, 100, 1280, 800), WindowPlacementService.Clamp(saved, [Primary, Right]));
    }

    [Fact]
    public void Sample_mode_options_are_parsed()
    {
        Assert.Null(SampleMode.Parse(["--accent", "orange"]));
        var sample = SampleMode.Parse(["--sample-data", "--accent", "orange", "--density", "compact", "--view", "download:daily",
            "--no-details", "--size", "1400x820"]);
        Assert.NotNull(sample);
        Assert.Equal(Accent.Orange, sample.Settings.Appearance.Accent);
        Assert.Equal(Density.Compact, sample.Settings.Appearance.Density);
        Assert.False(sample.Settings.Appearance.ShowDetailsPanel);
        Assert.Equal(new SidebarView(SidebarSection.Download, "daily"), sample.View);
        Assert.Equal((1400, 820), (sample.Settings.Internal.Window.Width, sample.Settings.Internal.Window.Height));
    }
}
