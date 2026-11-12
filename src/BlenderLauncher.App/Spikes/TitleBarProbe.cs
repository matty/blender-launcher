using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using BlenderLauncher.App.Controls;

namespace BlenderLauncher.App.Spikes;

/// <summary>
/// Measures a window that uses <see cref="AppTitleBar"/>: what Windows' hit test returns at the points that matter,
/// and whether the maximised client area matches the monitor's work area. Shared by the S05 self-test check and the
/// manual <c>--spike titlebar</c> window.
/// </summary>
internal static class TitleBarProbe
{
    public readonly record struct Probe(string Name, int Expected, int Actual)
    {
        public bool Ok => Expected == Actual;

        public override string ToString() =>
            $"{Name}={ChromeNative.HitTestName(Actual)}{(Ok ? "" : $"(want {ChromeNative.HitTestName(Expected)})")}";
    }

    /// <summary>Hit tests for the current mode and state. Call on the UI thread with the window shown and laid out.</summary>
    public static List<Probe> HitTests(Window window, AppTitleBar titleBar, Control body)
    {
        var hwnd = ChromeNative.GetHandle(window);
        var list = new List<Probe>();
        var wr = ChromeNative.WindowRect(hwnd);

        void Add(string name, int expected, PixelPoint p) => list.Add(new Probe(name, expected, ChromeNative.HitTest(hwnd, p.X, p.Y)));
        PixelPoint Centre(Visual v) => v.PointToScreen(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2));

        Add("body", ChromeNative.HTCLIENT, Centre(body));

        if (!WindowChrome.IsCustom(window))
        {
            // System title bar: the strip between the window top and the client top is the native caption.
            var client = ChromeNative.ClientRectOnScreen(hwnd);
            Add("sysCaption", ChromeNative.HTCAPTION, new PixelPoint((client.Left + client.Right) / 2, (wr.Top + client.Top) / 2 + 2));
            return list;
        }

        // The drag area's centre, and a point well inside its left part (over the title text).
        Add("drag", ChromeNative.HTCAPTION, Centre(titleBar.DragArea));
        Add("title", ChromeNative.HTCAPTION, titleBar.DragArea.PointToScreen(new Point(60, titleBar.DragArea.Bounds.Height / 2)));
        Add("min", ChromeNative.HTMINBUTTON, Centre(titleBar.MinimiseButton));
        Add("max", ChromeNative.HTMAXBUTTON, Centre(titleBar.MaximiseButton));
        Add("close", ChromeNative.HTCLOSE, Centre(titleBar.CloseButton));

        if (window.WindowState == WindowState.Maximized)
        {
            // Fitts: the extreme top-right pixel of the work area must still be the close button.
            var (_, work) = ChromeNative.MonitorRects(hwnd);
            Add("cornerClose", ChromeNative.HTCLOSE, new PixelPoint(work.Right - 1, work.Top));
            Add("topEdgeDrag", ChromeNative.HTCAPTION, new PixelPoint((work.Left + work.Right) / 2, work.Top));
        }
        else
        {
            var midX = (wr.Left + wr.Right) / 2;
            var midY = (wr.Top + wr.Bottom) / 2;
            Add("left", ChromeNative.HTLEFT, new PixelPoint(wr.Left + 2, midY));
            Add("right", ChromeNative.HTRIGHT, new PixelPoint(wr.Right - 3, midY));
            Add("top", ChromeNative.HTTOP, new PixelPoint(midX, wr.Top + 1));
            Add("bottom", ChromeNative.HTBOTTOM, new PixelPoint(midX, wr.Bottom - 3));
            Add("topLeft", ChromeNative.HTTOPLEFT, new PixelPoint(wr.Left + 2, wr.Top + 1));
            Add("topRight", ChromeNative.HTTOPRIGHT, new PixelPoint(wr.Right - 3, wr.Top + 1));
            Add("bottomLeft", ChromeNative.HTBOTTOMLEFT, new PixelPoint(wr.Left + 2, wr.Bottom - 3));
            Add("bottomRight", ChromeNative.HTBOTTOMRIGHT, new PixelPoint(wr.Right - 3, wr.Bottom - 3));
        }

        return list;
    }

    /// <summary>One-line description of the window's geometry in physical pixels.</summary>
    public static string Geometry(Window window)
    {
        var hwnd = ChromeNative.GetHandle(window);
        var (monitor, work) = ChromeNative.MonitorRects(hwnd);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"state={window.WindowState} scale={window.RenderScaling:0.##} ");
        sb.Append(CultureInfo.InvariantCulture, $"style=[{ChromeNative.StyleText(ChromeNative.Style(hwnd))}] ");
        sb.Append(CultureInfo.InvariantCulture, $"extended={window.IsExtendedIntoWindowDecorations} decorationMargin={window.WindowDecorationMargin} ");
        sb.Append(CultureInfo.InvariantCulture, $"window={ChromeNative.WindowRect(hwnd)} visible={ChromeNative.VisibleFrame(hwnd)} ");
        sb.Append(CultureInfo.InvariantCulture, $"client={ChromeNative.ClientRectOnScreen(hwnd)} work={work} monitor={monitor}");
        return sb.ToString();
    }

    /// <summary>
    /// For a maximised window: the client area and the visible frame must both equal the work area exactly, so nothing
    /// hangs off the screen and the taskbar is not covered. Returns null when they match, otherwise the mismatch.
    /// </summary>
    public static string? MaximisedBoundsError(Window window, AppTitleBar? titleBar)
    {
        var hwnd = ChromeNative.GetHandle(window);
        var (_, work) = ChromeNative.MonitorRects(hwnd);
        var client = ChromeNative.ClientRectOnScreen(hwnd);
        var visible = ChromeNative.VisibleFrame(hwnd);

        if (!client.Equals(work))
        {
            return $"client {client} != work {work}";
        }

        if (!visible.Equals(work))
        {
            return $"visible frame {visible} != work {work}";
        }

        if (titleBar is { IsVisible: true })
        {
            var top = titleBar.PointToScreen(new Point(0, 0));
            var bottomRight = titleBar.PointToScreen(new Point(titleBar.Bounds.Width, titleBar.Bounds.Height));
            if (top.Y != work.Top || top.X != work.Left || bottomRight.X != work.Right)
            {
                return $"title bar at ({top.X},{top.Y})-({bottomRight.X},{bottomRight.Y}), work {work}";
            }
        }

        return null;
    }
}
