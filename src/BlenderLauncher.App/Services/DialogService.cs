using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.ViewModels.Detail;
using BlenderLauncher.App.Views.Dialogs;

namespace BlenderLauncher.App.Services;

/// <summary>The icon a dialog shows (upstream's popup types: Info, Success, Warning, Error; plus a question).</summary>
public enum DialogKind
{
    None,
    Info,
    Success,
    Warning,
    Error,
    Question,
}

/// <summary>Everything one dialog shows. Built by the <see cref="DialogService"/> helpers.</summary>
public sealed record DialogRequest
{
    public DialogKind Kind { get; init; }

    /// <summary>The window title (the title bar); not repeated in the body.</summary>
    public required string Title { get; init; }

    /// <summary>The message. Upstream's hard line breaks are joined unless <see cref="KeepLineBreaks"/> is set.</summary>
    public string Message { get; init; } = "";

    /// <summary>Keep single line breaks (lists the caller built itself).</summary>
    public bool KeepLineBreaks { get; init; }

    /// <summary>The buttons, left to right.</summary>
    public required IReadOnlyList<PromptButton> Buttons { get; init; }

    /// <summary>Long text (release notes) in a scrolling box below the message; null for none.</summary>
    public string? ScrollText { get; init; }

    /// <summary>Shows <see cref="ScrollText"/> in the monospaced font without wrapping (command-line help).</summary>
    public bool ScrollTextMonospaced { get; init; }

    /// <summary>A text field's initial value; null for no text field.</summary>
    public string? Input { get; init; }

    /// <summary>Shows the text field's characters as dots.</summary>
    public bool MaskInput { get; init; }

    /// <summary>Checks the text field when the primary button is chosen: a message keeps the dialog open.</summary>
    public Func<string, string?>? Validate { get; init; }

    /// <summary>Collapsed details (an exception's trace).</summary>
    public string? Details { get; init; }

    /// <summary>When set, the footer has an "Open log folder" button for this folder.</summary>
    public string? LogFolder { get; init; }

    public double? Width { get; init; }

    /// <summary>The button Esc and the close button choose: the one marked cancel, else the last.</summary>
    public int CancelIndex => IndexWhere(b => b.IsCancel) is var i and >= 0 ? i : Buttons.Count - 1;

    /// <summary>The button Enter chooses when no button has focus: the one marked primary, else the first.</summary>
    public int PrimaryIndex => IndexWhere(b => b.IsPrimary) is var i and >= 0 ? i : 0;

