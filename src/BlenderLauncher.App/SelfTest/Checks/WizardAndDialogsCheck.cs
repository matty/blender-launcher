using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BlenderLauncher.App.Lifetime;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.App.ViewModels.FirstRun;
using BlenderLauncher.App.Views.Dialogs;
using BlenderLauncher.App.Views.FirstRun;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Core.Updates;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.App.SelfTest.Checks;

/// <summary>
/// Opens the first-run wizard off-screen with in-memory settings, renders each page, then the custom
/// build dialog for a folder holding a stub <c>blender.exe</c> and the update dialog with sample notes, and closes them. Nothing is written outside the work
/// directory: the wizard does not finish. Catches compiled-binding, XAML and template problems that only show under
/// Native AOT. Writes <c>wizard-&lt;n&gt;.png</c>, <c>custom-build.png</c> and <c>update.png</c> to the work directory.
/// </summary>
internal sealed class WizardAndDialogsCheck : ISelfTestCheck
{
    public string Id => "Dialogs";
    public string Name => "First-run wizard, custom build and update dialogs render";
    public bool NeedsUiThread => true;
    public bool NeedsNetwork => false;

    public async Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct)
    {
        var failures = new List<string>();
        var pages = await RenderWizardAsync(ctx, failures, ct);
        await RenderCustomBuildAsync(ctx, failures, ct);
        await RenderUpdateDialogAsync(ctx, ct);
        return failures.Count == 0
            ? SelfTestResult.Pass($"{pages} wizard pages, the custom build and update dialogs and the update progress window rendered")
            : SelfTestResult.Fail(string.Join("; ", failures));
    }

    private static async Task<int> RenderWizardAsync(SelfTestContext ctx, List<string> failures, CancellationToken ct)
    {
        // A small upstream INI in the work directory, so the library page shows the import offer. No credential store
        // is given, so nothing is written to Credential Manager.
        var ini = Path.Combine(ctx.WorkDirectory, "Blender Launcher.ini");
        await File.WriteAllTextAsync(ini, $"library_folder={Path.Combine(ctx.WorkDirectory, "Upstream Library").Replace('\\', '/')}\n" +
            "show_daily_builds=false\n[Internal]\nfirst_time_setup_seen=true\n", ct);
        var store = SettingsStore.InMemory();
        using var viewModel = new FirstRunViewModel(store, new FirstRunEnvironment
        {
            SuggestedLibraryFolder = Path.Combine(ctx.WorkDirectory, "Library"),
            UpstreamIniPath = ini,
            CreateLibraryFolders = false,
        });
        var window = new FirstRunWindow(viewModel);
        var rendered = 0;
        await ShowAsync(window, ct);
        try
        {
            // Imported: upstream's own defaults for keys the INI lacks (Bforartists and UPBGE on), Daily as the INI says.
            var importedShown = viewModel.Settings.Repos.Count(r => r.IsShown);
            viewModel.ImportUpstream = false;
            var freshShown = viewModel.Settings.Repos.Count(r => r.IsShown);
            viewModel.ImportUpstream = true;
            if (importedShown != 4 || freshShown != 3)
            {
                failures.Add($"channels shown: imported {importedShown} (expected 4), new user {freshShown} (expected 3)");
            }

            await viewModel.CheckLibraryNowAsync();
            var count = Enum.GetValues<FirstRunPage>().Length;
            for (var i = 0; i < count; i++)
            {
                await Settle(window);
                if (!window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text is { Length: > 0 }))
                {
                    failures.Add($"wizard page {i}: no text");
                }

                Render(window, Path.Combine(ctx.WorkDirectory, $"wizard-{i + 1}-{(FirstRunPage)i}.png"));
                rendered++;
                if (i < count - 1)
                {
                    if (!viewModel.CanGoNext)
                    {
                        failures.Add($"wizard page {(FirstRunPage)i}: Next disabled ({viewModel.LibraryProblemText})");
                        break;
                    }

                    await viewModel.NextCommand.ExecuteAsync(null);
                }
            }

            ctx.Log($"rendered {rendered} wizard pages to {ctx.WorkDirectory}");
        }
        finally
        {
            await CloseAsync(window, () => viewModel.CancelCommand.Execute(null));
        }

        if (store.Current.Internal.FirstRunSeen)
        {
            failures.Add("cancelling the wizard saved firstRunSeen");
        }

        return rendered;
    }

    private static async Task RenderCustomBuildAsync(SelfTestContext ctx, List<string> failures, CancellationToken ct)
    {
        var folder = Path.Combine(ctx.WorkDirectory, "custom", "My Build");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "blender.exe"), [0x4d, 0x5a], ct);
        var viewModel = new CustomBuildViewModel(folder, null, null) { Version = "4.2.0" };
        if (!viewModel.CanAccept || viewModel.ToBuildInfo() is not { Branch: "My Build", CustomExecutable: null })
        {
            failures.Add($"custom build: Accept {(viewModel.CanAccept ? "enabled" : "disabled")}, branch '{viewModel.ToBuildInfo()?.Branch}'");
        }

        var window = new CustomBuildWindow(viewModel);
        await ShowAsync(window, ct);
        try
        {
            await Settle(window);
            Render(window, Path.Combine(ctx.WorkDirectory, "custom-build.png"));
        }
        finally
        {
            await CloseAsync(window, window.Close);
        }
    }

    private static async Task RenderUpdateDialogAsync(SelfTestContext ctx, CancellationToken ct)
    {
        var release = new AppRelease("v0.2.0", new SemVer(0, 2, 0));
        ReleaseNote[] notes =
        [
            new("v0.2.0", release.Version, false, "", "Highlights\n\n• A first-run wizard\n• The tray icon and notifications"),
            new("v0.1.1", new SemVer(0, 1, 1), false, "", ""),
        ];
        var window = new DialogWindow(UpdateDialog.Request(release, notes));
        await ShowAsync(window, ct);
        try
        {
            await Settle(window);
            Render(window, Path.Combine(ctx.WorkDirectory, "update.png"));
        }
        finally
        {
            await CloseAsync(window, window.Close);
        }

        // The self-update's progress window, part way through a download.
        var progress = new UpdateProgressWindow("0.2.0");
        progress.Report(new UpdateProgress(UpdateStage.Downloading, 0.42));
        await ShowAsync(progress, ct);
        try
        {
            await Settle(progress);
            Render(progress, Path.Combine(ctx.WorkDirectory, "update-progress.png"));
        }
        finally
        {
            await CloseAsync(progress, progress.CloseFinished);
        }
    }

    private static async Task ShowAsync(Window window, CancellationToken ct)
    {
        var opened = new TaskCompletionSource();
        window.Opened += (_, _) => opened.TrySetResult();
        var screens = window.Screens.All;
        WindowPositionOverride.Pin(window, new PixelPoint((screens.Count > 0 ? screens.Min(s => s.Bounds.X) : 0) - 3000, -3000));
        window.ShowInTaskbar = false;
        window.Show();
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    private static async Task CloseAsync(Window window, Action close)
    {
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (window.IsVisible)
        {
            window.Close();
        }

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
    }

    private static async Task Settle(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }

    private static void Render(Window window, string path)
    {
        var size = window.ClientSize;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height), new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }
}
