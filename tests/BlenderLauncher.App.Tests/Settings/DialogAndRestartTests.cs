using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using BlenderLauncher.App.Controls;
using BlenderLauncher.App.Lifetime;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.ViewModels.Detail;
using BlenderLauncher.App.Views.Dialogs;
using BlenderLauncher.Core.Updates;

namespace BlenderLauncher.App.Tests.Settings;

/// <summary>Dialog behaviour, the "Restart now" command line and the hotkey text.</summary>
public class DialogAndRestartTests
{
    [Fact]
    public void Dialog_is_centred_on_its_owner_and_kept_on_the_owners_screen()
    {
        var screen = new PixelRect(0, 0, 1920, 1040);
        Assert.Equal(new PixelRect(410, 320, 460, 160), DialogPlacement.Place(new PixelRect(0, 0, 1280, 800), new PixelSize(460, 160), screen));
        // Owner hanging off the right edge: the dialog is pulled back inside.
        Assert.Equal(new PixelRect(1460, 320, 460, 160), DialogPlacement.Place(new PixelRect(1500, 0, 1280, 800), new PixelSize(460, 160), screen));
        // Taller than the screen: shrunk to it.
        Assert.Equal(new PixelRect(410, 0, 460, 1040), DialogPlacement.Place(new PixelRect(0, 0, 1280, 800), new PixelSize(460, 1200), screen));
    }

    [AvaloniaFact]
    public void Escape_chooses_the_cancel_button_and_Enter_the_focused_one()
    {
        var buttons = new[] { new PromptButton("Delete", IsDanger: true), new PromptButton("Cancel", IsPrimary: true, IsCancel: true) };

        var escaped = new DialogWindow(new DialogRequest { Title = "t", Message = "m", Buttons = buttons });
        var escapedResult = escaped.ShowForResultAsync(null);
        escaped.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Equal(1, Completed(escapedResult));

        var entered = new DialogWindow(new DialogRequest { Title = "t", Message = "m", Buttons = buttons });
        var enteredResult = entered.ShowForResultAsync(null);
        entered.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal(1, Completed(enteredResult));

        var text = new DialogWindow(new DialogRequest
        {
            Title = "t",
            Input = "",
            Validate = v => v.Length == 0 ? "empty" : null,
            Buttons = [new PromptButton("OK", IsPrimary: true), new PromptButton("Cancel", IsCancel: true)],
        });
        var textResult = text.ShowForResultAsync(null);
        text.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(textResult.IsCompleted);
        text.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Equal(1, Completed(textResult));
    }

    private static int Completed(Task<int> task)
    {
        Dispatcher.UIThread.RunJobs();
        Assert.True(task.IsCompleted);
        return task.Result;
    }

    [Fact]
    public void Restart_keeps_options_and_drops_commands()
    {
        Assert.Equal(["-debug", "--window-pos", "-3000,-3000", AppRestart.Marker],
            AppRestart.ArgumentsFor(["-debug", "-set-library-folder", "D:\\Lib", "--window-pos", "-3000,-3000", "-force-first-time", "C:\\a.blend"]));
        Assert.Equal(["--offline", AppRestart.Marker], AppRestart.ArgumentsFor(["--offline", "launch", "-v", "4.2", AppRestart.Marker]));
        Assert.Equal(new PixelPoint(-3000, -2000), WindowPositionOverride.Parse(["--window-pos", "-3000,-2000"]));
        Assert.Null(WindowPositionOverride.Parse(["--window-pos", "nowhere"]));
    }

    [Fact]
    public void Self_update_relaunch_drops_its_switches_and_says_where_it_came_from()
    {
        // A restart after an update must not claim to be updated again, nor read the test source.
        Assert.Equal(["--offline", AppRestart.Marker],
            AppRestart.ArgumentsFor(["--offline", "--updated-from", "0.0.9", "--update-source", "C:\\rel"]));

        // The CLI's "update" is a command: dropped, but an off-screen position after it is carried over.
        Assert.Equal(["-debug", AppRestart.Marker, "--window-pos", "-3000,-3000", SelfUpdate.UpdatedFromSwitch, "0.0.9"],
            SelfUpdate.RelaunchArguments(["-debug", "update", "v0.1.0", "--update-source", "C:\\rel", "--window-pos", "-3000,-3000"], "0.0.9"));
        Assert.Equal("0.0.9", SelfUpdate.UpdatedFrom(["--restarted", "--updated-from", "0.0.9"]));
        Assert.Null(SelfUpdate.UpdatedFrom(["--restarted"]));
        Assert.True(SelfUpdate.HasTestSource(["update", "--update-source", "C:\\rel"]));
    }

    [AvaloniaFact]
    public void Update_progress_window_shows_each_step_and_closes_with_the_result()
    {
        var gate = new TaskCompletionSource();
        var run = UpdateProgressWindow.RunAsync(null, "0.2.0", async (progress, ct) =>
        {
            progress.Report(new UpdateProgress(UpdateStage.Downloading, 0.5));
            await gate.Task;
            return new UpdateResult(UpdateOutcome.Failed, "bad");
        });
        // The update delegate reports from a worker thread, so the posted report can land after the first pump.
        UpdateProgressWindow? window = null;
        for (var i = 0; i < 200 && window?.StageDisplay != "Downloading… 50%"; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window = UpdateProgressWindow.Current;
            if (window?.StageDisplay != "Downloading… 50%")
            {
                Thread.Sleep(10);
            }
        }

        Assert.NotNull(window);
        Assert.Equal("Downloading… 50%", window.StageDisplay);

        gate.SetResult();
        for (var i = 0; i < 100 && !run.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Assert.True(run.IsCompleted);
        Assert.Equal(UpdateOutcome.Failed, run.Result.Outcome);
        Assert.False(window.IsVisible);
        Assert.Null(UpdateProgressWindow.Current);
    }
    [Fact]
    public void Hotkey_text_is_lower_case_with_modifiers_in_upstreams_order()
    {
        Assert.Equal("ctrl+alt+f11", HotkeyText.From(Key.F11, KeyModifiers.Alt | KeyModifiers.Control));
        Assert.Equal("ctrl+shift+1", HotkeyText.From(Key.D1, KeyModifiers.Shift | KeyModifiers.Control));
        Assert.Equal("alt+b", HotkeyText.From(Key.B, KeyModifiers.Alt));
        Assert.Equal("shift+/", HotkeyText.From(Key.OemQuestion, KeyModifiers.Shift));
        Assert.Null(HotkeyText.From(Key.LeftCtrl, KeyModifiers.Control));
    }
}
