using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using BlenderLauncher.App.DesignData;
using BlenderLauncher.App.Lifetime;
using BlenderLauncher.App.Views;

namespace BlenderLauncher.App;

public partial class App : Application
{
    /// <summary>
    /// False when the process runs without a main window (the self-test). Set before Avalonia starts.
    /// </summary>
    internal static bool CreateMainWindow { get; set; } = true;

    /// <summary>
    /// Replaces the main window, for <c>--spike &lt;name&gt;</c> (see <c>Spikes/ManualSpikes.cs</c>) and the
    /// second-instance notice. Set before Avalonia starts.
    /// </summary>
    internal static Func<Window>? MainWindowFactory { get; set; }

    /// <summary>The normal run's start-up state (log, settings, pipe), from <c>Program.Main</c>. Set before Avalonia starts.</summary>
    internal static AppBootstrap? Bootstrap { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        UseAppIcon();
        if (CreateMainWindow && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (MainWindowFactory is { } factory)
            {
                desktop.MainWindow = factory();
            }
            else if (Bootstrap is { } boot)
            {
                new AppController(desktop, boot).Start();
            }
            else
            {
                // Designer and ad-hoc hosts: the window with the design's sample data, nothing else.
                desktop.MainWindow = new MainWindow(SampleData.CreateMainWindowViewModel());
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The window icon for every window, from the same <c>app.ico</c> that is the exe's icon.</summary>
    private void UseAppIcon()
    {
        try
        {
            using var asset = AssetLoader.Open(new Uri("avares://BlenderLauncher/Assets/Icons/app.ico"));
            using var copy = new MemoryStream();
            asset.CopyTo(copy);
            copy.Position = 0;
            var icon = new WindowIcon(copy);
            Styles.Add(new Style(x => x.Is<Window>()) { Setters = { new Setter(Window.IconProperty, icon) } });
        }
        catch (Exception e)
        {
            // A platform without an icon loader (the headless tests): windows keep the default icon.
            System.Diagnostics.Trace.TraceWarning($"Window icon not set: {e.Message}");
        }
    }
}