    private int IndexWhere(Func<PromptButton, bool> predicate)
    {
        for (var i = 0; i < Buttons.Count; i++)
        {
            if (predicate(Buttons[i]))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// The dialog service: info, success, warning, error, confirm, multi-button and text input, each as an awaited
/// result. Every dialog is a <see cref="DialogWindow"/> in the app's theme, modal over its owner (by default the
/// app's topmost visible window: Settings when it is open, else the main window), centred on it and kept on its screen.
/// With no window open the dialog stands alone. Call on the UI thread.
/// </summary>
public static partial class DialogService
{
    /// <summary>A single "Ok" button.</summary>
    public static IReadOnlyList<PromptButton> OkButtons() => [new PromptButton(Loc.Tr("act.ok"), IsPrimary: true, IsCancel: true)];

    /// <summary>Shows <paramref name="request"/> and returns the index of the chosen button.</summary>
    public static Task<int> ShowAsync(DialogRequest request, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new DialogWindow(request).ShowForResultAsync(owner ?? DefaultOwner());
    }

    public static Task InfoAsync(string title, string message, Window? owner = null) =>
        ShowAsync(new DialogRequest { Kind = DialogKind.Info, Title = title, Message = message, Buttons = OkButtons() }, owner);

    public static Task SuccessAsync(string title, string message, Window? owner = null) =>
        ShowAsync(new DialogRequest { Kind = DialogKind.Success, Title = title, Message = message, Buttons = OkButtons() }, owner);

    public static Task WarningAsync(string title, string message, Window? owner = null) =>
        ShowAsync(new DialogRequest { Kind = DialogKind.Warning, Title = title, Message = message, Buttons = OkButtons() }, owner);

    /// <summary>An error, optionally with collapsed <paramref name="details"/> and an "Open log folder" button.</summary>
    public static Task ErrorAsync(string title, string message, string? details = null, string? logFolder = null, Window? owner = null) =>
        ShowAsync(new DialogRequest
        {
            Kind = DialogKind.Error,
            Title = title,
            Message = message,
            Details = details,
            LogFolder = logFolder,
            Buttons = [new PromptButton(Loc.Tr("ui.error.close"), IsPrimary: true, IsCancel: true)],
            Width = details is null ? null : 560,
        }, owner);

    /// <summary>A yes/no question; true for the confirming button. <paramref name="danger"/> draws it as destructive.</summary>
    public static async Task<bool> ConfirmAsync(string title, string message, string? confirmText = null, string? cancelText = null,
        bool danger = false, Window? owner = null)
    {
        var result = await ShowAsync(new DialogRequest
        {
            Kind = danger ? DialogKind.Warning : DialogKind.Question,
            Title = title,
            Message = message,
            Buttons =
            [
                new PromptButton(confirmText ?? Loc.Tr("act.yes"), IsPrimary: !danger, IsDanger: danger),
                new PromptButton(cancelText ?? Loc.Tr("act.no"), IsPrimary: danger, IsCancel: true),
            ],
        }, owner).ConfigureAwait(true);
        return result == 0;
    }

    /// <summary>A question with any number of buttons; returns the chosen index (the cancel button's when closed).</summary>
    public static Task<int> AskAsync(DialogKind kind, string title, string message, IReadOnlyList<PromptButton> buttons, Window? owner = null) =>
        ShowAsync(new DialogRequest { Kind = kind, Title = title, Message = message, Buttons = buttons }, owner);

    /// <summary>Asks for a line of text; null when cancelled. <paramref name="validate"/> keeps the dialog open on a problem.</summary>
    public static async Task<string?> AskTextAsync(string title, string message, string initial, string confirmText,
        Func<string, string?>? validate = null, Window? owner = null)
    {
        var window = new DialogWindow(new DialogRequest
        {
            Title = title,
            Message = message,
            Input = initial,
            Validate = validate,
            Buttons = [new PromptButton(confirmText, IsPrimary: true), new PromptButton(Loc.Tr("act.cancel"), IsCancel: true)],
        });
        var result = await window.ShowForResultAsync(owner ?? DefaultOwner()).ConfigureAwait(true);
        return result == 0 ? window.InputText : null;
    }

    /// <summary>The app's topmost visible window: the last opened that is still visible (Settings over the main window).</summary>
    public static Window? DefaultOwner()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        for (var i = desktop.Windows.Count - 1; i >= 0; i--)
        {
            if (desktop.Windows[i] is { IsVisible: true } window)
            {
                return window;
            }
        }

        return null;
    }

    /// <summary>Opens a web link, file or folder with the shell; failures are traced, not thrown.</summary>
    public static void OpenWithShell(string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Trace.TraceWarning($"Could not open {target}: {e.Message}");
        }
    }

    /// <summary>Creates <paramref name="folder"/> if need be and opens it with the shell.</summary>
    public static void OpenFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning($"Could not create {folder}: {e.Message}");
        }

        OpenWithShell(folder);
    }
}

/// <summary>Message text clean-up for dialogs.</summary>
public static partial class DialogText
{
    /// <summary>
    /// Upstream's messages break lines for its narrow popups ("Are you sure you want to\nsend this build…"); a dialog
    /// wraps text itself, so single breaks become spaces and only blank lines (paragraphs) are kept.
    /// </summary>
    public static string Unwrap(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SingleBreak().Replace(message.Trim().Replace("\r\n", "\n", StringComparison.Ordinal), " ");
    }

    [GeneratedRegex(@"(?<!\n)\n(?!\n)")]
    private static partial Regex SingleBreak();
}
