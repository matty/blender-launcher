using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
using BlenderLauncher.Core;
using BlenderLauncher.Platform.Windows.Tray;

namespace BlenderLauncher.App.Spikes;

/// <summary>
/// Manual test for spike S06 (<c>--spike tray</c>): shows the launcher's own tray icon and a small window
/// that logs every event the icon receives. Clicks cannot be automated, so a person runs this.
/// The log is also appended to <c>%TEMP%\blender-launcher-tray-spike.log</c>.
/// </summary>
public static class TraySpike
{
    private const int MenuShow = 1;
    private const int MenuLaunchDefault = 2;
    private const int MenuFavourites = 3;
    private const int MenuBalloon = 4;
    private const int MenuHide = 5;
    private const int MenuQuit = 6;

    private const string Instructions =
        "Tray spike (S06). The icon is in the notification area (it may be under the ^ overflow arrow).\n" +
        "1. Left click the icon: logs 'LEFT click' and brings this window to the front.\n" +
        "2. Middle click the icon: logs 'MIDDLE click (would launch the default build)'.\n" +
        "3. Right click the icon: logs 'RIGHT click' and opens the menu; choose an item. Open it again and click elsewhere: it must close.\n" +
        "4. Choose 'Show balloon' (menu or button), then click the notification: logs 'BALLOON clicked'.\n" +
        "5. Optional: restart Explorer (Task Manager > Windows Explorer > Restart): logs 'icon re-added'.\n" +
        "6. Quit from the menu or close this window; the icon must disappear at once.";

    /// <summary>Runs the manual tray test and returns the process exit code (0 on a normal quit).</summary>
    public static int Run(string[] args)
    {
        App.CreateMainWindow = false;
        var logPath = Path.Combine(Path.GetTempPath(), "blender-launcher-tray-spike.log");
        return Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, lifetime =>
        {
            lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            lifetime.Startup += (_, _) => Start(lifetime, logPath);
        });
    }

    private static void Start(IClassicDesktopStyleApplicationLifetime lifetime, string logPath)
    {
        var log = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            FontFamily = new Avalonia.Media.FontFamily("Consolas"),
        };
        var balloonButton = new Button { Content = "Show balloon" };
        var quitButton = new Button { Content = "Quit" };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { balloonButton, quitButton },
        };
        var header = new StackPanel
        {
            Spacing = 8,
            Children = { new TextBlock { Text = Instructions, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons },
        };
        DockPanel.SetDock(header, Dock.Top);
        var window = new Window
        {
            Title = AppIdentity.DisplayName + " tray spike",
            Width = 760,
            Height = 520,
            Content = new DockPanel { Margin = new Avalonia.Thickness(12), Children = { header, log } },
        };

        void Append(string line)
        {
            var stamped = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}  {line}";
            log.Text += stamped + Environment.NewLine;
            log.CaretIndex = log.Text.Length;
            try
            {
                File.AppendAllText(logPath, stamped + Environment.NewLine);
            }
            catch (IOException)
            {
            }
        }

        // Every tray event arrives on the tray thread; marshal to the UI thread before touching anything.
        void Post(Action action) => Dispatcher.UIThread.Post(action);

        ShellTrayIcon tray;
        try
        {
            tray = ShellTrayIcon.Create(AppIdentity.DisplayName);
        }
        catch (Exception ex)
        {
            Append($"FAILED to create the tray icon: {ex.Message}");
            window.Show();
            window.Closed += (_, _) => lifetime.Shutdown(1);
            return;
        }

        tray.SetMenu(
        [
            new TrayMenuItem(MenuShow, "Show") { IsDefault = true },
            new TrayMenuItem(MenuLaunchDefault, "Blender 4.5.3 (default, dummy)"),
            new TrayMenuItem(MenuFavourites, "Favorites (dummy)"),
            TrayMenuItem.Separator,
            new TrayMenuItem(MenuBalloon, "Show balloon"),
            new TrayMenuItem(MenuHide, "Hide"),
            TrayMenuItem.Separator,
            new TrayMenuItem(MenuQuit, "Quit"),
        ]);

        void Quit()
        {
            Append("quitting");
            tray.Dispose();
            lifetime.Shutdown(0);
        }

        void ShowWindow()
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
        }

        void ShowBalloon() =>
            _ = tray.ShowBalloonAsync(AppIdentity.DisplayName, "Click this notification to test NIN_BALLOONUSERCLICK.")
                .ContinueWith(t => Post(() => Append($"balloon requested: accepted={t.Status == TaskStatus.RanToCompletion && t.Result}")), TaskScheduler.Default);

        tray.Trace += (_, line) => Post(() => Append("  raw: " + line));
        tray.Clicked += (_, e) => Post(() =>
        {
            switch (e.Click)
            {
                case TrayClick.Left:
                    Append($"LEFT click at {e.X},{e.Y}: showing the window");
                    ShowWindow();
                    break;
                case TrayClick.LeftDouble:
                    Append($"LEFT DOUBLE click at {e.X},{e.Y}");
                    break;
                case TrayClick.Middle:
                    Append($"MIDDLE click at {e.X},{e.Y} (would launch the default build)");
                    break;
                case TrayClick.Right:
                    Append($"RIGHT click at {e.X},{e.Y}: menu opening");
                    break;
            }
        });
        tray.MenuItemInvoked += (_, id) => Post(() =>
        {
            Append($"MENU item {id} chosen");
            switch (id)
            {
                case MenuShow:
                    ShowWindow();
                    break;
                case MenuBalloon:
                    ShowBalloon();
                    break;
                case MenuHide:
                    window.Hide();
                    break;
                case MenuQuit:
                    Quit();
                    break;
            }
        });
        tray.BalloonShown += (_, _) => Post(() => Append("balloon shown"));
        tray.BalloonClosed += (_, _) => Post(() => Append("balloon closed without a click"));
        tray.BalloonClicked += (_, _) => Post(() =>
        {
            Append("BALLOON clicked: showing the window");
            ShowWindow();
        });
        tray.Recreated += (_, _) => Post(() => Append("Explorer restarted: icon re-added"));

        balloonButton.Click += (_, _) => ShowBalloon();
        quitButton.Click += (_, _) => Quit();
        window.Closing += (_, _) => tray.Dispose();
        window.Closed += (_, _) => lifetime.Shutdown(0);
        lifetime.Exit += (_, _) => tray.Dispose();

        Append($"tray icon added={tray.IsAdded}, icon source={tray.IconSource}; log file {logPath}");
        window.Show();
    }
}
