using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace BlenderLauncher.App.Views.Sidebar;

public partial class SidebarPanel : UserControl
{
    public SidebarPanel()
    {
        InitializeComponent();
        // Tunnel: the scroll viewer would otherwise take the arrow keys to scroll.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>F6: focuses the active entry (the sidebar is one Tab stop; the arrows move inside it).</summary>
    public void FocusActiveEntry()
    {
        var target = EntryScroller.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("active") && b.IsEffectivelyVisible)
            ?? EntryScroller.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("nav-item"));
        if (target is not null)
        {
            target.Focus(NavigationMethod.Tab);
            target.BringIntoView();
        }
    }

    /// <summary>Up/Down move focus between entries across both groups and Settings; Home/End jump to the ends.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Source is not Button { Classes: var classes } current
            || !classes.Contains("nav-item"))
        {
            return;
        }

        // The groups top to bottom (visual order inside the scroll viewer), then Settings at the foot.
        var entries = EntryScroller.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.Classes.Contains("nav-item") && b.IsEffectivelyVisible && b.IsEffectivelyEnabled)
            .Append(SettingsButton)
            .ToList();
        var index = entries.IndexOf(current);
        if (index < 0)
        {
            return;
        }

        var target = e.Key switch
        {
            Key.Up => Math.Max(0, index - 1),
            Key.Down => Math.Min(entries.Count - 1, index + 1),
            Key.Home => 0,
            Key.End => entries.Count - 1,
            _ => -1,
        };
        if (target < 0)
        {
            return;
        }

        entries[target].Focus(NavigationMethod.Directional);
        entries[target].BringIntoView();
        e.Handled = true;
    }
}
