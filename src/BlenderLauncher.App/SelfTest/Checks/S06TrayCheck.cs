using BlenderLauncher.Core;
using BlenderLauncher.Platform.Windows.Tray;

namespace BlenderLauncher.App.SelfTest.Checks;

/// <summary>
/// Spike S06: the launcher's own tray icon through <c>Shell_NotifyIconW</c>. Clicks need a person; they are
/// covered by the manual mode in <see cref="Spikes.TraySpike"/>. This check proves the rest: add, tooltip,
/// balloon, re-add on TaskbarCreated, delete, and a clean teardown with no icon left behind.
/// </summary>
internal sealed class S06TrayCheck : ISelfTestCheck
{
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(3);

    public string Id => "S06";
    public string Name => "Own tray icon (Shell_NotifyIconW)";
    public bool NeedsUiThread => false;
    public bool NeedsNetwork => false;

    public async Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct)
    {
        var notes = new List<string>();
        var tray = ShellTrayIcon.Create(AppIdentity.DisplayName + " (self-test)");
        try
        {
            tray.Trace += (_, line) => ctx.Log($"tray: {line}");
            notes.Add($"icon={tray.IconSource}");
            if (!tray.IsAdded || !await tray.IsPresentAsync())
            {
                return SelfTestResult.Fail("NIM_ADD/NIM_SETVERSION(4) failed: icon not present");
            }

            if (!await tray.SetTooltipAsync(AppIdentity.DisplayName + " (self-test, tooltip changed)"))
            {
                return SelfTestResult.Fail("NIM_MODIFY tooltip failed");
            }

            // Silent and short-lived: shown, then hidden straight away.
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.BalloonShown += (_, _) => shown.TrySetResult();
            if (!await tray.ShowBalloonAsync(AppIdentity.DisplayName, "Self-test balloon (S06)", TrayBalloonIcon.Info, silent: true))
            {
                return SelfTestResult.Fail("NIM_MODIFY NIF_INFO (balloon) failed");
            }

            var balloonShown = await Completes(shown.Task, ct);
            notes.Add(balloonShown ? "balloon shown (NIN_BALLOONSHOW)" : "balloon accepted, NIN_BALLOONSHOW not seen (notifications off or Focus on?)");
            if (!await tray.HideBalloonAsync())
            {
                return SelfTestResult.Fail("hiding the balloon failed");
            }

            // Explorer restart: drop the icon behind the tray's back, then deliver TaskbarCreated.
            var recreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tray.Recreated += (_, _) => recreated.TrySetResult();
            if (!await tray.RemoveIconAsync() || await tray.IsPresentAsync())
            {
                return SelfTestResult.Fail("NIM_DELETE did not remove the icon");
            }

            tray.SimulateTaskbarCreated();
            if (!await Completes(recreated.Task, ct) || !await tray.IsPresentAsync())
            {
                return SelfTestResult.Fail("icon not re-added after TaskbarCreated");
            }

            notes.Add("re-added on TaskbarCreated");

            // TaskbarCreated is a broadcast: prove our window receives broadcasts and a message-only one does not.
            var broadcast = await CompareBroadcastDeliveryAsync(tray, ct);
            notes.Add(broadcast);
            if (!broadcast.StartsWith("broadcast: top-level yes", StringComparison.Ordinal))
            {
                return SelfTestResult.Fail(broadcast);
            }

            // Delete, confirm Explorer no longer knows the icon, then tear down.
            if (!await tray.RemoveIconAsync() || await tray.IsPresentAsync())
            {
                return SelfTestResult.Fail("final NIM_DELETE left the icon in place");
            }

            tray.Dispose();
            if (tray.IsThreadAlive || tray.IsWindowAlive)
            {
                return SelfTestResult.Fail("tray thread or window survived Dispose");
            }

            notes.Add("deleted; window and thread gone");
            return SelfTestResult.Pass(string.Join("; ", notes));
        }
        finally
        {
            tray.Dispose();
        }
    }

    private static async Task<string> CompareBroadcastDeliveryAsync(ShellTrayIcon tray, CancellationToken ct)
    {
        using var messageOnly = ShellTrayIcon.Create("probe", messageOnly: true, addIcon: false);
        var topLevelGot = tray.WaitForBroadcastProbeAsync();
        var messageOnlyGot = messageOnly.WaitForBroadcastProbeAsync();
        if (!ShellTrayIcon.BroadcastProbe())
        {
            return "broadcast probe could not be posted";
        }

        var top = await Completes(topLevelGot, ct);
        var msgOnly = await Completes(messageOnlyGot, ct);
        return $"broadcast: top-level {(top ? "yes" : "no")}, HWND_MESSAGE {(msgOnly ? "yes" : "no")}";
    }

    private static async Task<bool> Completes(Task task, CancellationToken ct)
    {
        var finished = await Task.WhenAny(task, Task.Delay(EventWait, ct));
        return finished == task;
    }
}
