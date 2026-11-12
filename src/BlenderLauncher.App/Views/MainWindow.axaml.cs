using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using BlenderLauncher.App.Controls;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.App.Views.Lists;
using BlenderLauncher.App.Views.Sidebar;

namespace BlenderLauncher.App.Views;

/// <summary>
/// The main window. Layout only; its behaviour (placement, close-to-tray, quit, chrome mode) is driven by
/// <c>Lifetime/AppController</c> and its content by <see cref="MainWindowViewModel"/>.
/// </summary>
/// <remarks>
/// Keyboard: Tab runs sidebar → filter → list → detail panel (the title bar's buttons are not Tab stops);
/// F6 and Shift+F6 jump between those regions. The list keys live in <see cref="BuildListPanel"/>.
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>For the XAML previewer.</summary>
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
    }

    public MainWindow(MainWindowViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    /// <summary>The custom title bar (hidden when the system title bar is used).</summary>
    public AppTitleBar AppTitleBar => TitleBar;

    public MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    /// <summary>F6 / Shift+F6: focus the next (previous) region: sidebar, list, detail panel (when shown).</summary>
    public void CycleRegion(bool backwards)
    {
        var regions = new List<ContentControl> { SidebarRegion, MainRegion };
        if (DetailRegion.IsEffectivelyVisible)
        {
            regions.Add(DetailRegion);
        }

        var focused = FocusManager?.GetFocusedElement() as Visual;
        var current = regions.FindIndex(r => focused is not null && (ReferenceEquals(focused, r) || r.IsVisualAncestorOf(focused)));
        var next = current < 0
            ? (backwards ? regions.Count - 1 : 0)
            : (current + (backwards ? regions.Count - 1 : 1)) % regions.Count;
        FocusRegion(regions[next]);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F6 && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None)
        {
            CycleRegion((e.KeyModifiers & KeyModifiers.Shift) != 0);
            e.Handled = true;
        }
    }

    private static void FocusRegion(ContentControl region)
    {
        var view = region.GetVisualDescendants();
        if (view.OfType<SidebarPanel>().FirstOrDefault() is { } sidebar)
        {
            sidebar.FocusActiveEntry();
        }
        else if (view.OfType<BuildListPanel>().FirstOrDefault() is { } list)
        {
            list.FocusList();
        }
        else if (view.OfType<InputElement>().FirstOrDefault(e => e.Focusable && e.IsTabStop && e.IsEffectivelyVisible && e.IsEffectivelyEnabled) is { } first)
        {
            first.Focus(NavigationMethod.Tab);
        }
    }
}
