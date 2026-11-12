using Avalonia.Controls;

namespace BlenderLauncher.App.Controls;

/// <summary>
/// Switches a window between the app's custom title bar and the system title bar, at any time (settings: "system
/// title bar"). Chosen by spike S5.
/// <para>
/// Custom: <see cref="WindowDecorations.BorderOnly"/> plus <see cref="Window.ExtendClientAreaToDecorationsHint"/>.
/// BorderOnly keeps <c>WS_THICKFRAME</c>, so Windows still provides the resize border on all edges, the DWM shadow,
/// the Windows 11 rounded corners and the correct maximised client area; it has no <c>WS_CAPTION</c>, so Avalonia
/// does not add its own drawn title bar (<c>WindowDrawnDecorations</c> is used only with <c>Full</c>). The title bar
/// itself is <see cref="AppTitleBar"/>, whose elements carry <c>WindowDecorationProperties.ElementRole</c>.
/// <c>WindowDecorations.None</c> would lose the resize border (no <c>WS_THICKFRAME</c>).
/// </para>
/// <para>System: <see cref="WindowDecorations.Full"/> without the hint. Avalonia sets the DWM dark-mode attribute
/// from the theme variant, so the system title bar is dark in the dark theme.</para>
/// </summary>
public static class WindowChrome
{
    /// <summary>
    /// The current "system title bar" setting, for windows opened later (Settings, dialogs). Kept up to date by the
    /// app controller; false (the custom title bar) until then.
    /// </summary>
    public static bool UseSystemTitleBar { get; set; }

    /// <summary>Raised on the UI thread when <see cref="UseSystemTitleBar"/> changes, so open windows can follow it.</summary>
    public static event EventHandler? UseSystemTitleBarChanged;

    /// <summary>Sets <see cref="UseSystemTitleBar"/> and tells open windows.</summary>
    public static void SetUseSystemTitleBar(bool value)
    {
        if (UseSystemTitleBar == value)
        {
            return;
        }

        UseSystemTitleBar = value;
        UseSystemTitleBarChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Applies the current setting to <paramref name="window"/> now and whenever it changes, until the window closes.
    /// </summary>
    public static void Follow(Window window, AppTitleBar titleBar)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(titleBar);
        Apply(window, titleBar, UseSystemTitleBar);
        void OnChanged(object? sender, EventArgs e) => Apply(window, titleBar, UseSystemTitleBar);
        UseSystemTitleBarChanged += OnChanged;
        window.Closed += (_, _) => UseSystemTitleBarChanged -= OnChanged;
    }

    public static void Apply(Window window, AppTitleBar titleBar, bool useSystemTitleBar)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(titleBar);

        if (useSystemTitleBar)
        {
            window.ExtendClientAreaToDecorationsHint = false;
            window.WindowDecorations = WindowDecorations.Full;
            titleBar.IsVisible = false;
        }
        else
        {
            window.WindowDecorations = WindowDecorations.BorderOnly;
            window.ExtendClientAreaToDecorationsHint = true;
            titleBar.IsVisible = true;
        }
    }

    /// <summary>True when the window is currently set up for the custom title bar.</summary>
    public static bool IsCustom(Window window) =>
        window.ExtendClientAreaToDecorationsHint && window.WindowDecorations == WindowDecorations.BorderOnly;
}
