using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.App.ViewModels.Detail;
using BlenderLauncher.App.ViewModels.Rows;

namespace BlenderLauncher.App.Views.Detail;

/// <summary>
/// The build row context menu. Attach it to a list (or any control whose descendants carry a
/// <see cref="BuildRowViewModel"/> as their DataContext) with
/// <c>detail:BuildContextMenu.Shell="{Binding Shell}"</c>; a right-click (or the menu key) on a row then opens the
/// menu of <see cref="BuildContextMenuViewModel"/> for that row, or the delete-only menu when several rows of a
/// <see cref="ListBox"/> are selected. No resource needs merging.
/// </summary>
/// <remarks>
/// The menu is built in code from the view model's entries (no reflection or runtime XAML), so it suits Native AOT.
/// While it is open, Shift (or Ctrl, as upstream) is followed from key events and by polling the key state, so the
/// delete/trash and config-folder headers swap live.
/// </remarks>
public static class BuildContextMenu
{
    /// <summary>The shell whose actions the menu calls; setting it enables the menu on the control.</summary>
    public static readonly AttachedProperty<MainWindowViewModel?> ShellProperty =
        AvaloniaProperty.RegisterAttached<Control, MainWindowViewModel?>("Shell", typeof(BuildContextMenu));

    static BuildContextMenu()
    {
        ShellProperty.Changed.AddClassHandler<Control>(OnShellChanged);
    }

    public static MainWindowViewModel? GetShell(Control control) => control.GetValue(ShellProperty);

    public static void SetShell(Control control, MainWindowViewModel? value) => control.SetValue(ShellProperty, value);

    /// <summary>Builds the Avalonia menu for <paramref name="model"/> (also used by tests).</summary>
    public static ContextMenu Create(BuildContextMenuViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var menu = new ContextMenu { ItemsSource = model.Entries.Select(ToItem).ToList() };

        DispatcherTimer? poll = null;
        void Follow(KeyModifiers modifiers) => model.IsShiftHeld = (modifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0;
        menu.AddHandler(InputElement.KeyDownEvent, (_, e) => Follow(e.KeyModifiers | ModifierOf(e.Key)), RoutingStrategies.Tunnel, handledEventsToo: true);
        menu.AddHandler(InputElement.KeyUpEvent, (_, e) => Follow(e.KeyModifiers & ~ModifierOf(e.Key)), RoutingStrategies.Tunnel, handledEventsToo: true);
        menu.Opened += (_, _) =>
        {
            model.IsShiftHeld = ModifierKeys.IsShiftOrControlDown;
            if (OperatingSystem.IsWindows())
            {
                poll = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Input,
                    (_, _) => model.IsShiftHeld = ModifierKeys.IsShiftOrControlDown);
                poll.Start();
            }
        };
        menu.Closed += (_, _) => poll?.Stop();
        return menu;
    }

    private static KeyModifiers ModifierOf(Key key) => key switch
    {
        Key.LeftShift or Key.RightShift => KeyModifiers.Shift,
        Key.LeftCtrl or Key.RightCtrl => KeyModifiers.Control,
        _ => KeyModifiers.None,
    };

    private static Control ToItem(BuildMenuEntry entry)
    {
        if (entry.IsSeparator)
        {
            return new Separator();
        }

        var item = new MenuItem
        {
            Header = entry.Header,
            Command = entry.Command,
            IsEnabled = entry.IsEnabled,
        };
        if (entry.HasChildren)
        {
            item.ItemsSource = entry.Children.Select(ToItem).ToList();
        }

        if (entry.IsChecked)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = true;
        }

        if (entry.IsDanger)
        {
            item.Classes.Add("danger");
        }

        if (entry.HasToolTip)
        {
            ToolTip.SetTip(item, entry.ToolTip);
        }

        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BuildMenuEntry.Header))
            {
                item.Header = entry.Header;
            }
            else if (e.PropertyName == nameof(BuildMenuEntry.IsEnabled))
            {
                item.IsEnabled = entry.IsEnabled;
            }
        }

        entry.PropertyChanged += OnChanged;
        item.DetachedFromLogicalTree += (_, _) => entry.PropertyChanged -= OnChanged;
        return item;
    }

    private static void OnShellChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        control.ContextRequested -= OnContextRequested;
        if (e.NewValue is not null)
        {
            control.ContextRequested += OnContextRequested;
        }
    }

    private static void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control host || GetShell(host) is not { } shell || e.Source is not Visual source)
        {
            return;
        }

        var (row, container) = FindRow(source, host);
        if (row is null)
        {
            return;
        }

        IReadOnlyList<BuildRowViewModel> rows = [row];
        if (host is ListBox list)
        {
            if (list.SelectedItems?.Contains(row) != true)
            {
                list.SelectedItem = row;
            }
            else if (list.SelectedItems.Count > 1)
            {
                rows = [.. list.SelectedItems.OfType<BuildRowViewModel>()];
            }
        }

        var model = new BuildContextMenuViewModel(shell, rows);
        e.Handled = true;
        if (!model.HasEntries)
        {
            return;
        }

        Create(model).Open(container ?? host);
    }

    // The nearest element between the source and the host whose DataContext is a row, and that element.
    private static (BuildRowViewModel? Row, Control? Container) FindRow(Visual source, Control host)
    {
        for (Visual? v = source; v is not null; v = v.GetVisualParent())
        {
            if (v is Control { DataContext: BuildRowViewModel row } c)
            {
                return (row, c is ListBoxItem ? c : c.FindAncestorOfType<ListBoxItem>() ?? c);
            }

            if (ReferenceEquals(v, host))
            {
                break;
            }
        }

        return (null, null);
    }
}
