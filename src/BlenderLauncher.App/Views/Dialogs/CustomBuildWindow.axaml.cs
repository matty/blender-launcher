using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BlenderLauncher.App.Controls;
using BlenderLauncher.App.Lifetime;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;

namespace BlenderLauncher.App.Views.Dialogs;

/// <summary>
/// The custom build dialog; layout in the XAML, state in <see cref="CustomBuildViewModel"/>. Modal over its
/// owner; closes with the <see cref="BuildInfo"/> to write, or null when cancelled. Esc cancels, Enter accepts when
/// Accept is enabled.
/// </summary>
public partial class CustomBuildWindow : Window
{
    private Window? _owner;

    /// <summary>For the XAML previewer.</summary>
    public CustomBuildWindow()
        : this(new CustomBuildViewModel(Path.GetTempPath(), null, null))
    {
    }

    public CustomBuildWindow(CustomBuildViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        TitleBar.IsDialog = true;
        WindowChrome.Follow(this, TitleBar);
        TitleBarHost.IsVisible = TitleBar.IsVisible;
        TitleBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                TitleBarHost.IsVisible = TitleBar.IsVisible;
            }
        };

        CancelButton.Click += (_, _) => Close(null);
        AcceptButton.Click += (_, _) => Accept();
        BrowseButton.Click += async (_, _) => await BrowseAsync().ConfigureAwait(true);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            DialogPlacement.KeepOnOwnerScreen(this, _owner);
            ExecutableBox.Focus();
        };
    }

    public CustomBuildViewModel ViewModel => (CustomBuildViewModel)DataContext!;

    /// <summary>Shows the dialog modal over <paramref name="owner"/>; returns the fields to write, or null.</summary>
    public async Task<BuildInfo?> ShowForResultAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
        WindowPositionOverride.Apply(this);
        return await ShowDialog<BuildInfo?>(owner).ConfigureAwait(true);
    }

    private void Accept()
    {
        if (ViewModel.ToBuildInfo() is { } build)
        {
            Close(build);
        }
    }

    private async Task BrowseAsync()
    {
        if (!StorageProvider.CanOpen)
        {
            return;
        }

        var options = new FilePickerOpenOptions
        {
            Title = Loc.Tr("ui.custom_build.pick_title"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Executables") { Patterns = ["*.exe", "*.bat", "*.cmd"] }],
        };
        if (await StorageProvider.TryGetFolderFromPathAsync(ViewModel.Folder).ConfigureAwait(true) is { } folder)
        {
            options.SuggestedStartLocation = folder;
        }

        var picked = await StorageProvider.OpenFilePickerAsync(options).ConfigureAwait(true);
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
        {
            // Inside the folder: stored relative. Outside it: kept as typed, so the field shows why it is refused.
            ViewModel.Executable = LibraryLayout.IsSameOrInside(Path.GetFullPath(path), ViewModel.Folder)
                ? Path.GetRelativePath(ViewModel.Folder, path)
                : path;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (e.Key == Key.Escape && !ExecutableBox.IsDropDownOpen)
        {
            e.Handled = true;
            Close(null);
        }
        else if (e.Key is Key.Enter or Key.Return && !ExecutableBox.IsDropDownOpen && ViewModel.CanAccept)
        {
            e.Handled = true;
            Accept();
        }
    }
}
