using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using BlenderLauncher.App.DesignData;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.App.ViewModels.Detail;
using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.App.Views.Detail;

namespace BlenderLauncher.App.Tests.Detail;

/// <summary>Which entries the row menu holds, and Shift swapping delete and trash.</summary>
public class BuildContextMenuTests
{
    [AvaloniaFact]
    public void Installed_row_menu_has_upstreams_actions_and_shift_swaps_the_delete_entry()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var lts = shell.Catalog.Installed[0];
        var menu = new BuildContextMenuViewModel(shell, lts);

        string[] expected =
        [
            BuildMenuIds.Launch, BuildMenuIds.OpenLast, BuildMenuIds.Favourite, BuildMenuIds.Default, BuildMenuIds.Template,
            BuildMenuIds.Portable, BuildMenuIds.CopyHash, BuildMenuIds.Freeze, BuildMenuIds.ReleaseNotes,
            BuildMenuIds.BuildFolder, BuildMenuIds.ConfigFolder, BuildMenuIds.Rename, BuildMenuIds.Edit, BuildMenuIds.Remove,
        ];
        foreach (var id in expected)
        {
            Assert.NotNull(menu.Find(id));
        }

        Assert.Null(menu.Find(BuildMenuIds.FetchPrName)); // not a pull request
        Assert.Null(menu.Find(BuildMenuIds.Update)); // no update offered
        Assert.False(menu.Entries[0].IsSeparator);
        Assert.False(menu.Entries[^1].IsSeparator);

        var remove = menu.Find(BuildMenuIds.Remove)!;
        Assert.True(remove.IsDanger);
        Assert.Equal("Send to Trash", remove.Header);
        menu.IsShiftHeld = true;
        Assert.Equal("Delete From Drive", remove.Header);
    }

    [AvaloniaFact]
    public void More_section_leaves_out_what_the_panel_already_shows()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var more = new BuildContextMenuViewModel(shell, [shell.Catalog.Installed[0]], BuildMenuScope.MoreSection);

        foreach (var id in new[] { BuildMenuIds.Launch, BuildMenuIds.Favourite, BuildMenuIds.Default, BuildMenuIds.Portable, BuildMenuIds.Remove })
        {
            Assert.Null(more.Find(id));
        }

        Assert.NotNull(more.Find(BuildMenuIds.OpenLast));
        Assert.DoesNotContain(more.FlatEntries, e => e.IsSeparator);
    }

    [AvaloniaFact]
    public void Several_rows_get_the_delete_only_menu_and_downloads_their_own()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var multi = new BuildContextMenuViewModel(shell, [shell.Catalog.Installed[1], shell.Catalog.Installed[2]]);
        Assert.Equal([BuildMenuIds.Remove], multi.Entries.Select(e => e.Id));

        var idle = shell.Catalog.Available.First(r => r.IsIdle);
        var download = new BuildContextMenuViewModel(shell, idle);
        Assert.Equal(BuildMenuIds.Download, download.Entries[0].Id);
        Assert.Null(download.Find(BuildMenuIds.Remove));

        // An installed download shows the installed row's menu, as upstream.
        var installed = shell.Catalog.Available.First(r => r.IsInstalled);
        Assert.NotNull(new BuildContextMenuViewModel(shell, installed).Find(BuildMenuIds.Remove));
    }

    [AvaloniaFact]
    public void Avalonia_menu_mirrors_the_entries()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var model = new BuildContextMenuViewModel(shell, shell.Catalog.Installed[0]);
        var menu = BuildContextMenu.Create(model);
        var items = menu.ItemsSource!.Cast<Control>().ToList();
        Assert.Equal(model.Entries.Count, items.Count);

        var remove = (MenuItem)items[^1];
        Assert.Contains("danger", remove.Classes);
        model.IsShiftHeld = true;
        Assert.Equal("Delete From Drive", remove.Header);
    }

    [AvaloniaFact]
    public async Task Copy_hash_puts_the_full_hash_on_the_clipboard()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var ui = new DetailPanelTests.ScriptedUi();
        shell.Actions.Ui = ui;
        BuildRowViewModel row = shell.Catalog.Installed[0];

        await shell.Actions.CopyHashAsync(row);
        Assert.Equal(row.Hash, ui.Clipboard);
    }
}
