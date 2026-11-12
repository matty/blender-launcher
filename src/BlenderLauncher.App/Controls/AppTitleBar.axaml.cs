using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BlenderLauncher.App.Controls;

/// <summary>
/// The app's own title bar: mark, title and the three caption buttons. Place it at the top of a window whose chrome
/// is set up by <see cref="WindowChrome"/>. It follows the host window's title and state on its own.
/// </summary>
public partial class AppTitleBar : UserControl
{
    private Window? _window;
    private IDisposable? _stateSubscription;
    private IDisposable? _titleSubscription;

    public AppTitleBar()
    {
        // The XAML name generator provides the named elements as internal fields (MinimiseButton, MaximiseButton,
        // CloseButton, DragArea, TitleText, MaximiseGlyph, RestoreGlyph); the self-test reads them for hit-test points.
        InitializeComponent();

        MinimiseButton.Click += OnMinimiseClick;
        MaximiseButton.Click += OnMaximiseClick;
        CloseButton.Click += OnCloseClick;
    }

    /// <summary>
    /// For dialogs and the Settings window: only the close button is shown (no minimise or maximise), so the title
    /// bar matches a window that is modal over the main window.
    /// </summary>
    public bool IsDialog
    {
        get => !MinimiseButton.IsVisible;
        set
        {
            MinimiseButton.IsVisible = !value;
            MaximiseButton.IsVisible = !value;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is null)
        {
            return;
        }

        _stateSubscription = _window.GetObservable(Window.WindowStateProperty)
            .Subscribe(new ActionObserver<WindowState>(UpdateMaximiseGlyph));
        _titleSubscription = _window.GetObservable(Window.TitleProperty)
            .Subscribe(new ActionObserver<string?>(title => TitleText.Text = title));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _stateSubscription?.Dispose();
        _titleSubscription?.Dispose();
        _stateSubscription = null;
        _titleSubscription = null;
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateMaximiseGlyph(WindowState state)
    {
        var maximised = state == WindowState.Maximized;
        MaximiseGlyph.IsVisible = !maximised;
        RestoreGlyph.IsVisible = maximised;
        Avalonia.Automation.AutomationProperties.SetName(MaximiseButton,
            Localisation.Loc.Tr(maximised ? "ui.window.restore" : "ui.window.maximise"));
    }

    private void OnMinimiseClick(object? sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.WindowState = WindowState.Minimized;
        }
    }

    private void OnMaximiseClick(object? sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.WindowState = _window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => _window?.Close();

    private sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(T value) => onNext(value);
    }
}
