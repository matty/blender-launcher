using Avalonia.Controls;
using Avalonia.Media;

namespace BlenderLauncher.App.Spikes;

/// <summary>Spike S7, <c>--spike fonts</c>: the font sample in a normal window, rendered on screen.</summary>
public sealed class FontSpikeWindow : Window
{
    public FontSpikeWindow()
    {
        Title = "Blender Launcher - fonts spike (S7)";
        Width = 900;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#2a2a2a"));
        Content = new ScrollViewer { Content = FontSample.BuildVisual() };
    }
}
