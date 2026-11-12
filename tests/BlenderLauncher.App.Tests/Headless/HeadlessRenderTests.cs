using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace BlenderLauncher.App.Tests.Headless;

/// <summary>Spike S8: an [AvaloniaFact] runs on the headless platform under dotnet test.</summary>
public class HeadlessRenderTests
{
    [AvaloniaFact]
    public void Window_lays_out_and_renders_a_control()
    {
        var box = new Border
        {
            Width = 40,
            Height = 30,
            Background = Brushes.Red,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var window = new Window
        {
            Width = 200,
            Height = 100,
            Background = Brushes.Blue,
            Content = box,
        };

        window.Show();
        using var frame = window.CaptureRenderedFrame();

        Assert.True(Dispatcher.UIThread.CheckAccess());
        Assert.Equal(new Rect(0, 0, 40, 30), box.Bounds);
        Assert.NotNull(frame);
        Assert.Equal(new PixelSize(200, 100), frame.PixelSize);
        Assert.Equal(Colors.Red, PixelAt(frame, 10, 10));
        Assert.Equal(Colors.Blue, PixelAt(frame, 150, 80));

        window.Close();
    }

    /// <summary>Reads one pixel; the headless framebuffer format is a platform option, so both byte orders are handled.</summary>
    internal static Color PixelAt(WriteableBitmap frame, int x, int y)
    {
        using var buffer = frame.Lock();
        var px = new byte[4];
        Marshal.Copy(buffer.Address + y * buffer.RowBytes + x * 4, px, 0, 4);
        return buffer.Format == PixelFormat.Rgba8888
            ? Color.FromArgb(px[3], px[0], px[1], px[2])
            : Color.FromArgb(px[3], px[2], px[1], px[0]);
    }
}
