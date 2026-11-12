using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BlenderLauncher.App.DesignData;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.App.ViewModels.Lists;
using BlenderLauncher.App.Views;
using BlenderLauncher.App.Views.Lists;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.Tests.Lists;

/// <summary>The lists' filter, sort and layout rules, on the design's sample data.</summary>
public class BuildListTests
{
    [AvaloniaFact]
    public void Filter_uses_a_version_query_first_and_fuzzy_text_otherwise()
    {
        var list = Loaded(SampleData.CreateMainWindowViewModel());

        list.FilterText = "5.2.^";
        Assert.Equal(["5.2.2"], list.InstalledRows.Select(r => r.Version));

        list.FilterText = "4.*.* lts";
        Assert.Equal(["4.5.6", "4.2.14"], list.InstalledRows.Select(r => r.Version));

        list.FilterText = "npr";
        Assert.Equal(Channel.Experimental, Assert.Single(list.InstalledRows).Channel);

        list.FilterText = "6a3d0b8";
        Assert.Equal("4.2.14", Assert.Single(list.InstalledRows).Version);

        list.FilterText = "zzzzzz";
        Assert.Empty(list.InstalledRows);
        Assert.Equal(ListEmptyState.NoMatch, list.EmptyState);
        Assert.Contains("zzzzzz", list.EmptyText, StringComparison.Ordinal);

        list.FilterText = "";
        Assert.Equal(7, list.InstalledRows.Count);
        Assert.Equal(ListEmptyState.None, list.EmptyState);
    }

    [AvaloniaFact]
    public void Filtering_keeps_the_selection_and_the_row_objects()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var list = Loaded(shell);
        var selected = shell.SelectedInstalled;
        var before = list.InstalledRows.ToList();

        list.FilterText = "daily";
        Assert.All(list.InstalledRows, r => Assert.Equal(Channel.Daily, r.Channel));
        list.SelectedInstalledItem = null; // what a list writes when its selection is filtered out
        Assert.Same(selected, shell.SelectedInstalled);

        list.FilterText = "";
        Assert.Equal(before, list.InstalledRows);
    }

    [AvaloniaFact]
    public void Sort_toggles_and_is_remembered_per_view()
    {
        var shell = SampleData.CreateMainWindowViewModel(settings: new AppSettings());
        var list = Loaded(shell);
        Assert.Equal(SortColumn.Built, list.Sort.Column);
        Assert.Equal("5.4.0", list.InstalledRows[0].Version); // newest build first

        list.ToggleSort(SortColumn.Version);
        Assert.True(list.Sort.Descending);
        Assert.Equal(["5.4.0", "5.4.0", "5.3.0", "5.2.2", "5.1.1", "4.5.6", "4.2.14"], list.InstalledRows.Select(r => r.Version));

        list.ToggleSort(SortColumn.Version);
        Assert.False(list.Sort.Descending);
        Assert.Equal("4.2.14", list.InstalledRows[0].Version);
        Assert.Equal(new ViewSort { Column = SortColumn.Version, Descending = false },
            shell.Settings!.Current.Internal.SortFor(SortViews.Installed));

        shell.Navigate(SidebarSection.Download, "daily");
        Assert.Equal(ViewSort.Default, list.Sort);
        shell.Navigate(SidebarSection.Installed, SidebarView.AllKey);
        Assert.Equal(SortColumn.Version, list.Sort.Column);
    }

    [AvaloniaFact]
    public void Views_select_their_rows_and_empty_states()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var list = Loaded(shell);

        shell.Navigate(SidebarSection.Installed, SidebarView.FavoritesKey);
        Assert.Equal(2, list.InstalledRows.Count);
        Assert.Equal("Favorites", list.Title);

        shell.Navigate(SidebarSection.Installed, "bforartists");
        Assert.Equal(ListEmptyState.NoBuilds, list.EmptyState);
        Assert.True(list.ShowBrowseDownloads);
        list.BrowseDownloads();
        Assert.Equal(new SidebarView(SidebarSection.Download, "bforartists"), shell.CurrentView);
        Assert.Equal(ListEmptyState.NoneAvailable, list.EmptyState);

        shell.Navigate(SidebarSection.Download, "daily");
        Assert.Equal(5, list.AvailableRows.Count);
        Assert.All(list.AvailableRows, r => Assert.Equal(Channel.Daily, r.Channel));
        Assert.StartsWith("Download · Daily", list.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Columns_fit_the_default_window_by_dropping_the_size_column()
    {
        // 1280 wide, sidebar 224, detail panel 300: 682 px for the row content.
        var narrow = ListColumnsPanel.ComputeWidths(ListColumnsLayout.Installed, 682);
        Assert.Equal(0, narrow[5]);
        Assert.True(narrow.Sum() + (ListColumnsPanel.Gap * (narrow.Count(w => w > 0) - 1)) <= 682.01);
        Assert.Equal(156, narrow[6]);

        // The design's 1400-wide canvas keeps every column.
        var wide = ListColumnsPanel.ComputeWidths(ListColumnsLayout.Installed, 802);
        Assert.All(wide, w => Assert.True(w > 0));
        Assert.Equal(1.4 / 1.3, wide[1] / wide[3], 3);
    }

    [AvaloniaFact]
    public void Window_shows_the_selected_row_selected()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var window = new MainWindow(shell) { Width = 1400, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var listBox = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "InstalledList");
        Assert.Equal(7, listBox.ItemCount);
        Assert.Same(shell.SelectedInstalled, listBox.SelectedItem);
        window.Close();
    }

    private static BuildListViewModel Loaded(MainWindowViewModel shell)
    {
        shell.BuildList.Refresh();
        return shell.BuildList;
    }
}
