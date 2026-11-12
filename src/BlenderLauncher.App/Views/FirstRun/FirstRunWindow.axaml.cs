using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BlenderLauncher.App.Controls;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.Theme;
using BlenderLauncher.App.ViewModels.FirstRun;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.Views.FirstRun;

/// <summary>
/// The first-run wizard window; layout in the XAML, state in <see cref="FirstRunViewModel"/>. Appearance
/// choices are previewed live (accent, density, title bar). Closing the window is the same as Cancel: the app quits.
/// </summary>
public partial class FirstRunWindow : Window, IFirstRunHost
{
    private bool _closing;

    /// <summary>For the XAML previewer.</summary>
    public FirstRunWindow()
    {
        InitializeComponent();
        // A top-level window with its own taskbar button: minimise, no maximise.
        TitleBar.IsDialog = true;
        TitleBar.MinimiseButton.IsVisible = true;
        WindowChrome.Follow(this, TitleBar);
        TitleBarHost.IsVisible = TitleBar.IsVisible;
        TitleBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                TitleBarHost.IsVisible = TitleBar.IsVisible;
            }
        };

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Closing += OnClosing;
    }

    public FirstRunWindow(FirstRunViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.Host = this;
        viewModel.Draft.Changed += UiDispatcher.Marshal<SettingsChangedEventArgs>(OnDraftChanged);
        viewModel.Completed += (_, _) =>
        {
            _closing = true;
            Close();
        };
    }

    public FirstRunViewModel? ViewModel => DataContext as FirstRunViewModel;

    public async Task<string?> PickFolderAsync(string title, string? startFolder)
    {
        if (!StorageProvider.CanPickFolder)
        {
            return null;
        }

        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (startFolder is not null && await StorageProvider.TryGetFolderFromPathAsync(startFolder).ConfigureAwait(true) is { } folder)
        {
            options.SuggestedStartLocation = folder;
        }

        var picked = await StorageProvider.OpenFolderPickerAsync(options).ConfigureAwait(true);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    // The wizard's appearance page previews its choices on the app (this window included).
    private static void OnDraftChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Appearance == e.Current.Appearance)
        {
            return;
        }

        ThemeManager.Apply(e.Current.Appearance);
        WindowChrome.SetUseSystemTitleBar(e.Current.Appearance.UseSystemTitleBar);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Escape && ViewModel is { IsCommitting: false } vm)
        {
            e.Handled = true;
            vm.CancelCommand.Execute(null);
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing || ViewModel is not { } vm)
        {
            return;
        }

        // The close button is Cancel (upstream: closing the wizard quits); never while the settings are being saved.
        e.Cancel = true;
        if (!vm.IsCommitting)
        {
            vm.CancelCommand.Execute(null);
        }
    }
}
