using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace BlenderLauncher.App.Controls;

/// <summary>
/// Turns a key press into upstream's hotkey text (<c>quick_launch_key_seq</c>):
/// modifiers in the order ctrl, alt, shift, then the key, joined with <c>+</c> and lower-cased, for
/// example <c>ctrl+alt+f11</c>. The key is the physical key, so with Shift a symbol is its unshifted key (Shift+1 is
/// <c>shift+1</c>, not <c>!</c>), as upstream's remapping intends. Modifier-only presses give null.
/// </summary>
public static class HotkeyText
{
    public static string? From(Key key, KeyModifiers modifiers)
    {
        if (KeyName(key) is not { } name)
        {
            return null;
        }

        var parts = new List<string>(4);
        if ((modifiers & KeyModifiers.Control) != 0)
        {
            parts.Add("ctrl");
        }

        if ((modifiers & KeyModifiers.Alt) != 0)
        {
            parts.Add("alt");
        }

        if ((modifiers & KeyModifiers.Shift) != 0)
        {
            parts.Add("shift");
        }

        parts.Add(name);
        return string.Join('+', parts);
    }

    /// <summary>The key's name in the hotkey text, or null for a modifier or a key that cannot be a hotkey.</summary>
    public static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('a' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F24 => "f" + (key - Key.F1 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        Key.OemMinus or Key.Subtract => "-",
        Key.OemPlus => "=",
        Key.Add => "+",
        Key.Multiply => "*",
        Key.Divide => "/",
        Key.Decimal => ".",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe or Key.OemBackslash => "\\",
        Key.OemTilde => "`",
        Key.Space => "space",
        Key.Tab => "tab",
        Key.Enter => "enter",
        Key.Back => "backspace",
        Key.Delete => "delete",
        Key.Insert => "insert",
        Key.Home => "home",
        Key.End => "end",
        Key.PageUp => "page_up",
        Key.PageDown => "page_down",
        Key.Left => "left",
        Key.Right => "right",
        Key.Up => "up",
        Key.Down => "down",
        Key.Pause => "pause",
        Key.PrintScreen => "print_screen",
        Key.Scroll => "scroll_lock",
        _ => null,
    };
}

/// <summary>
/// The quick-launch hotkey's key-capture field: read-only text that shows <see cref="Hotkey"/>; while focused, the next
/// key combination replaces it (<see cref="HotkeyText"/>). Esc and Tab keep their usual meaning when pressed alone.
/// It has no context menu, as upstream's.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly StyledProperty<string?> HotkeyProperty =
        AvaloniaProperty.Register<HotkeyBox, string?>(nameof(Hotkey), defaultBindingMode: BindingMode.TwoWay);

    static HotkeyBox()
    {
        HotkeyProperty.Changed.AddClassHandler<HotkeyBox>((box, _) => box.Text = box.Hotkey);
    }

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsUndoEnabled = false;
        ContextMenu = null;
        ContextFlyout = null;
        AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel);
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    public string? Hotkey
    {
        get => GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Escape or Key.Tab)
        {
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Shift && e.Key == Key.Tab)
        {
            return;
        }

        e.Handled = true;
        if (HotkeyText.From(e.Key, e.KeyModifiers) is { } text)
        {
            Hotkey = text;
        }
    }
}
