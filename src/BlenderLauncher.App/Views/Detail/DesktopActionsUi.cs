using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.App.ViewModels.Detail;
using BlenderLauncher.App.Views.Dialogs;
using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.App.Views.Detail;

/// <summary>
/// <see cref="IBuildActionsUi"/> on the desktop: questions go through <see cref="DialogService"/> (modal over the main
/// window); the clipboard and the save picker are the main window's. Without a visible main window (previews, early
/// start-up) questions answer with their cancel button.
/// </summary>
public sealed class DesktopActionsUi : IBuildActionsUi
{
    public static DesktopActionsUi Instance { get; } = new();

    public async Task<int> AskAsync(string title, string message, IReadOnlyList<PromptButton> buttons)
    {
        var request = new DialogRequest { Kind = DialogKind.Question, Title = title, Message = message, Buttons = buttons };
        if (Owner() is not { } owner)
        {
            return request.CancelIndex;
        }

        return await DialogService.ShowAsync(request, owner).ConfigureAwait(true);
    }

    public Task<string?> AskTextAsync(string title, string message, string initial, string confirmText) =>
        Owner() is { } owner
            ? DialogService.AskTextAsync(title, message, initial, confirmText, owner: owner)
            : Task.FromResult<string?>(null);

    public async Task<bool> SetClipboardTextAsync(string text)
    {
        if (Owner()?.Clipboard is not { } clipboard)
        {
            return false;
        }

        await clipboard.SetTextAsync(text).ConfigureAwait(true);
        return true;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string? startFolder)
    {
        if (Owner()?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }

        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
        };
        if (startFolder is not null && await storage.TryGetFolderFromPathAsync(startFolder).ConfigureAwait(true) is { } folder)
        {
            options.SuggestedStartLocation = folder;
        }

        var file = await storage.SaveFilePickerAsync(options).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    public void OpenWithShell(string target) => DialogService.OpenWithShell(target);

    public Task<BuildInfo?> EditBuildAsync(CustomBuildViewModel viewModel) =>
        Owner() is { } owner
            ? new CustomBuildWindow(viewModel).ShowForResultAsync(owner)
            : Task.FromResult<BuildInfo?>(null);

    private static Window? Owner() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } }
            ? DialogService.DefaultOwner()
            : null;
}
