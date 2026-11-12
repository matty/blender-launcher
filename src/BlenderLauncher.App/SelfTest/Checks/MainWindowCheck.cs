using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using BlenderLauncher.App.DesignData;
using BlenderLauncher.App.Lifetime;
using BlenderLauncher.App.Views;
using BlenderLauncher.App.Views.Detail;
using BlenderLauncher.App.Views.Lists;
using BlenderLauncher.App.Views.Sidebar;
using BlenderLauncher.App.Views.StatusBar;

namespace BlenderLauncher.App.SelfTest.Checks;

/// <summary>
/// Opens the main window off-screen with the design's sample data, renders one frame to a bitmap and
/// closes it. Catches compiled-binding, XAML, resource and template problems that only show under Native AOT: each
/// region must resolve to its view through the typed DataTemplates, and the rendered frame must show the design's
/// surface colours where the regions are. Writes <c>main-window.png</c> to the work directory (kept with <c>--keep-temp</c>).
/// </summary>
internal sealed class MainWindowCheck : ISelfTestCheck
{
    private const int Width = 1400;
    private const int Height = 820;

    public string Id => "MainWindow";
    public string Name => "Main window renders with sample data";
    public bool NeedsUiThread => true;
    public bool NeedsNetwork => false;

    public async Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct)
    {
        var failures = new List<string>();
        var viewModel = SampleData.CreateMainWindowViewModel();
        var window = new MainWindow(viewModel)
        {
            Width = Width,
            Height = Height,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };

        var opened = new TaskCompletionSource();
        window.Opened += (_, _) => opened.TrySetResult();
        try
        {
            WindowPositionOverride.Pin(window, OffScreen(window));
            window.Show();
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            window.UpdateLayout();

            CheckRegion<SidebarPanel>(window, "SidebarRegion", failures);
            CheckRegion<BuildListPanel>(window, "MainRegion", failures);
            CheckRegion<DetailPanel>(window, "DetailRegion", failures);
            CheckRegion<StatusBarPanel>(window, "StatusBarRegion", failures);

            var size = window.ClientSize;
            var pixels = new PixelSize((int)size.Width, (int)size.Height);
            using var bitmap = new RenderTargetBitmap(pixels, new Vector(96, 96));
            bitmap.Render(window);
            var png = Path.Combine(ctx.WorkDirectory, "main-window.png");
            bitmap.Save(png, new PngBitmapEncoderOptions());
            ctx.Log($"rendered {pixels.Width}x{pixels.Height} to {png}; installed rows {viewModel.Catalog.Installed.Count}, available {viewModel.Catalog.Available.Count}");

            // Surfaces at fixed points: sidebar, main area, detail panel, status bar.
            CheckPixel(bitmap, 100, 300, Color.Parse("#212121"), "sidebar", failures);
            CheckPixel(bitmap, pixels.Width / 2, pixels.Height - 120, Color.Parse("#2a2a2a"), "main area", failures);
            CheckPixel(bitmap, pixels.Width - 30, pixels.Height - 120, Color.Parse("#242424"), "detail panel", failures);
            CheckPixel(bitmap, pixels.Width / 2, pixels.Height - 6, Color.Parse("#181818"), "status bar", failures);

            if (viewModel.Catalog.Installed.Count != 7 || viewModel.Catalog.Available.Count != 21)
            {
                failures.Add($"sample data: {viewModel.Catalog.Installed.Count} installed, {viewModel.Catalog.Available.Count} available");
            }
        }
        finally
        {
            window.Close();
        }

        return failures.Count == 0
            ? SelfTestResult.Pass($"{Width}x{Height}, 4 regions, surfaces as designed")
            : SelfTestResult.Fail(string.Join("; ", failures));
    }

    /// <summary>A position left of every screen, so the window never flashes up.</summary>
    private static PixelPoint OffScreen(Window window)
    {
        var screens = window.Screens.All;
        var left = screens.Count > 0 ? screens.Min(s => s.Bounds.X) : 0;
        return new PixelPoint(left - (Width * 3), -3000);
    }

    private static void CheckRegion<TView>(Window window, string name, List<string> failures)
        where TView : Control
    {
        var host = window.FindControl<ContentControl>(name);
        if (host is null)
        {
            failures.Add($"{name}: not found");
            return;
        }

        var presenter = host.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault();
        if (presenter?.Child is not TView view)
        {
            failures.Add($"{name}: shows {presenter?.Child?.GetType().Name ?? "nothing"}, expected {typeof(TView).Name}");
            return;
        }

        if (view.Bounds.Width <= 0 || view.Bounds.Height <= 0)
        {
            failures.Add($"{name}: zero size");
        }
    }

    private static void CheckPixel(RenderTargetBitmap bitmap, int x, int y, Color expected, string what, List<string> failures)
    {
        var actual = PixelAt(bitmap, x, y);
        if (Math.Abs(actual.R - expected.R) > 2 || Math.Abs(actual.G - expected.G) > 2 || Math.Abs(actual.B - expected.B) > 2)
        {
            failures.Add($"{what} at ({x},{y}) is {actual}, expected {expected}");
        }
    }

    private static unsafe Color PixelAt(Bitmap bitmap, int x, int y)
    {
        var px = stackalloc byte[4];
        bitmap.CopyPixels(new PixelRect(x, y, 1, 1), (nint)px, 4, 4);
        return bitmap.Format == PixelFormat.Rgba8888
            ? Color.FromArgb(px[3], px[0], px[1], px[2])
            : Color.FromArgb(px[3], px[2], px[1], px[0]);
    }
}
