using System.Runtime.InteropServices;

namespace BlenderLauncher.App.ViewModels.Detail;

/// <summary>
/// Whether Shift is held right now, for actions that Shift changes (Shift-launch opens the previous file, Shift
/// inverts delete and trash, Shift shows the user config folder). Read from the OS at the moment of the click, so it
/// works the same from a button, the detail panel and an open context menu, where Avalonia key events do not reach
/// the window. Upstream also treats Ctrl as Shift while its menu is open; <see cref="IsShiftOrControlDown"/> does that.
/// </summary>
public static partial class ModifierKeys
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;

    /// <summary>A replacement for tests (null: ask the OS).</summary>
    internal static Func<bool>? Override { get; set; }

    /// <summary>True while either Shift key is down.</summary>
    public static bool IsShiftDown => Override?.Invoke() ?? (OperatingSystem.IsWindows() && IsDown(VkShift));

    /// <summary>True while Shift or Ctrl is down (upstream's menu rule for delete versus trash).</summary>
    public static bool IsShiftOrControlDown => Override?.Invoke() ?? (OperatingSystem.IsWindows() && (IsDown(VkShift) || IsDown(VkControl)));

    private static bool IsDown(int key) => (GetKeyState(key) & 0x8000) != 0;

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int virtualKey);
}
