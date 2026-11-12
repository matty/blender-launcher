using BlenderLauncher.Platform.Windows.Interop;

namespace BlenderLauncher.Platform.Windows.Processes;

/// <summary>
/// Lets another process bring its window to the front. A second launch (started by the user, so allowed to take the
/// foreground) calls <see cref="AllowAny"/> before handing a request to the running launcher, so the window the
/// running launcher opens for it can be activated instead of only flashing in the taskbar.
/// </summary>
public static class ForegroundRights
{
    private const uint AsfwAny = unchecked((uint)-1);

    /// <summary><c>AllowSetForegroundWindow(ASFW_ANY)</c>; false when this process may not pass the right on.</summary>
    public static bool AllowAny() => WindowNative.AllowSetForegroundWindow(AsfwAny);
}
