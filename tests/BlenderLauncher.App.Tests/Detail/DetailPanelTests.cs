using Avalonia.Headless.XUnit;
using BlenderLauncher.App.DesignData;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.App.ViewModels.Detail;
using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.Tests.Detail;

/// <summary>The detail panel's view model with the design's sample data (no services).</summary>
public class DetailPanelTests
{
    [AvaloniaFact]
    public void Follows_the_selection_and_shows_the_launch_states()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var detail = Assert.IsType<InstalledDetailViewModel>(shell.Detail.Current);
        Assert.Same(shell.SelectedInstalled, detail.Row);
        Assert.Equal("Launch 5.2.2", detail.LaunchText);

        detail.Row.IsStarting = true;
        Assert.Equal("Starting…", detail.LaunchText);
        detail.Row.IsStarting = false;
        detail.Row.RunningCount = 2;
        Assert.Equal("Launch new instance", detail.LaunchText);

        shell.Navigate(SidebarSection.Download, "daily");
        var download = Assert.IsType<AvailableDetailViewModel>(shell.Detail.Current);
        Assert.StartsWith("Download · ", download.DownloadText);

        shell.Select(null);
        Assert.Null(shell.Detail.Current);
        Assert.False(shell.Detail.HasSelection);
    }

    [AvaloniaFact]
    public void Section_state_is_written_to_the_settings_and_read_back()
    {
        var shell = SampleData.CreateMainWindowViewModel(settings: new AppSettings());
        Assert.True(shell.Detail.IsBuildExpanded);
        Assert.False(shell.Detail.IsMoreExpanded);

        shell.Detail.IsBuildExpanded = false;
        shell.Detail.IsMoreExpanded = true;
        Assert.Equal(["build", "more-open"], shell.Settings!.Current.Internal.CollapsedDetailSections);

        var again = SampleData.CreateMainWindowViewModel(settings: shell.Settings.Current);
        Assert.False(again.Detail.IsBuildExpanded);
        Assert.True(again.Detail.IsMoreExpanded);
        Assert.True(again.Detail.IsOptionsExpanded);
    }

    [AvaloniaFact]
    public void Options_change_the_row_and_default_is_exclusive()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var second = shell.Catalog.Installed[1];
        shell.SelectedInstalled = second;
        var detail = Assert.IsType<InstalledDetailViewModel>(shell.Detail.Current);

        detail.IsDefault = true;
        Assert.True(second.IsDefault);
        Assert.Single(shell.Catalog.Installed, r => r.IsDefault);

        detail.IsFavourite = true;
        detail.IsPortable = true;
        Assert.True(second.IsFavourite);
        Assert.True(second.IsPortable);
    }

    [AvaloniaFact]
    public async Task Remove_asks_first_and_shift_inverts_trash_and_delete()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var ui = new ScriptedUi { Answer = 1 };
        shell.Actions.Ui = ui;
        var row = shell.Catalog.Installed.First(r => !r.IsRunning);
        shell.SelectedInstalled = row;

        await shell.Actions.RemoveAsync(row);
        Assert.Contains(row, shell.Catalog.Installed);
        Assert.Equal("Send to Trash", ui.LastTitle);

        await shell.Actions.RemoveAsync(row, invertDeleteSetting: true);
        Assert.Equal("Delete From Drive", ui.LastTitle);

        ui.Answer = 0;
        await shell.Actions.RemoveAsync(row);
        Assert.DoesNotContain(row, shell.Catalog.Installed);
        Assert.Null(shell.SelectedInstalled);
    }

    [AvaloniaFact]
    public async Task A_running_build_is_not_removed()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var ui = new ScriptedUi { Answer = 0 };
        shell.Actions.Ui = ui;
        var running = shell.Catalog.Installed.First(r => r.IsRunning);

        await shell.Actions.RemoveAsync(running);
        Assert.Contains(running, shell.Catalog.Installed);
        Assert.Null(ui.LastTitle);
    }

    internal sealed class ScriptedUi : IBuildActionsUi
    {
        public int Answer { get; set; }

        public string? LastTitle { get; private set; }

        public string? Clipboard { get; private set; }

        public List<string> Opened { get; } = [];

        public Task<int> AskAsync(string title, string message, IReadOnlyList<PromptButton> buttons)
        {
            LastTitle = title;
            return Task.FromResult(Answer);
        }

        public Task<string?> AskTextAsync(string title, string message, string initial, string confirmText) => Task.FromResult<string?>(null);

        public Task<bool> SetClipboardTextAsync(string text)
        {
            Clipboard = text;
            return Task.FromResult(true);
        }

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string? startFolder) => Task.FromResult<string?>(null);

        public void OpenWithShell(string target) => Opened.Add(target);
    }
}
