using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlenderLauncher.App.DesignData;
using BlenderLauncher.App.Tests.Headless;
using BlenderLauncher.App.Theme;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.App.Views;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.Tests.Shell;

/// <summary>
/// Renders the main window's states at the design's canvas width (1400 × 860) from the sample data and writes
/// each to <c>renders/&lt;state&gt;.png</c> in the test output folder, for comparison with the design by eye. Asserts only
/// that each frame has the expected size and is not blank (the design's surfaces are where they should be).
/// </summary>
public class MainWindowRenderTests
{
    private const int Width = 1400;
    private const int Height = 860;

    [AvaloniaFact]
    public void Main_window_states_render_to_png()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "renders");
        Directory.CreateDirectory(folder);
        var downloads = new SidebarView(SidebarSection.Download, "daily");
        try
        {
            Render(folder, "installed", SampleData.CreateMainWindowViewModel(), new AppearanceSettings());
            Render(folder, "download", SampleData.CreateMainWindowViewModel(downloads), new AppearanceSettings());
            Render(folder, "empty-library", EmptyLibrary(), new AppearanceSettings());
            Render(folder, "compact", SampleData.CreateMainWindowViewModel(), new AppearanceSettings { Density = Density.Compact });
            Render(folder, "orange-accent", SampleData.CreateMainWindowViewModel(downloads), new AppearanceSettings { Accent = Accent.Orange });
        }
        finally
        {
            ThemeManager.Apply(new AppearanceSettings());
        }
    }

    private static MainWindowViewModel EmptyLibrary()
    {
        var shell = new MainWindowViewModel();
        shell.Catalog.LoadSample([], [], @"D:\Blender\builds", DateTimeOffset.Now.AddMinutes(-4));
        return shell;
    }

    private static void Render(string folder, string state, MainWindowViewModel shell, AppearanceSettings appearance)
    {
        ThemeManager.Apply(appearance);
        var window = new MainWindow(shell) { Width = Width, Height = Height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(folder, state + ".png"), new PngBitmapEncoderOptions());
            Assert.Equal(new PixelSize(Width, Height), frame.PixelSize);

            // Not blank: the sidebar, main area and status bar surfaces are drawn, and something else (text, rows) too.
            Assert.Equal(Color.Parse("#212121"), HeadlessRenderTests.PixelAt(frame, 100, 600));
            Assert.Equal(Color.Parse("#2a2a2a"), HeadlessRenderTests.PixelAt(frame, 700, Height - 60));
            Assert.Equal(Color.Parse("#181818"), HeadlessRenderTests.PixelAt(frame, 700, Height - 5));
            var colours = new HashSet<Color>();
            for (var y = 0; y < Height; y += 7)
            {
                for (var x = 0; x < Width; x += 7)
                {
                    colours.Add(HeadlessRenderTests.PixelAt(frame, x, y));
                }
            }

            Assert.True(colours.Count > 50, $"{state}: only {colours.Count} colours; the frame looks blank");
        }
        finally
        {
            window.Close();
        }
    }
}
