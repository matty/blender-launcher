using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using BlenderLauncher.App.Controls;
using BlenderLauncher.App.Spikes;

namespace BlenderLauncher.App.SelfTest.Checks;

/// <summary>
/// Spike S05: the custom title bar (<see cref="AppTitleBar"/> on a window set up by <see cref="WindowChrome"/>:
/// <c>WindowDecorations.BorderOnly</c> + <c>ExtendClientAreaToDecorationsHint</c>, elements marked with
/// <c>WindowDecorationProperties.ElementRole</c>). Opens a real window briefly (not activated) and checks, through
/// the real <c>WM_NCHITTEST</c> answers and the Win32/DWM rectangles: role elements exist; drag area, caption buttons
/// and all eight resize edges hit-test correctly; the window has a native thick frame (shadow, corners, resize);
/// maximised client area and visible frame equal the work area; restore returns the old bounds; the runtime switch to
/// the system title bar and back works. Snap flyout, resize feel and the look of shadow and corners are manual
/// (<c>--spike titlebar</c>).
/// </summary>
internal sealed class S05TitleBarCheck : ISelfTestCheck
{
    public string Id => "S05";
    public string Name => "Custom title bar";
    public bool NeedsUiThread => true;
    public bool NeedsNetwork => false;
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct)
    {
        var failures = new List<string>();
        var notes = new List<string>();

        var window = new TitleBarSpikeWindow
        {
            ShowLiveReadout = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = 900,
            Height = 600,
        };

        try
        {
            var opened = new TaskCompletionSource();
            window.Opened += (_, _) => opened.TrySetResult();
            window.Show();
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

            // Place it inside the primary work area so maximise and hit tests use a real monitor.
            var primary = window.Screens.Primary ?? window.Screens.All[0];
            window.Position = new PixelPoint(primary.WorkingArea.X + 80, primary.WorkingArea.Y + 80);
            await Settle(ct);

            var hwnd = ChromeNative.GetHandle(window);
            ctx.Log("normal: " + TitleBarProbe.Geometry(window));

            // 1. Role elements.
            var bar = window.TitleBar;
            CheckRole(bar.DragArea, WindowDecorationsElementRole.TitleBar, "drag area", failures);
            CheckRole(bar.MinimiseButton, WindowDecorationsElementRole.MinimizeButton, "minimise", failures);
            CheckRole(bar.MaximiseButton, WindowDecorationsElementRole.MaximizeButton, "maximise", failures);
            CheckRole(bar.CloseButton, WindowDecorationsElementRole.CloseButton, "close", failures);
            if (Math.Abs(bar.Bounds.Height - 34) > 0.01 || Math.Abs(bar.CloseButton.Bounds.Width - 46) > 0.01)
            {
                failures.Add($"size: bar {bar.Bounds.Height} high, close {bar.CloseButton.Bounds.Width} wide");
            }

            // 2. Native frame: thick frame without caption, extended, with invisible resize borders outside the visible frame.
            var style = ChromeNative.Style(hwnd);
            if ((style & ChromeNative.WS_THICKFRAME) == 0 || (style & ChromeNative.WS_CAPTION) == ChromeNative.WS_CAPTION)
            {
                failures.Add($"custom style [{ChromeNative.StyleText(style)}]");
            }

            if (!window.IsExtendedIntoWindowDecorations)
            {
                failures.Add("not extended into decorations");
            }

            var wr = ChromeNative.WindowRect(hwnd);
            var vis = ChromeNative.VisibleFrame(hwnd);
            notes.Add($"resize border L{vis.Left - wr.Left} R{wr.Right - vis.Right} B{wr.Bottom - vis.Bottom} T{vis.Top - wr.Top}");
            if (vis.Left - wr.Left <= 0 || wr.Bottom - vis.Bottom <= 0)
            {
                failures.Add($"no invisible resize border: window {wr}, visible {vis}");
            }

            ProbeAll("normal", window, failures, ctx);

            // 3. Maximise: client area and visible frame equal the work area; title bar flush with its top.
            var normalRect = wr;
            window.WindowState = WindowState.Maximized;
            await Settle(ct);
            ctx.Log("maximised: " + TitleBarProbe.Geometry(window));
            if (TitleBarProbe.MaximisedBoundsError(window, bar) is { } maxError)
            {
                failures.Add("maximised: " + maxError);
            }

            ProbeAll("maximised", window, failures, ctx);

            // 4. Restore returns the previous bounds.
            window.WindowState = WindowState.Normal;
            await Settle(ct);
            var restored = ChromeNative.WindowRect(hwnd);
            if (!restored.Equals(normalRect))
            {
                failures.Add($"restore: {restored} != {normalRect}");
            }

            // 5. Runtime switch to the system title bar.
            var clientBefore = window.ClientSize;
            window.UseSystemTitleBar = true;
            await Settle(ct);
            ctx.Log("system: " + TitleBarProbe.Geometry(window));
            style = ChromeNative.Style(hwnd);
            if ((style & ChromeNative.WS_CAPTION) != ChromeNative.WS_CAPTION || window.IsExtendedIntoWindowDecorations || bar.IsVisible)
            {
                failures.Add($"system mode: style [{ChromeNative.StyleText(style)}], extended {window.IsExtendedIntoWindowDecorations}, bar visible {bar.IsVisible}");
            }

            notes.Add($"system dark mode {ChromeNative.ImmersiveDarkMode(hwnd)?.ToString() ?? "unknown"}");
            if (ChromeNative.ImmersiveDarkMode(hwnd) == false)
            {
                failures.Add("system title bar is not dark");
            }

            notes.Add($"client {clientBefore.Width}x{clientBefore.Height} -> {window.ClientSize.Width}x{window.ClientSize.Height} on switch");
            ProbeAll("system", window, failures, ctx);

            window.WindowState = WindowState.Maximized;
            await Settle(ct);
            var (_, work) = ChromeNative.MonitorRects(hwnd);
            if (!ChromeNative.VisibleFrame(hwnd).Equals(work))
            {
                failures.Add($"system maximised: visible {ChromeNative.VisibleFrame(hwnd)} != work {work}");
            }

            window.WindowState = WindowState.Normal;
            await Settle(ct);

            // 6. And back to the custom title bar.
            window.UseSystemTitleBar = false;
            await Settle(ct);
            ctx.Log("custom again: " + TitleBarProbe.Geometry(window));
            style = ChromeNative.Style(hwnd);
            if ((style & ChromeNative.WS_CAPTION) == ChromeNative.WS_CAPTION || !window.IsExtendedIntoWindowDecorations || !bar.IsVisible)
            {
                failures.Add($"back to custom: style [{ChromeNative.StyleText(style)}], extended {window.IsExtendedIntoWindowDecorations}");
            }

            ProbeAll("custom again", window, failures, ctx);

            window.WindowState = WindowState.Maximized;
            await Settle(ct);
            if (TitleBarProbe.MaximisedBoundsError(window, bar) is { } maxError2)
            {
                failures.Add("custom again maximised: " + maxError2);
            }

            // 7. Minimise and restore, in both modes, starting from Normal and from Maximized.
            foreach (var system in new[] { false, true })
            {
                window.UseSystemTitleBar = system;
                foreach (var start in new[] { WindowState.Normal, WindowState.Maximized })
                {
                    window.WindowState = start;
                    await Settle(ct);
                    var before = ChromeNative.WindowRect(hwnd);
                    window.WindowState = WindowState.Minimized;
                    await Settle(ct);
                    var minimised = window.WindowState;
                    window.WindowState = WindowState.Normal;
                    await Settle(ct);
                    var line = $"{(system ? "system" : "custom")} {start} -> min ({minimised}) -> Normal gives {window.WindowState} {ChromeNative.WindowRect(hwnd)} (was {before})";
                    ctx.Log(line);
                    if (start == WindowState.Normal && (minimised != WindowState.Minimized || window.WindowState != WindowState.Normal))
                    {
                        failures.Add(line);
                    }

                    if (start == WindowState.Maximized)
                    {
                        notes.Add($"{(system ? "system" : "custom")}: Normal after min-from-max gives {window.WindowState}");
                    }
                }
            }

            window.UseSystemTitleBar = false;
        }
        finally
        {
            window.Close();
        }

        var detail = "BorderOnly+ExtendClientAreaToDecorationsHint, ElementRole; " + string.Join("; ", notes);
        return failures.Count == 0
            ? SelfTestResult.Pass(detail)
            : SelfTestResult.Fail(string.Join("; ", failures) + " | " + detail);
    }

    private static void CheckRole(Visual visual, WindowDecorationsElementRole expected, string name, List<string> failures)
    {
        var role = WindowDecorationProperties.GetElementRole(visual);
        if (role != expected)
        {
            failures.Add($"{name} role {role}, want {expected}");
        }
    }

    private static void ProbeAll(string phase, TitleBarSpikeWindow window, List<string> failures, SelfTestContext ctx)
    {
        var probes = TitleBarProbe.HitTests(window, window.TitleBar, window.Body);
        ctx.Log($"{phase} hit tests: {string.Join(' ', probes)}");
        foreach (var p in probes.Where(p => !p.Ok))
        {
            failures.Add($"{phase}: {p}");
        }
    }

    /// <summary>Lets the window procedure, layout and render catch up after a state change.</summary>
    private static Task Settle(CancellationToken ct) => Task.Delay(TimeSpan.FromMilliseconds(500), ct);
}
