using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BlenderLauncher.App.Controls;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.ViewModels.Detail;
using BlenderLauncher.App.ViewModels.Settings;
using BlenderLauncher.Core;

namespace BlenderLauncher.App.Views.Settings;

/// <summary>
/// The Settings window; layout in the XAML, state in <see cref="SettingsViewModel"/>. Modal over the main
/// window, with the app's title bar (close only) that follows the "system title bar" setting live. Text fields save on
/// leaving them and on Enter; Esc closes. Closing saves what is pending and, when a setting that needs a restart changed,
/// asks "Restart now / Later"; <see cref="RestartRequested"/> carries the answer to the app controller.
/// </summary>
public partial class SettingsWindow : Window, ISettingsHost
{
    private bool _closeConfirmed;

    /// <summary>For the XAML previewer.</summary>
    public SettingsWindow()
    {
        InitializeComponent();
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

        AddHandler(LostFocusEvent, OnFieldLostFocus, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        Closing += OnClosing;
        Opened += (_, _) => DialogPlacement.KeepOnOwnerScreen(this, Owner as Window);
    }

    public SettingsWindow(SettingsViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.Host = this;
    }

    /// <summary>"v0.1.0", at the foot of the tab list.</summary>
    public static string VersionLabel => "v" + AppIdentity.Version;

    public SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    /// <summary>The tabs (General, Appearance, Connection, Blender Builds, About), for the self-test and tests.</summary>
    public TabControl TabControl => Tabs;

    /// <summary>The user chose "Restart now" when the window closed.</summary>
    public bool RestartRequested { get; private set; }

    public async Task<string?> PickFolderAsync(string title, string? startFolder)
    {
        if (!StorageProvider.CanPickFolder)
        {
            return null;
        }

        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (startFolder is not null && Directory.Exists(startFolder)
            && await StorageProvider.TryGetFolderFromPathAsync(startFolder).ConfigureAwait(true) is { } folder)
        {
            options.SuggestedStartLocation = folder;
        }

        var picked = await StorageProvider.OpenFolderPickerAsync(options).ConfigureAwait(true);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    public Task<int> AskAsync(DialogKind kind, string title, string message, IReadOnlyList<PromptButton> buttons) =>
        DialogService.AskAsync(kind, title, message, buttons, this);

    public void Open(string target, bool isFolder)
    {
        if (isFolder)
        {
            DialogService.OpenFolder(target);
        }
        else
        {
            DialogService.OpenWithShell(target);
        }
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox)
        {
            ViewModel?.CommitDrafts();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (e.Key is Key.Enter or Key.Return && e.Source is TextBox)
        {
            ViewModel?.CommitDrafts();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || ViewModel is null)
        {
            return;
        }

        e.Cancel = true;
        _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        var viewModel = ViewModel!;
        viewModel.CommitDrafts();
        var pending = viewModel.PendingRestart();
        if (pending.Count > 0)
        {
            var header = DialogText.Unwrap(Loc.Tr("msg.popup.apply_the_following", ("pending", "")));
            var message = header + "\n\n" + string.Join("\n", pending.Select(p => "•  " + p));
            var answer = await DialogService.ShowAsync(new DialogRequest
            {
                Kind = DialogKind.Question,
                Title = Loc.Tr("ui.settings.restart.title"),
                Message = message,
                KeepLineBreaks = true,
                Buttons =
                [
                    new PromptButton(Loc.Tr("act.restart_now"), IsPrimary: true),
                    new PromptButton(Loc.Tr("act.later"), IsCancel: true),
                ],
            }, this).ConfigureAwait(true);
            RestartRequested = answer == 0;
        }

        _closeConfirmed = true;
        Close();
    }
}
