using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BlenderLauncher.App.Controls;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.App.Views.Dialogs;

/// <summary>
/// The launch picker window; layout in the XAML, state in <see cref="LaunchPickerViewModel"/>. Loads when
/// opened; closes when the view model finishes (launched or cancelled). Enter launches, Esc closes; any key or
/// click stops the launch timer.
/// </summary>
public partial class LaunchPickerWindow : Window
{
    /// <summary>For the XAML previewer.</summary>
    public LaunchPickerWindow()
        : this(new LaunchPickerViewModel(new NoHost(), new LaunchPickerRequest()))
    {
    }

    public LaunchPickerWindow(LaunchPickerViewModel viewModel)
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

        CancelButton.Click += (_, _) => viewModel.Cancel();
        LaunchButton.Click += (_, _) => viewModel.Launch();
        SaveButton.Click += (_, _) => viewModel.SaveQuery();
        BuildList.DoubleTapped += (_, e) =>
        {
            if ((e.Source as Avalonia.StyledElement)?.DataContext is LaunchPickerRow row)
            {
                viewModel.UseRow(row);
            }
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, _) => viewModel.StopCountdown(), RoutingStrategies.Tunnel);
        viewModel.Finished += (_, _) => Close();
        Closed += (_, _) =>
        {
            viewModel.Cancel();
            viewModel.Dispose();
        };
        Opened += async (_, _) =>
        {
            VersionBox.Focus();
            await viewModel.LoadAsync().ConfigureAwait(true);
        };
    }

    public LaunchPickerViewModel ViewModel => (LaunchPickerViewModel)DataContext!;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            ViewModel.Cancel();
            return;
        }

        if (e.Key is Key.Enter or Key.Return && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            ViewModel.Launch();
            return;
        }

        ViewModel.StopCountdown();
    }

    private sealed class NoHost : ILaunchPickerHost
    {
        public Task<LaunchPickerLibrary> LoadAsync(CancellationToken ct) =>
            Task.FromResult(new LaunchPickerLibrary([], null, new Dictionary<string, string>(), -1));

        public void SaveQuery(string versionKey, string queryText)
        {
        }

        public void Launch(BuildInfo build, LaunchMode mode, IReadOnlyList<string> blenderArgs)
        {
        }
    }
}
