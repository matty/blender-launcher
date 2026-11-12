using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BlenderLauncher.App.Lifetime;
using BlenderLauncher.App.ViewModels.Settings;
using BlenderLauncher.App.Views.Settings;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.SelfTest.Checks;

/// <summary>
/// Opens the Settings window off-screen with in-memory settings, shows each tab once, renders it to a bitmap
/// and closes the window. Catches compiled-binding, XAML and template problems in the settings pages that only show
/// under Native AOT. Writes <c>settings-&lt;tab&gt;.png</c> to the work directory.
/// </summary>
internal sealed class SettingsWindowCheck : ISelfTestCheck
{
    private const int Width = 960;
    private const int Height = 700;

    public string Id => "SettingsWindow";
    public string Name => "Settings window renders each tab";
    public bool NeedsUiThread => true;
    public bool NeedsNetwork => false;

    public async Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct)
    {
        var failures = new List<string>();
        var store = SettingsStore.InMemory();
        using var viewModel = new SettingsViewModel(store, new InMemoryCredentialStore());
        var window = new SettingsWindow(viewModel)
        {
            Width = Width,
            Height = Height,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };

        var opened = new TaskCompletionSource();
        var closed = new TaskCompletionSource();
        window.Opened += (_, _) => opened.TrySetResult();
        window.Closed += (_, _) => closed.TrySetResult();
        Type[] expected = [typeof(GeneralTab), typeof(AppearanceTab), typeof(ConnectionTab), typeof(BuildsTab), typeof(AboutTab)];
        try
        {
            var screens = window.Screens.All;
            WindowPositionOverride.Pin(window, new PixelPoint((screens.Count > 0 ? screens.Min(s => s.Bounds.X) : 0) - (Width * 3), -3000));
            window.Show();
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

            for (var i = 0; i < expected.Length; i++)
            {
                window.TabControl.SelectedIndex = i;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                window.UpdateLayout();

                var host = window.TabControl.GetVisualDescendants().OfType<ContentPresenter>()
                    .FirstOrDefault(p => p.Name == "PART_SelectedContentHost");
                var page = host?.Child;
                if (page is null || page.GetType() != expected[i])
                {
                    failures.Add($"tab {i}: shows {page?.GetType().Name ?? "nothing"}, expected {expected[i].Name}");
                    continue;
                }

                if (page.Bounds.Width <= 0 || page.Bounds.Height <= 0 || !page.GetVisualDescendants().OfType<TextBlock>().Any())
                {
                    failures.Add($"{expected[i].Name}: empty");
                }

                var size = window.ClientSize;
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height), new Vector(96, 96));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(ctx.WorkDirectory, $"settings-{expected[i].Name}.png"), new PngBitmapEncoderOptions());
            }

            ctx.Log($"rendered {expected.Length} tabs at {Width}x{Height} to {ctx.WorkDirectory}");
        }
        finally
        {
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        return failures.Count == 0
            ? SelfTestResult.Pass($"{expected.Length} tabs rendered")
            : SelfTestResult.Fail(string.Join("; ", failures));
    }
}
