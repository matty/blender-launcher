using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlenderLauncher.App.Services;

/// <summary>
/// The status-bar toast: one line of text shown for three seconds; a new toast replaces the current
/// one and restarts the clock. Callable from any thread. Notifications are also routed here while the window is
/// visible (<see cref="Shown"/> is the hook for that and for tests).
/// </summary>
public sealed partial class ToastService : ObservableObject
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(3);

    private DispatcherTimer? _timer;

    /// <summary>The text on screen, or null when no toast is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToast))]
    public partial string? Text { get; private set; }

    public bool HasToast => Text is not null;

    /// <summary>How long a toast stays; <see cref="DefaultDuration"/> unless changed (tests).</summary>
    public TimeSpan Duration { get; set; } = DefaultDuration;

    /// <summary>Raised on the UI thread for each toast shown.</summary>
    public event EventHandler<string>? Shown;

    /// <summary>Shows <paramref name="text"/> in the status bar.</summary>
    public void Show(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        UiDispatcher.Post(() =>
        {
            Text = text;
            _timer ??= CreateTimer();
            _timer.Stop();
            _timer.Interval = Duration;
            _timer.Start();
            Shown?.Invoke(this, text);
        });
    }

    /// <summary>Removes the current toast now.</summary>
    public void Clear() => UiDispatcher.Post(() =>
    {
        _timer?.Stop();
        Text = null;
    });

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = Duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Text = null;
        };
        return timer;
    }
}
