using System.Globalization;
using Avalonia;
using Avalonia.Controls;

namespace BlenderLauncher.App.Lifetime;

/// <summary>
/// Hidden <c>--window-pos X,Y</c> switch (physical pixels): every window the app opens (the main window, Settings and
/// every dialog) is placed at that point and shown without taking focus. Used to drive and capture the real app
/// off-screen, for example <c>--window-pos -3000,-3000</c>, without anything appearing on the desktop. Placement
/// rules that would pull a window back onto a screen (saved placement, dialog clamping) are skipped while it is set,
/// and the main window's placement is not saved.
/// </summary>
public static class WindowPositionOverride
{
    public const string Switch = "--window-pos";

    /// <summary>The forced position, or null when the switch was not given.</summary>
    public static PixelPoint? Position { get; private set; }

    public static bool IsActive => Position is not null;

    /// <summary>Reads the switch from the command line. Call once in <c>Main</c>.</summary>
    public static void Configure(string[] args) => Position = Parse(args);

    /// <summary>The position after <see cref="Switch"/>, or null when it is absent or malformed.</summary>
    public static PixelPoint? Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], Switch, StringComparison.OrdinalIgnoreCase)
                && args[i + 1].Split(',') is [var x, var y]
                && int.TryParse(x.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var px)
                && int.TryParse(y.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var py))
            {
                return new PixelPoint(px, py);
            }
        }

        return null;
    }

    /// <summary>Places <paramref name="window"/> at the forced position, unactivated. Call before it is shown.</summary>
    public static bool Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (Position is not { } position)
        {
            return false;
        }

        Pin(window, position);
        return true;
    }

    /// <summary>
    /// Places <paramref name="window"/> at <paramref name="position"/> (physical pixels), unactivated, and keeps it there
    /// while it opens, even when that is on no screen. Call before the window is shown. Also used by the self-test.
    /// </summary>
    public static void Pin(Window window, PixelPoint position)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.WindowState = WindowState.Normal;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = position;
        window.ShowActivated = false;

        // Avalonia 12 pulls a window whose position is on no screen back onto the nearest one when it is shown
        // (Window.SetWindowStartupLocation). Every move before the window has opened is undone here, synchronously,
        // so it is never drawn on a screen.
        var opened = false;
        window.PositionChanged += (_, e) =>
        {
            if (!opened && e.Point != position)
            {
                window.Position = position;
            }
        };
        window.Opened += (_, _) =>
        {
            opened = true;
            if (window.Position != position)
            {
                window.Position = position;
            }
        };
    }
}
