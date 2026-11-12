using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.Views.Lists;

/// <summary>
/// The main area's view. Input only: every row button, double-click, key, drop and right-click is routed here once (not
/// bound per row, which keeps realised rows light) and forwarded to <see cref="BuildListViewModel"/>.
/// </summary>
/// <remarks>
/// <para>Shift: tracked from key and pointer events on the window. While Shift is held over a Launch button the button
/// shows "open previous file" (class <c>previous</c>) and a click launches with <c>--open-last</c>; Shift-click on
/// "Check for builds" forces a check.</para>
/// <para>The row context menu is attached in XAML with <c>detail:BuildContextMenu.Shell</c>.</para>
/// </remarks>
public partial class BuildListPanel : UserControl
{
    private const double FilterBoxWidth = 260;
    private const double FilterBoxMinWidth = 140;
    private const double TitleMinWidth = 160;

    private TopLevel? _topLevel;
    private KeyModifiers _modifiers;
    private Button? _hoveredLaunch;
    private ListBoxItem? _dropTarget;
    private DispatcherTimer? _clock;
    private BuildListViewModel? _viewModel;
    private bool _pushingSelection;

    public BuildListPanel()
    {
        InitializeComponent();

        AddHandler(Button.ClickEvent, OnButtonClick);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);

        InstalledList.SelectionChanged += OnInstalledSelectionChanged;
        DownloadList.SelectionChanged += OnDownloadSelectionChanged;
        InstalledList.DoubleTapped += OnListDoubleTapped;
        DownloadList.DoubleTapped += OnListDoubleTapped;
        InstalledList.KeyDown += OnListKeyDown;
        DownloadList.KeyDown += OnListKeyDown;

        InstalledList.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        InstalledList.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        InstalledList.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        InstalledList.AddHandler(DragDrop.DropEvent, OnDrop);

