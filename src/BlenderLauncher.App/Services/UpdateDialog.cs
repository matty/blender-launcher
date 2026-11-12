using Avalonia.Controls;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.ViewModels.Detail;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Updates;

namespace BlenderLauncher.App.Services;

/// <summary>
/// The launcher's "update available" dialog: "New version X available!", each newer release's notes flattened
/// to plain text (<see cref="MarkdownText.ToPlainText"/>, done by the release service), and Update / Later. Update runs
/// the self-update (<c>AppController</c>); <see cref="ReleasePage"/> is the fallback when it cannot.
/// </summary>
public static class UpdateDialog
{
    private const string Rule = "────────────";

    /// <summary>
    /// The release to offer after a check: an available update whose tag differs from <paramref name="dismissedTag"/>
    /// (the one already offered), so each new version is offered once. Null when there is nothing to offer.
    /// </summary>
    public static AppRelease? ToOffer(UpdateCheckResult? check, string? dismissedTag) =>
        check is { Status: UpdateCheckStatus.UpdateAvailable, Latest: { } latest }
        && !string.Equals(latest.Tag, dismissedTag, StringComparison.OrdinalIgnoreCase)
            ? latest
            : null;

    /// <summary>The notes, newest first: the tag on its own line, then its notes or "No release notes available.".</summary>
    public static string NotesText(IReadOnlyList<ReleaseNote> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (notes.Count == 0)
        {
            return Loc.Tr("msg.updates.no_release_notes");
        }

        return string.Join("\n\n" + Rule + "\n\n", notes.Select(n =>
            n.Tag + "\n\n" + (string.IsNullOrWhiteSpace(n.PlainText) ? Loc.Tr("msg.updates.no_release_notes") : n.PlainText.Trim())));
    }

    /// <summary>The page Update opens: the release's own page, else the releases list. Null without a repository.</summary>
    public static string? ReleasePage(AppRelease release, string? repo = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        repo ??= AppIdentity.UpdateRepo;
        return string.IsNullOrEmpty(repo) ? null : $"https://github.com/{repo}/releases/tag/{Uri.EscapeDataString(release.Tag)}";
    }

    /// <summary>Shows the dialog; true when the user chose Update (the caller then runs the self-update).</summary>
    public static async Task<bool> ShowAsync(AppRelease release, IReadOnlyList<ReleaseNote> notes, Window? owner = null)
    {
        var answer = await DialogService.ShowAsync(Request(release, notes), owner).ConfigureAwait(true);
        return answer == 0;
    }

    /// <summary>The dialog's content: the version line, the notes in a scrolling box, Update and Later.</summary>
    public static DialogRequest Request(AppRelease release, IReadOnlyList<ReleaseNote> notes)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(notes);
        var message = Loc.Tr("msg.updates.new_version_available", ("version", release.Version.ToString())) + "\n"
            + Loc.Tr("ui.update.current", ("current", AppIdentity.Version)) + " " + Loc.Tr("ui.update.installs");
        return new DialogRequest
        {
            Kind = DialogKind.Info,
            Title = Loc.Tr("ui.update.title"),
            Message = message,
            KeepLineBreaks = true,
            ScrollText = NotesText(notes),
            Width = 560,
            Buttons =
            [
                new PromptButton(Loc.Tr("act.update"), IsPrimary: true),
                new PromptButton(Loc.Tr("act.later"), IsCancel: true),
            ],
        };
    }
}
