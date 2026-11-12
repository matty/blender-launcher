using Avalonia;
using Avalonia.Controls;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.Lifetime;

/// <summary>
/// Saves and restores the main window's position, size and maximised state (<c>internal.window</c>). Position is
/// in physical pixels, size in device-independent pixels, as Avalonia uses them. A saved position is used only when the
/// window's top strip would be visible on a screen, and the window is then moved (and if need be shrunk) to lie inside
/// that screen's working area; otherwise it is centred on the primary screen.
/// </summary>
public sealed class WindowPlacementService
{
    /// <summary>How much of the title bar must be on a screen for a saved position to count as visible.</summary>
    public const int MinimumVisibleWidth = 120;

    public const int MinimumVisibleHeight = 30;

    /// <summary>How much of that strip's height must be on the screen.</summary>
    public const int MinimumVisibleStripHeight = 10;

    private readonly Window _window;
    private PixelPoint _normalPosition;
    private Size _normalSize;
    private bool _haveNormal;

    public WindowPlacementService(Window window)
    {
        _window = window;
        window.PositionChanged += (_, _) => RememberNormal();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == TopLevel.ClientSizeProperty)
            {
                RememberNormal();
            }
        };
    }

    /// <summary>Sets size, position and state from <paramref name="placement"/>. Call before the window is shown.</summary>
    public void Restore(WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var width = Math.Max(placement.Width, WindowPlacement.MinimumWidth);
        var height = Math.Max(placement.Height, WindowPlacement.MinimumHeight);
        _window.Width = width;
        _window.Height = height;
        _window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (placement.X is { } x && placement.Y is { } y)
        {
            var areas = _window.Screens.All.Select(s => (s.WorkingArea, s.Scaling)).ToList();
            var scaling = ScalingAt(areas, new PixelPoint(x, y));
            var saved = new PixelRect(x, y, (int)Math.Round(width * scaling), (int)Math.Round(height * scaling));
            if (Clamp(saved, areas.Select(a => a.WorkingArea).ToList()) is { } clamped)
            {
                _window.WindowStartupLocation = WindowStartupLocation.Manual;
                _window.Position = clamped.Position;
                _window.Width = Math.Max(WindowPlacement.MinimumWidth, clamped.Width / scaling);
                _window.Height = Math.Max(WindowPlacement.MinimumHeight, clamped.Height / scaling);
            }
        }

        if (placement.Maximised)
        {
            _window.WindowState = WindowState.Maximized;
        }
    }

    /// <summary>The placement to save: the last normal (not maximised or minimised) bounds and the maximised flag.</summary>
    public WindowPlacement Capture(WindowPlacement previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        RememberNormal();
        var maximised = _window.WindowState == WindowState.Maximized
                        || (_window.WindowState == WindowState.Minimized && previous.Maximised);
        if (!_haveNormal)
        {
            return previous with { Maximised = maximised };
        }

        return previous with
        {
            X = _normalPosition.X,
            Y = _normalPosition.Y,
            Width = (int)Math.Round(_normalSize.Width),
            Height = (int)Math.Round(_normalSize.Height),
            Maximised = maximised,
        };
    }

    /// <summary>
    /// Fits <paramref name="window"/> into the working area that shows its top strip; null when none does (the saved
    /// position is on a screen that is gone). A window larger than that area is shrunk to it.
    /// </summary>
    public static PixelRect? Clamp(PixelRect window, IReadOnlyList<PixelRect> workingAreas)
    {
        ArgumentNullException.ThrowIfNull(workingAreas);
        var strip = new PixelRect(window.X, window.Y, Math.Max(window.Width, 1), MinimumVisibleHeight);
        PixelRect? best = null;
        var bestArea = 0L;
        foreach (var area in workingAreas)
        {
            var overlap = strip.Intersect(area);
            var size = (long)overlap.Width * overlap.Height;
            if (overlap.Width >= Math.Min(MinimumVisibleWidth, window.Width) && overlap.Height >= Math.Min(MinimumVisibleStripHeight, area.Height)
                && size > bestArea)
            {
                best = area;
                bestArea = size;
            }
        }

        if (best is not { } target)
        {
            return null;
        }

        var width = Math.Min(window.Width, target.Width);
        var height = Math.Min(window.Height, target.Height);
        var x = Math.Clamp(window.X, target.X, target.Right - width);
        var y = Math.Clamp(window.Y, target.Y, target.Bottom - height);
        return new PixelRect(x, y, width, height);
    }

    private static double ScalingAt(IReadOnlyList<(PixelRect WorkingArea, double Scaling)> areas, PixelPoint point)
    {
        foreach (var (area, scaling) in areas)
        {
            if (area.Contains(point))
            {
                return scaling;
            }
        }

        return areas.Count > 0 ? areas[0].Scaling : 1;
    }

    private void RememberNormal()
    {
        if (_window.WindowState != WindowState.Normal || !_window.IsVisible)
        {
            return;
        }

        _normalPosition = _window.Position;
        _normalSize = _window.ClientSize;
        _haveNormal = true;
    }
}
