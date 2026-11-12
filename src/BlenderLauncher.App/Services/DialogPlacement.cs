using Avalonia;
using Avalonia.Controls;
using BlenderLauncher.App.Lifetime;

namespace BlenderLauncher.App.Services;

/// <summary>Where a dialog goes: centred on its owner, then moved (and if need be shrunk) into the owner's screen.</summary>
public static class DialogPlacement
{
    /// <summary>
    /// The dialog's bounds: centred on <paramref name="owner"/>, then kept inside <paramref name="workingArea"/> (the
    /// working area of the owner's screen). Without a working area it is only centred.
    /// </summary>
    public static PixelRect Place(PixelRect owner, PixelSize dialog, PixelRect? workingArea)
    {
        var x = owner.X + ((owner.Width - dialog.Width) / 2);
        var y = owner.Y + ((owner.Height - dialog.Height) / 2);
        if (workingArea is not { } area || area.Width <= 0 || area.Height <= 0)
        {
            return new PixelRect(x, y, dialog.Width, dialog.Height);
        }

        var width = Math.Min(dialog.Width, area.Width);
        var height = Math.Min(dialog.Height, area.Height);
        x = Math.Clamp(x, area.X, area.Right - width);
        y = Math.Clamp(y, area.Y, area.Bottom - height);
        return new PixelRect(x, y, width, height);
    }

    /// <summary>
    /// Moves an opened <paramref name="window"/> so it is centred on <paramref name="owner"/> and inside the owner's
    /// screen. Call from <see cref="Window.Opened"/> (the size is known then). Does nothing while
    /// <see cref="WindowPositionOverride"/> is active, which places windows off-screen on purpose.
    /// </summary>
    public static void KeepOnOwnerScreen(Window window, Window? owner)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (WindowPositionOverride.IsActive || owner is null)
        {
            return;
        }

        var ownerRect = new PixelRect(owner.Position, PixelSize.FromSize(owner.FrameSize ?? owner.ClientSize, owner.DesktopScaling));
        var size = PixelSize.FromSize(window.FrameSize ?? window.ClientSize, window.DesktopScaling);
        var area = owner.Screens.ScreenFromWindow(owner)?.WorkingArea;
        var placed = Place(ownerRect, size, area);
        if (placed.Position != window.Position)
        {
            window.Position = placed.Position;
        }
    }
}
