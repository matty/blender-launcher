using Avalonia.Headless.XUnit;
using BlenderLauncher.App.DesignData;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Pipeline;
using BlenderLauncher.Core.Scraping;

namespace BlenderLauncher.App.Tests.Sidebar;

/// <summary>The status bar's texts and download summary.</summary>
public class StatusBarTests
{
    [AvaloniaFact]
    public void Shows_default_running_and_aggregate_downloads()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var status = shell.StatusBar;
        Assert.Equal("Blender 5.2 LTS (5.2.2)", status.DefaultText);
        Assert.True(status.HasRunning);
        Assert.Equal("1 instance running", status.RunningText);
        Assert.False(status.HasDownloads);

        var daily = shell.Catalog.Available.First(r => r.Channel == Channel.Daily && r.IsIdle);
        daily.ApplySample(null, InstallState.Downloading, 0.4);
        Assert.True(status.HasDownloads);
        Assert.Equal("Downloading " + daily.Name, status.DownloadText);
        Assert.Equal("40%", status.DownloadPercentText);

        var experimental = shell.Catalog.Available.First(r => r.Channel == Channel.Experimental && r.IsIdle);
        experimental.ApplySample(null, InstallState.Downloading, 0.8);
        Assert.Equal("Downloading 2 builds", status.DownloadText);
        Assert.Equal(0.6, status.DownloadProgress, 6);
        Assert.Equal("60%", status.DownloadPercentText);

        daily.ApplySample(null);
        experimental.ApplySample(null);
        Assert.False(status.HasDownloads);
    }

    [AvaloniaFact]
    public void Status_text_follows_upstream()
    {
        var at = new DateTimeOffset(2026, 11, 11, 14, 5, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 11)));
        Assert.Equal("Checking for new builds", StatusBarViewModel.StatusFor(true, true, null, at));
        Assert.Equal("Reading local builds", StatusBarViewModel.StatusFor(false, true, null, null));
        Assert.Equal("Last check at 14:05", StatusBarViewModel.StatusFor(false, false, null, at));
        Assert.Equal("", StatusBarViewModel.StatusFor(false, false, null, null));

        var failure = ScrapeError.ConnectionFailed("stable");
        var failed = new ScrapeSnapshot
        {
            Sources = [new ScrapeSourceResult("stable", [Channel.Stable], [], failure, TimeSpan.Zero, 0, 0)],
            Builds = [],
            Errors = [failure],
            CompletedAt = at,
        };
        var text = StatusBarViewModel.StatusFor(false, false, failed, at);
        Assert.StartsWith("Error: connection failed at ", text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Toast_takes_the_status_slot()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var status = shell.StatusBar;
        Assert.True(status.ShowStatus);
        shell.ShowToast("Hello");
        Assert.False(status.ShowStatus);
        shell.Toasts.Clear();
        Assert.True(status.ShowStatus);
    }
}
