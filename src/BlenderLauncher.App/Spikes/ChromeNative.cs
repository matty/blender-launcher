using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace BlenderLauncher.App.Spikes;

/// <summary>
/// Win32 reads used to verify the title bar (spike S5): the real WM_NCHITTEST answer for a screen point, and the
/// window, client, visible-frame and monitor rectangles in physical pixels. Not needed by the title bar itself.
/// </summary>
internal static partial class ChromeNative
{
    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int HTMINBUTTON = 8;
    public const int HTMAXBUTTON = 9;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;
    public const int HTCLOSE = 20;

    public const long WS_CAPTION = 0x00C00000;
    public const long WS_BORDER = 0x00800000;
    public const long WS_THICKFRAME = 0x00040000;
    public const long WS_SYSMENU = 0x00080000;
    public const long WS_MAXIMIZEBOX = 0x00010000;

    private const uint WM_NCHITTEST = 0x0084;
    private const int GWL_STYLE = -16;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;

        public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    public static nint GetHandle(Window window) => window.TryGetPlatformHandle()?.Handle ?? 0;

    /// <summary>Asks the window procedure what is at a screen point, exactly as Windows does on mouse move.</summary>
    public static int HitTest(nint hwnd, int screenX, int screenY)
    {
        var lParam = (nint)((screenY & 0xFFFF) << 16 | (screenX & 0xFFFF));
        return (int)SendMessageW(hwnd, WM_NCHITTEST, 0, lParam);
    }

    public static Rect WindowRect(nint hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return r;
    }

    /// <summary>The client area in screen pixels.</summary>
    public static Rect ClientRectOnScreen(nint hwnd)
    {
        GetClientRect(hwnd, out var r);
        var p = new Point();
        ClientToScreen(hwnd, ref p);
        return new Rect { Left = p.X, Top = p.Y, Right = p.X + r.Width, Bottom = p.Y + r.Height };
    }

    /// <summary>The visible frame (without the invisible resize borders), as DWM draws it.</summary>
    public static Rect VisibleFrame(nint hwnd)
    {
        DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<Rect>());
        return r;
    }

    public static (Rect Monitor, Rect Work) MonitorRects(nint hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfoW(monitor, ref info);
        return (info.Monitor, info.Work);
    }

    public static long Style(nint hwnd) => GetWindowLongPtrW(hwnd, GWL_STYLE);

    /// <summary>Reads DWMWA_USE_IMMERSIVE_DARK_MODE; null when DWM refuses the query.</summary>
    public static bool? ImmersiveDarkMode(nint hwnd)
    {
        var hr = DwmGetWindowAttributeInt(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, out var value, sizeof(int));
        return hr == 0 ? value != 0 : null;
    }

    public static string StyleText(long style) =>
        $"{((style & WS_CAPTION) == WS_CAPTION ? "CAPTION " : "")}{((style & WS_BORDER) != 0 ? "BORDER " : "")}" +
        $"{((style & WS_THICKFRAME) != 0 ? "THICKFRAME " : "")}{((style & WS_SYSMENU) != 0 ? "SYSMENU " : "")}" +
        $"{((style & WS_MAXIMIZEBOX) != 0 ? "MAXBOX" : "")}".TrimEnd();

    public static string HitTestName(int value) => value switch
    {
        0 => "NOWHERE",
        HTCLIENT => "CLIENT",
        HTCAPTION => "CAPTION",
        HTMINBUTTON => "MINBUTTON",
        HTMAXBUTTON => "MAXBUTTON",
        HTLEFT => "LEFT",
        HTRIGHT => "RIGHT",
        HTTOP => "TOP",
        HTTOPLEFT => "TOPLEFT",
        HTTOPRIGHT => "TOPRIGHT",
        HTBOTTOM => "BOTTOM",
        HTBOTTOMLEFT => "BOTTOMLEFT",
        HTBOTTOMRIGHT => "BOTTOMRIGHT",
        HTCLOSE => "CLOSE",
        _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hWnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint hWnd, ref Point point);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hWnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    private static partial long GetWindowLongPtrW(nint hWnd, int index);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hWnd, uint attribute, out Rect value, int size);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowAttributeInt(nint hWnd, uint attribute, out int value, int size);
}
