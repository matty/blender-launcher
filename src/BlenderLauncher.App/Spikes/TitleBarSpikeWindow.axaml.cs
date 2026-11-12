using Avalonia.Controls;
using Avalonia.Threading;
using BlenderLauncher.App.Controls;

namespace BlenderLauncher.App.Spikes;

/// <summary>
/// Spike S5 window: the production <see cref="AppTitleBar"/> on a window set up by <see cref="WindowChrome"/>.
/// Used by the S05 self-test check (automated) and by <c>--spike titlebar</c> (manual, with on-screen instructions).
/// </summary>
public partial class TitleBarSpikeWindow : Window
{
    private readonly DispatcherTimer _timer;
    private bool _useSystemTitleBar;

    public TitleBarSpikeWindow()
    {
        InitializeComponent();

        Instructions.Text = InstructionsText;
        WindowChrome.Apply(this, TitleBar, useSystemTitleBar: false);

        ToggleChromeButton.Click += (_, _) => UseSystemTitleBar = !UseSystemTitleBar;
        MaximiseToggleButton.Click += (_, _) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => UpdateReadout());
    }

    /// <summary>When false (the readout timer is off), the window is being driven by the self-test.</summary>
    public bool ShowLiveReadout { get; init; } = true;

    public bool UseSystemTitleBar
    {
        get => _useSystemTitleBar;
        set
        {
            _useSystemTitleBar = value;
            WindowChrome.Apply(this, TitleBar, value);
            ToggleChromeButton.Content = value ? "Switch to custom title bar" : "Switch to system title bar";
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (ShowLiveReadout)
        {
            _timer.Start();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private void UpdateReadout()
    {
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

        var probes = TitleBarProbe.HitTests(this, TitleBar, Body);
        var bounds = WindowState == WindowState.Maximized
            ? "maxBounds=" + (TitleBarProbe.MaximisedBoundsError(this, TitleBar) ?? "OK")
            : "";
        Readout.Text = $"mode={(UseSystemTitleBar ? "system" : "custom")} {bounds}\n" +
                        TitleBarProbe.Geometry(this) + "\nhit tests: " + string.Join(' ', probes);
    }

    private const string InstructionsText = """
        1. Hover the three caption buttons (top right, each 46 px wide). Minimise and maximise turn #2e2e2e; close turns red #c42b1c with a white X. Pressing and dragging off a button must not trigger it.
        2. Windows 11 only: rest the mouse on the maximise button for about a second. The snap-layout flyout must appear under it. Pick a zone and check the window snaps there. (Windows 10 has no flyout; there, check maximise still works.)
        3. Resize from all four edges and all four corners. The cursor must change just outside the visible edge (and on the top edge, inside the title bar's top few pixels), and resizing must feel like a normal window: no lag at the edge, no flicker, no black or white bars.
        4. With the window not maximised, look at the frame: a soft drop shadow on all sides, rounded corners on Windows 11 (square on Windows 10), a 1 px border, and no white strip or line at the top.
        5. Drag the title bar to move the window. Drag it to the top edge of the screen (maximises), and to a side edge (half-screen snap). Drag a maximised window down by its title bar (restores).
        6. Double-click the title bar: maximises. Double-click again: restores to the previous size and place.
        7. Maximise. The title bar must be fully on screen at the very top, the close button must reach the top-right corner pixel (throw the mouse into the corner and click: the window closes - test this last), and the taskbar must stay visible. Readout shows maxBounds=OK.
        8. Keyboard: Win+Up maximises, Win+Down restores and then minimises, Win+Left/Right snap. Click the taskbar button to minimise and restore (with the minimise animation).
        9. Right-click the title bar and press Alt+Space: note whether the system menu appears (it is not expected in custom mode, as the custom frame has no WS_SYSMENU; it should in system mode).
        10. Click 'Switch to system title bar': a native dark title bar replaces the custom one, the window does not jump or resize unexpectedly, and items 3-6 still work. Switch back: the custom bar returns and its buttons, snap flyout and resize still work.
        11. If you have a second monitor with a different scale, drag the window across and repeat item 7 there.
        """;
}
