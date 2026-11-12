using Avalonia;
using Avalonia.Controls;

namespace BlenderLauncher.App.Spikes;

/// <summary>
/// Hidden <c>--spike &lt;name&gt;</c> switch: opens a spike window for a human to check the parts the self-test
/// cannot see (snap flyout, resize feel, shadow, tray behaviour). Each entry is listed explicitly; no reflection.
/// </summary>
internal static class ManualSpikes
{
    private const string Switch = "--spike";

    /// <summary>The spike name after <c>--spike</c>, or null when the switch is absent.</summary>
    public static string? GetRequested(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], Switch, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length ? args[i + 1] : "";
            }
        }

        return null;
    }

    /// <summary>Runs the named spike and returns the process exit code (2 for an unknown name).</summary>
    public static int Run(string name, string[] args) => name.ToLowerInvariant() switch
    {
        "titlebar" => RunWindow(() => new TitleBarSpikeWindow(), args),
        "fonts" => RunWindow(() => new FontSpikeWindow(), args),
        "tray" => TraySpike.Run(args),
        "theme" => RunWindow(() => new ThemeGalleryWindow(args.Contains("--open-menu", StringComparer.OrdinalIgnoreCase)), args),
        _ => 2,
    };

    private static int RunWindow(Func<Window> createWindow, string[] args)
    {
        App.MainWindowFactory = createWindow;
        return Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
}
