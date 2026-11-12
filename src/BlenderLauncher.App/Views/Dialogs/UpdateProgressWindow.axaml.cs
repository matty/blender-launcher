using System.Globalization;
using Avalonia.Controls;
using BlenderLauncher.App.Controls;
using BlenderLauncher.App.Lifetime;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.Core.Updates;

namespace BlenderLauncher.App.Views.Dialogs;

/// <summary>
/// The self-update's progress window: "Updating to X", the current step with a bar, and Cancel while Velopack
/// is still downloading (nothing is changed until the launcher closes for the install). <see cref="RunAsync"/> shows it,
/// runs the download off the UI thread and closes it when the download ends.
/// </summary>
public partial class UpdateProgressWindow : Window
{
    private readonly CancellationTokenSource _cancel = new();
    private bool _finished;

    /// <summary>For the XAML previewer.</summary>
    public UpdateProgressWindow()
        : this("0.0.0")
    {
    }

    public UpdateProgressWindow(string version)
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

        Title = Loc.Tr("ui.update.progress_title");
        HeadingText.Text = Loc.Tr("ui.update.progress_heading", ("version", version));
        Report(new UpdateProgress(UpdateStage.Downloading, null));
        CancelButton.Click += (_, _) => RequestCancel();
        Closing += (_, e) =>
        {
            // The close button is Cancel; the window itself closes only when the update has ended.
            if (!_finished)
            {
                e.Cancel = true;
                RequestCancel();
            }
        };
    }

    /// <summary>The progress window while <see cref="RunAsync"/> shows one (tests).</summary>
    internal static UpdateProgressWindow? Current { get; private set; }

    /// <summary>The text of the current step, for tests and the self-test.</summary>
    public string StageDisplay => StageText.Text ?? "";

    /// <summary>Shows <paramref name="progress"/>. UI thread only.</summary>
    public void Report(UpdateProgress progress)
    {
        var text = Loc.Tr(progress.Stage == UpdateStage.Downloading ? "ui.update.stage_downloading" : "ui.update.stage_installing");
        if (progress is { Stage: UpdateStage.Downloading, Fraction: { } fraction })
        {
            text += " " + fraction.ToString("P0", CultureInfo.CurrentCulture);
        }

        StageText.Text = text;
        Bar.IsIndeterminate = progress.Fraction is null;
        Bar.Value = progress.Fraction ?? 0;
        CancelButton.IsEnabled = progress.Stage != UpdateStage.Installing && !_cancel.IsCancellationRequested;
    }

    /// <summary>Closes the window once the update has ended (the close button alone only cancels).</summary>
    internal void CloseFinished()
    {
        _finished = true;
        Close();
    }

    private void RequestCancel()
    {
        if (!CancelButton.IsEnabled)
        {
            return;
        }

        CancelButton.IsEnabled = false;
        StageText.Text = Loc.Tr("ui.update.cancelling");
        _cancel.Cancel();
    }

    /// <summary>
    /// Shows the window (modal over <paramref name="owner"/>, or on its own), runs <paramref name="update"/> on the
    /// thread pool with a progress sink and the window's Cancel token, and returns its result once the window is closed.
    /// </summary>
    public static async Task<UpdateResult> RunAsync(Window? owner, string version,
        Func<IProgress<UpdateProgress>, CancellationToken, Task<UpdateResult>> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var window = new UpdateProgressWindow(version);
        if (owner is null)
        {
            window.ShowInTaskbar = true;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        WindowPositionOverride.Apply(window);
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) =>
        {
            Current = null;
            closed.TrySetResult();
        };
        Current = window;
        if (owner is not null)
        {
            _ = window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }

        var progress = new SyncProgress(p => UiDispatcher.Post(() => window.Report(p)));
        UpdateResult result;
        try
        {
            result = await Task.Run(() => update(progress, window._cancel.Token)).ConfigureAwait(true);
        }
        finally
        {
            window.CloseFinished();
        }

        await closed.Task.ConfigureAwait(true);
        window._cancel.Dispose();
        return result;
    }

    private sealed class SyncProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => report(value);
    }
}