        FilterBox.KeyDown += OnFilterKeyDown;
        InstalledList.LayoutUpdated += (_, _) => AlignHeader(InstalledList, InstalledHeader);
        DownloadList.LayoutUpdated += (_, _) => AlignHeader(DownloadList, DownloadHeader);
        Header.SizeChanged += (_, e) => FitHeader(e.NewSize.Width);
    }

    private BuildListViewModel? ViewModel => DataContext as BuildListViewModel;

    private bool Shift => (_modifiers & KeyModifiers.Shift) != 0;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.AddHandler(KeyDownEvent, OnTopLevelKey, RoutingStrategies.Tunnel, handledEventsToo: true);
            _topLevel.AddHandler(KeyUpEvent, OnTopLevelKey, RoutingStrategies.Tunnel, handledEventsToo: true);
            _topLevel.AddHandler(KeyDownEvent, OnTopLevelShortcut, RoutingStrategies.Bubble);
            _topLevel.AddHandler(PointerPressedEvent, OnTopLevelPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
            _topLevel.AddHandler(PointerReleasedEvent, OnTopLevelPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
            if (_topLevel is Window window)
            {
                window.Deactivated += OnTopLevelDeactivated;
            }
        }

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => ViewModel?.RefreshRelativeTimes());
        _clock.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null)
        {
            _topLevel.RemoveHandler(KeyDownEvent, OnTopLevelKey);
            _topLevel.RemoveHandler(KeyUpEvent, OnTopLevelKey);
            _topLevel.RemoveHandler(KeyDownEvent, OnTopLevelShortcut);
            _topLevel.RemoveHandler(PointerPressedEvent, OnTopLevelPointer);
            _topLevel.RemoveHandler(PointerReleasedEvent, OnTopLevelPointer);
            if (_topLevel is Window window)
            {
                window.Deactivated -= OnTopLevelDeactivated;
            }
            _topLevel = null;
        }

        _clock?.Stop();
        _clock = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHoveredLaunch(null);
    }

    // ------------------------------------------------------------------ modifiers and Shift-hover

    private void OnTopLevelKey(object? sender, KeyEventArgs e)
    {
        // KeyModifiers lags by one event for the modifier key itself, so fold the key in.
        var modifiers = e.KeyModifiers;
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            modifiers = e.RoutedEvent == KeyDownEvent ? modifiers | KeyModifiers.Shift : modifiers & ~KeyModifiers.Shift;
        }

        SetModifiers(modifiers);

        if (e.RoutedEvent == KeyDownEvent && e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && !e.Handled)
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Window-wide keys that no control claimed (bubble, so a focused control sees them first): F5 checks for builds
    /// (Shift+F5 forces the check), Esc clears the filter from anywhere in the window.
    /// </summary>
    private void OnTopLevelShortcut(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F5 when (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None:
                vm.CheckForBuilds((e.KeyModifiers & KeyModifiers.Shift) != 0);
                e.Handled = true;
                break;
            case Key.Escape when e.KeyModifiers == KeyModifiers.None && vm.FilterText.Length > 0:
                vm.FilterText = "";
                e.Handled = true;
                break;
        }
    }

    /// <summary>F6: focuses the visible list's selected row, else its first row, else the filter box.</summary>
    public void FocusList()
    {
        var list = ViewModel?.Shell.IsDownloadView == true ? DownloadList : InstalledList;
        if (list.IsEffectivelyVisible && list.ItemCount > 0)
        {
            var index = Math.Max(0, list.SelectedIndex);
            list.ScrollIntoView(index);
            if (list.ContainerFromIndex(index) is { } row)
            {
                row.Focus(NavigationMethod.Tab);
                return;
            }
        }

        FilterBox.Focus(NavigationMethod.Tab);
    }

    private void OnTopLevelPointer(object? sender, PointerEventArgs e) => SetModifiers(e.KeyModifiers);

    private void OnTopLevelDeactivated(object? sender, EventArgs e) => SetModifiers(KeyModifiers.None);

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        _modifiers = e.KeyModifiers;
        SetHoveredLaunch((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is { } b && b.Classes.Contains("launch") ? b : null);
    }

    private void SetModifiers(KeyModifiers modifiers)
    {
        _modifiers = modifiers;
        _hoveredLaunch?.Classes.Set("previous", Shift);
    }

    private void SetHoveredLaunch(Button? button)
    {
        if (ReferenceEquals(button, _hoveredLaunch))
        {
            button?.Classes.Set("previous", Shift);
            return;
        }

        _hoveredLaunch?.Classes.Set("previous", false);
        _hoveredLaunch = button;
        _hoveredLaunch?.Classes.Set("previous", Shift);
    }

    // ------------------------------------------------------------------ buttons

    private void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || e.Source is not Button button)
        {
            return;
        }

        var classes = button.Classes;
        if (ReferenceEquals(button, CheckButton))
        {
            vm.CheckForBuilds(Shift);
        }
        else if (ReferenceEquals(button, RetryButton) || ReferenceEquals(button, BannerRetryButton))
        {
            vm.CheckForBuilds();
        }
        else if (ReferenceEquals(button, BrowseDownloadsButton))
        {
            vm.BrowseDownloads();
        }
        else if (classes.Contains("sort"))
        {
            vm.ToggleSort(classes.Contains("sort-version") ? SortColumn.Version : SortColumn.Built);
        }
        else
        {
            switch (button.DataContext)
            {
                case InstalledBuildRowViewModel row when classes.Contains("launch"):
                    vm.Launch(row, Shift);
                    break;
                case InstalledBuildRowViewModel row when classes.Contains("fav"):
                    vm.ToggleFavourite(row);
                    break;
                case InstalledBuildRowViewModel row when classes.Contains("init"):
                    vm.Initialise(row);
                    break;
                case InstalledBuildRowViewModel row when classes.Contains("remove"):
                    _ = vm.RemoveAsync(row, Shift);
                    break;
                case AvailableBuildRowViewModel row when classes.Contains("download"):
                    vm.Shell.SelectedAvailable = row;
                    vm.Download(row);
                    break;
                case AvailableBuildRowViewModel row when classes.Contains("cancel"):
                    vm.CancelDownload(row);
                    break;
                case AvailableBuildRowViewModel row when classes.Contains("launch"):
                    vm.LaunchInstalled(row, Shift);
                    break;
                default:
                    return;
            }
        }

        e.Handled = true;
    }

    // ------------------------------------------------------------------ lists

    // Selection is synchronised here rather than bound: the shell's selection outlives filtering and loading (a row
    // can be selected before it is in the list), and with multi-select the shell follows the row clicked last.

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as BuildListViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            PushSelection();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BuildListViewModel.SelectedInstalledItem) or nameof(BuildListViewModel.SelectedAvailableItem))
        {
            PushSelection();
        }
    }

    /// <summary>Shows the shell's selection in both lists (when the row is in them).</summary>
    private void PushSelection()
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        _pushingSelection = true;
        try
        {
            if (vm.SelectedInstalledItem is { } installed && vm.InstalledRows.Contains(installed)
                && !InstalledList.Selection.SelectedItems.Contains(installed))
            {
                InstalledList.SelectedItem = installed;
            }

            if (vm.SelectedAvailableItem is { } available && vm.AvailableRows.Contains(available)
                && !ReferenceEquals(DownloadList.SelectedItem, available))
            {
                DownloadList.SelectedItem = available;
            }
        }
        finally
        {
            _pushingSelection = false;
        }

        vm.SetSelectedInstalledRows(InstalledList.Selection.SelectedItems.OfType<InstalledBuildRowViewModel>());
    }

    private void OnInstalledSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        vm.SetSelectedInstalledRows(InstalledList.Selection.SelectedItems.OfType<InstalledBuildRowViewModel>());
        if (_pushingSelection)
        {
            return;
        }

        if (e.AddedItems.OfType<InstalledBuildRowViewModel>().LastOrDefault() is { } added)
        {
            vm.SelectedInstalledItem = added;
        }
        else if (vm.SelectedInstalledItem is { } current && !InstalledList.Selection.SelectedItems.Contains(current))
        {
            vm.SelectedInstalledItem = InstalledList.SelectedItem as InstalledBuildRowViewModel;
        }
    }

    private void OnDownloadSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is { } vm && !_pushingSelection)
        {
            vm.SelectedAvailableItem = DownloadList.SelectedItem as AvailableBuildRowViewModel;
        }
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is not { } vm || e.Source is not Visual source || source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is BuildRowViewModel row)
        {
            vm.Activate(row, (e.KeyModifiers & KeyModifiers.Shift) != 0);
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        switch (e.Key)
        {
            case Key.Enter when ReferenceEquals(sender, InstalledList) && vm.SelectedInstalledItem is { } installed:
                vm.Activate(installed, shift);
                e.Handled = true;
                break;
            case Key.Enter when ReferenceEquals(sender, DownloadList) && vm.SelectedAvailableItem is { } available:
                vm.Activate(available, shift);
                e.Handled = true;
                break;
            case Key.Delete when ReferenceEquals(sender, InstalledList):
                _ = vm.RemoveSelectedAsync(shift);
                e.Handled = true;
                break;
        }
    }

    // ------------------------------------------------------------------ drag and drop

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is InstalledBuildRowViewModel row && BuildListViewModel.CanOpenFiles(row, DroppedPaths(e)))
        {
            e.DragEffects = DragDropEffects.Copy;
            SetDropTarget(item);
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            SetDropTarget(null);
        }

        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => SetDropTarget(null);

    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetDropTarget(null);
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (ViewModel is { } vm && item?.DataContext is InstalledBuildRowViewModel row)
        {
            var paths = DroppedPaths(e);
            if (BuildListViewModel.CanOpenFiles(row, paths))
            {
                vm.Shell.SelectedInstalled = row;
                vm.OpenFiles(row, paths);
                e.DragEffects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }
    }

    private void SetDropTarget(ListBoxItem? item)
    {
        if (ReferenceEquals(item, _dropTarget))
        {
            return;
        }

        _dropTarget?.Classes.Remove("drop-target");
        _dropTarget = item;
        _dropTarget?.Classes.Add("drop-target");
    }

    private static List<string> DroppedPaths(DragEventArgs e)
    {
        var paths = new List<string>();
        if (e.DataTransfer.TryGetFiles() is { } files)
        {
            foreach (var file in files)
            {
                if (file.TryGetLocalPath() is { } path)
                {
                    paths.Add(path);
                }
            }
        }

        return paths;
    }

    // ------------------------------------------------------------------ header

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel is { } vm && vm.FilterText.Length > 0)
        {
            vm.FilterText = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            var list = ViewModel?.Shell.IsDownloadView == true ? DownloadList : InstalledList;
            if (list.ItemCount > 0)
            {
                list.SelectedIndex = Math.Max(0, list.SelectedIndex);
                list.ContainerFromIndex(list.SelectedIndex)?.Focus(NavigationMethod.Directional);
                e.Handled = true;
            }
        }
    }

    /// <summary>Keeps the column header over the columns when the list's scroll bar takes width.</summary>
    private static void AlignHeader(ListBox list, Border header)
    {
        if (list.Scroll is not ScrollViewer scroller || !list.IsVisible)
        {
            return;
        }

        var bar = Math.Max(0, Math.Round(list.Bounds.Width - scroller.Viewport.Width));
        var padding = header.Padding;
        if (Math.Abs(padding.Right - (padding.Left + bar)) > 0.5)
        {
            header.Padding = new Thickness(padding.Left, padding.Top, padding.Left + bar, padding.Bottom);
        }
    }

    /// <summary>The design's header wraps when narrow; here the filter box gives way first, down to 140 px.</summary>
    private void FitHeader(double width)
    {
        var button = CheckButton.IsVisible ? CheckButton.DesiredSize.Width + Header.ColumnSpacing : 0;
        var available = width - button - Header.ColumnSpacing - TitleMinWidth;
        FilterBox.Width = Math.Clamp(available, FilterBoxMinWidth, FilterBoxWidth);
    }
}
