namespace BlenderLauncher.Platform.Windows.Tray;

/// <summary>Which click the user made on the tray icon.</summary>
public enum TrayClick
{
    /// <summary>Left click, or Enter/Space on the focused icon (NIN_SELECT / NIN_KEYSELECT).</summary>
    Left,

    /// <summary>Left double click (WM_LBUTTONDBLCLK). A double click also raises <see cref="Left"/> first.</summary>
    LeftDouble,

    /// <summary>Middle button released (WM_MBUTTONUP).</summary>
    Middle,

    /// <summary>Right click, or Shift+F10/the menu key on the focused icon (WM_CONTEXTMENU). The menu follows.</summary>
    Right,
}

/// <summary>The icon shown inside a balloon (toast on Windows 10 and 11).</summary>
public enum TrayBalloonIcon
{
    None,
    Info,
    Warning,
    Error,
}

/// <summary>A click on the tray icon, with the anchor point in screen coordinates.</summary>
public sealed class TrayClickEventArgs(TrayClick click, int x, int y) : EventArgs
{
    public TrayClick Click { get; } = click;

    public int X { get; } = x;

    public int Y { get; } = y;
}

/// <summary>
/// One entry in the tray's popup menu. <see cref="Id"/> must be greater than zero; it is what
/// <see cref="ShellTrayIcon.MenuItemInvoked"/> reports.
/// </summary>
public sealed record TrayMenuItem(int Id, string Text)
{
    public bool IsEnabled { get; init; } = true;

    public bool IsChecked { get; init; }

    /// <summary>Shown in bold, as the item a left click would perform.</summary>
    public bool IsDefault { get; init; }

    public bool IsSeparator { get; private init; }

    public static TrayMenuItem Separator { get; } = new(0, string.Empty) { IsSeparator = true };
}
