using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using BlenderLauncher.App.DesignData;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.App.ViewModels.Sidebar;
using BlenderLauncher.App.Views;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.Tests.Sidebar;

/// <summary>The sidebar's entries, counts and badges follow the catalog and the settings.</summary>
public class SidebarTests
{
    [AvaloniaFact]
    public void Counts_and_badges_follow_the_catalog()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var sidebar = shell.Sidebar;

        Assert.Equal(["all", "favorites", "stable", "lts", "daily", "experimental", "patch", "bforartists", "upbge-stable", "custom"],
            sidebar.LibraryItems.Select(i => i.View.Key));
        Assert.Equal([7, 2, 1, 3, 2, 1, 0, 0, 0, 0], sidebar.LibraryItems.Select(i => i.Count));
        Assert.False(sidebar.LibraryItems.Single(i => i.Channel == Channel.Patch).IsShown);

        // "N new" = new and not installed: Daily 2, Experimental 1.
        Assert.Equal(["stable", "lts", "daily", "experimental", "patch", "bforartists", "upbge-stable"],
            sidebar.DownloadItems.Select(i => i.View.Key));
        Assert.Equal("2 new", Item(sidebar, SidebarSection.Download, "daily").NewText);
        Assert.Equal(1, Item(sidebar, SidebarSection.Download, "experimental").NewCount);
        Assert.False(Item(sidebar, SidebarSection.Download, "stable").HasNew);

        // Live: un-favouriting and removing a build recount at once.
        var favourite = shell.Catalog.Installed.First(r => r.IsFavourite);
        favourite.IsFavourite = false;
        Assert.Equal(1, Item(sidebar, SidebarSection.Installed, SidebarView.FavoritesKey).Count);
        var daily = shell.Catalog.Installed.First(r => r.Channel == Channel.Daily);
        shell.Catalog.Installed.Remove(daily);
        Assert.Equal(1, Item(sidebar, SidebarSection.Installed, "daily").Count);
        Assert.Equal(6, Item(sidebar, SidebarSection.Installed, SidebarView.AllKey).Count);
        Assert.Equal(shell.Catalog.Installed.Sum(r => r.SizeBytes ?? 0), sidebar.Disk.UsedBytes);

        // Removing a row also stops listening to it.
        daily.IsFavourite = true;
        Assert.Equal(1, Item(sidebar, SidebarSection.Installed, SidebarView.FavoritesKey).Count);
    }

    [AvaloniaFact]
    public void Entries_follow_the_settings_and_selection_drives_navigation()
    {
        var shell = SampleData.CreateMainWindowViewModel(settings: new AppSettings());
        var sidebar = shell.Sidebar;
        Assert.True(sidebar.LibraryItems[0].IsActive);

        shell.Settings!.Update(s => s with
        {
            Builds = s.Builds with { ShowUpbgeWeekly = true, ScrapeUpbgeWeekly = true, ScrapeDaily = false, ShowBforartists = false },
        });
        Assert.Contains(sidebar.LibraryItems, i => i.Channel == Channel.UpbgeWeekly);
        Assert.Contains(sidebar.DownloadItems, i => i.Channel == Channel.UpbgeWeekly);
        Assert.DoesNotContain(sidebar.DownloadItems, i => i.Channel == Channel.Daily);
        Assert.Contains(sidebar.LibraryItems, i => i.Channel == Channel.Daily);
        Assert.DoesNotContain(sidebar.LibraryItems.Concat(sidebar.DownloadItems), i => i.Channel == Channel.Bforartists);

        var experimental = Item(sidebar, SidebarSection.Download, "experimental");
        sidebar.SelectCommand.Execute(experimental);
        Assert.Equal(experimental.View, shell.CurrentView);
        Assert.True(experimental.IsActive);
        Assert.Single(sidebar.LibraryItems.Concat(sidebar.DownloadItems), i => i.IsActive);
    }

    [AvaloniaFact]
    public void Settings_entry_toasts_until_a_settings_window_handles_it()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        string? toast = null;
        shell.Toasts.Shown += (_, text) => toast = text;
        shell.Sidebar.OpenSettingsCommand.Execute(null);
        Assert.Equal("Settings arrive in a later version", toast);

        var raised = false;
        shell.Sidebar.SettingsRequested += (_, _) => raised = true;
        toast = null;
        shell.Sidebar.OpenSettingsCommand.Execute(null);
        Assert.True(raised);
        Assert.Null(toast);
    }

    [AvaloniaFact]
    public async Task Disk_card_measures_the_builds_against_the_volume()
    {
        var disk = new DiskUsageViewModel(_ => new VolumeInfo(TotalBytes: 100_000_000_000, FreeBytes: 40_000_000_000))
        {
            LibraryRoot = @"D:\Blender\builds",
            UsedBytes = 25_000_000_000,
        };
        await disk.RefreshVolumeAsync();
        Assert.Equal(0.25, disk.Fraction, 6);
        Assert.Equal("25.0 GB", disk.UsedText);
        Assert.Equal("D:\\Blender\\builds\n25.0 GB used by builds · 40.0 GB free of 100.0 GB", disk.TooltipText);

        disk.UsedBytes = 1_000_000;
        Assert.Equal(DiskUsageViewModel.MinimumVisibleFraction, disk.Fraction);

        disk.LibraryRoot = null;
        await disk.RefreshVolumeAsync();
        Assert.Equal(0, disk.Fraction);
        Assert.Equal("No library folder", disk.PathText);
    }

    [AvaloniaFact]
    public void Arrow_keys_move_between_entries_and_enter_selects()
    {
        var shell = SampleData.CreateMainWindowViewModel();
        var window = new MainWindow(shell) { Width = 1400, Height = 820 };
        window.Show();
        var buttons = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "EntryScroller")
            .GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("nav-item") && b.IsEffectivelyVisible).ToList();
        Assert.True(buttons[0].Focus(NavigationMethod.Tab));

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.Same(buttons[3], window.FocusManager?.GetFocusedElement());
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(new SidebarView(SidebarSection.Installed, "lts"), shell.CurrentView);

        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.Same(buttons[2], window.FocusManager?.GetFocusedElement());
        window.Close();
    }

    private static SidebarItemViewModel Item(SidebarViewModel sidebar, SidebarSection section, string key) =>
        sidebar.LibraryItems.Concat(sidebar.DownloadItems).Single(i => i.View == new SidebarView(section, key));
}
