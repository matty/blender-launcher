using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using BlenderLauncher.App.Theme;

[assembly: AvaloniaTestApplication(typeof(BlenderLauncher.App.Tests.Headless.TestAppBuilder))]

namespace BlenderLauncher.App.Tests.Headless;

/// <summary>
/// Builds the real <see cref="App"/> (Fluent theme, dark variant, the design's theme) on the headless platform.
/// Skia does the drawing, so frames can be captured and inspected; HarfBuzz shapes the text and the embedded IBM Plex
/// fonts are registered, as in the exe.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .ConfigureFonts(fonts => fonts.AddFontCollection(new AppFontCollection()))
            .With(AppFonts.CreateFontManagerOptions(CultureInfo.CurrentUICulture))
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
