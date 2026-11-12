namespace BlenderLauncher.App.ViewModels.Detail;

/// <summary>One button of a <see cref="IBuildActionsUi.AskAsync"/> prompt.</summary>
/// <param name="Text">The button text.</param>
/// <param name="IsPrimary">Drawn in the accent colour and pressed by Enter.</param>
/// <param name="IsDanger">Drawn as a danger button (delete).</param>
/// <param name="IsCancel">Pressed by Escape and by closing the window.</param>
public sealed record PromptButton(string Text, bool IsPrimary = false, bool IsDanger = false, bool IsCancel = false);

/// <summary>
/// What <see cref="BuildActions"/> needs from the window: questions, a text prompt, the clipboard, a save-file
/// picker and opening links. The desktop implementation is <see cref="Views.Detail.DesktopActionsUi"/>; tests pass
/// their own. Its questions go through the dialog service (<see cref="Services.DialogService"/>).
/// </summary>
public interface IBuildActionsUi
{
    /// <summary>Asks a question; returns the index of the chosen button, or the cancel button's when the window is closed.</summary>
    Task<int> AskAsync(string title, string message, IReadOnlyList<PromptButton> buttons);

    /// <summary>Asks for a line of text; null when cancelled.</summary>
    Task<string?> AskTextAsync(string title, string message, string initial, string confirmText);

    /// <summary>Puts <paramref name="text"/> on the clipboard; false when there is no clipboard.</summary>
    Task<bool> SetClipboardTextAsync(string text);

    /// <summary>Asks where to save a file; null when cancelled.</summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string? startFolder);

    /// <summary>Opens a web link or a folder with the shell.</summary>
    void OpenWithShell(string target);

    /// <summary>
    /// The custom build dialog for <paramref name="viewModel"/>'s folder; returns the fields to write, or null
    /// when cancelled (the default, for UIs without the dialog).
    /// </summary>
    Task<BlenderLauncher.Core.Builds.BuildInfo?> EditBuildAsync(CustomBuildViewModel viewModel) =>
        Task.FromResult<BlenderLauncher.Core.Builds.BuildInfo?>(null);
}
