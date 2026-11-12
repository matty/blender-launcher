using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using BlenderLauncher.App.Controls;
using BlenderLauncher.App.Lifetime;
using BlenderLauncher.App.Services;

namespace BlenderLauncher.App.Views.Dialogs;

/// <summary>
/// The themed dialog. Built from a <see cref="DialogRequest"/> by <see cref="DialogService"/>; closes with the
/// index of the chosen button (the cancel button's index for Esc and the close button). Enter presses the focused
/// button, or the primary one when focus is elsewhere (for example in the text field, whose value must then pass
/// validation). Placed centred on its owner and kept inside the owner's screen.
/// </summary>
public partial class DialogWindow : Window
{
    private readonly DialogRequest _request;
    private readonly List<Button> _buttons = [];
    private Window? _owner;

    /// <summary>For the XAML previewer.</summary>
    public DialogWindow()
        : this(new DialogRequest { Title = "Dialog", Message = "Message", Buttons = DialogService.OkButtons() })
    {
    }

    public DialogWindow(DialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        InitializeComponent();
        TitleBar.IsDialog = true;
        WindowChrome.Follow(this, TitleBar);
        TitleBarHost.IsVisible = TitleBar.IsVisible;
        TitleBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                TitleBarHost.IsVisible = TitleBar.IsVisible;
            }
        };

        Title = request.Title;
        if (request.Width is { } width)
        {
            Width = width;
        }

        MessageText.Text = request.KeepLineBreaks ? request.Message.Trim() : DialogText.Unwrap(request.Message);
        MessageText.IsVisible = MessageText.Text.Length > 0;
        SetIcon(request.Kind);

        if (request.ScrollText is { } scrollText)
        {
            ScrollTextBlock.Text = scrollText;
            ScrollHost.IsVisible = true;
            if (request.ScrollTextMonospaced && this.TryFindResource("BlMonoFont", out var mono) && mono is Avalonia.Media.FontFamily font)
            {
                ScrollTextBlock.FontFamily = font;
                ScrollTextBlock.FontSize = 11.5;
                ScrollTextBlock.LineHeight = 16;
                ScrollTextBlock.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
                if (ScrollTextBlock.Parent is ScrollViewer viewer)
                {
                    viewer.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
                    viewer.MaxHeight = 460;
                }
            }
        }

        if (request.Input is { } input)
        {
            InputBox.Text = input;
            InputBox.IsVisible = true;
            InputBox.PasswordChar = request.MaskInput ? '•' : default;
            InputBox.TextChanged += (_, _) => InputError.IsVisible = false;
        }

        if (request.Details is { } details)
        {
            DetailsText.Text = details;
            DetailsExpander.IsVisible = true;
        }

        if (request.LogFolder is { } folder)
        {
            ExtraButton.IsVisible = true;
            ExtraButton.Click += (_, _) => DialogService.OpenFolder(folder);
        }

        BuildButtons();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += OnOpened;
    }

    /// <summary>The text field's value (text prompts).</summary>
    public string InputText => InputBox.Text ?? "";

    /// <summary>The index returned when the dialog is closed without a button.</summary>
    public int CancelIndex => _request.CancelIndex;

    /// <summary>Shows the dialog modal over <paramref name="owner"/> (or on its own) and returns the chosen index.</summary>
    public async Task<int> ShowForResultAsync(Window? owner)
    {
        _owner = owner;
        if (owner is null)
        {
            // A dialog with no window to sit over (an error at start-up) needs its own taskbar button.
            ShowInTaskbar = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        WindowPositionOverride.Apply(this);

        if (owner is not null)
        {
            var result = await ShowDialog<object?>(owner).ConfigureAwait(true);
            return result is int index ? index : CancelIndex;
        }

        var closed = new TaskCompletionSource<int>();
        Closed += (_, _) => closed.TrySetResult(_closedWith ?? CancelIndex);
        Show();
        return await closed.Task.ConfigureAwait(true);
    }

    private int? _closedWith;

    private void CloseWith(int index)
    {
        _closedWith = index;
        Close(index);
    }

    private void BuildButtons()
    {
        for (var i = 0; i < _request.Buttons.Count; i++)
        {
            var spec = _request.Buttons[i];
            var index = i;
            var button = new Button
            {
                Content = spec.Text,
                MinWidth = 84,
                Theme = (ControlTheme)this.FindResource(spec.IsDanger ? "BlDangerButton" : spec.IsPrimary ? "BlPrimaryButton" : "BlButton")!,
            };
            button.Click += (_, _) => Choose(index);
            ButtonRow.Children.Add(button);
            _buttons.Add(button);
        }
    }

    private void Choose(int index)
    {
        if (_request.Input is not null && index == _request.PrimaryIndex && _request.Validate is { } validate
            && validate(InputText) is { } problem)
        {
            InputError.Text = problem;
            InputError.IsVisible = true;
            InputBox.Focus();
            return;
        }

        CloseWith(index);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Place();
        if (_request.Input is not null)
        {
            InputBox.Focus();
            InputBox.SelectAll();
        }
        else if (_request.PrimaryIndex is var primary and >= 0 && primary < _buttons.Count)
        {
            // Keyboard users start on the primary (safe) answer; removals make "No" the primary button.
            _buttons[primary].Focus(NavigationMethod.Tab);
        }
    }

    // Centred on the owner, inside the owner's screen. Avalonia's CenterOwner has already centred it; this only
    // moves it when it would hang off the screen.
    private void Place() => DialogPlacement.KeepOnOwnerScreen(this, _owner);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseWith(CancelIndex);
        }
        else if (e.Key is Key.Enter or Key.Return)
        {
            e.Handled = true;
            var focused = FocusManager?.GetFocusedElement();
            var index = focused is Button button ? _buttons.IndexOf(button) : -1;
            if (index < 0 && focused is Button)
            {
                // Another button (Open log folder, the expander): let it handle Enter itself.
                e.Handled = false;
                return;
            }

            Choose(index >= 0 ? index : Math.Max(_request.PrimaryIndex, 0));
        }
    }

    private void SetIcon(DialogKind kind)
    {
        var (glyph, colour) = kind switch
        {
            DialogKind.Info => ("i", Color.Parse("#7aa6e6")),
            DialogKind.Success => ("✓", Color.Parse("#82c784")),
            DialogKind.Warning => ("!", Color.Parse("#e0bb4f")),
            DialogKind.Error => ("!", Color.Parse("#e07a7a")),
            DialogKind.Question => ("?", Color.Parse("#b4b4b4")),
            _ => ("", default),
        };
        if (glyph.Length == 0)
        {
            return;
        }

        IconBadge.IsVisible = true;
        IconBadge.Background = new SolidColorBrush(colour, 0.16);
        IconGlyph.Text = glyph;
        IconGlyph.Foreground = new SolidColorBrush(colour);
    }
}
