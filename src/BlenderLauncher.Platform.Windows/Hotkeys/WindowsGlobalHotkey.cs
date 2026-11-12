using System.Globalization;
using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Platform.Windows.Interop;

namespace BlenderLauncher.Platform.Windows.Hotkeys;

/// <summary>A parsed hotkey: <c>RegisterHotKey</c> modifiers and a virtual-key code.</summary>
public readonly record struct HotkeyChord(HotkeyModifiers Modifiers, uint VirtualKey)
{
    /// <summary>
    /// Parses upstream's hotkey text (<c>quick_launch_key_seq</c>): <c>+</c>-separated, case-insensitive, modifiers
    /// (<c>ctrl</c>, <c>alt</c>, <c>shift</c>, <c>win</c>) then one key, as the key-capture box writes it
    /// (<c>ctrl+alt+f11</c>, <c>shift+1</c>, <c>ctrl++</c>). pynput's <c>&lt;ctrl&gt;</c> form is accepted too.
    /// </summary>
    public static bool TryParse(string? text, out HotkeyChord chord)
    {
        chord = default;
        var t = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(t))
        {
            return false;
        }

        // A trailing "+" key ("ctrl++", or "+" alone) would otherwise split into empty parts.
        string keyName;
        string[] modifierParts;
        if (t == "+")
        {
            keyName = "+";
            modifierParts = [];
        }
        else if (t.EndsWith("++", StringComparison.Ordinal))
        {
            keyName = "+";
            modifierParts = t[..^2].Split('+');
        }
        else
        {
            var parts = t.Split('+');
            keyName = parts[^1];
            modifierParts = parts[..^1];
        }

        var modifiers = HotkeyModifiers.None;
        foreach (var raw in modifierParts)
        {
            var flag = Unwrap(raw) switch
            {
                "ctrl" or "control" or "ctrl_l" or "ctrl_r" => HotkeyModifiers.Control,
                "alt" or "alt_l" or "alt_r" or "alt_gr" => HotkeyModifiers.Alt,
                "shift" or "shift_l" or "shift_r" => HotkeyModifiers.Shift,
                "win" or "cmd" or "super" or "meta" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None,
            };
            if (flag == HotkeyModifiers.None || (modifiers & flag) != 0)
            {
                return false;
            }

            modifiers |= flag;
        }

        if (VirtualKeyFor(Unwrap(keyName)) is not { } vk)
        {
            return false;
        }

        chord = new HotkeyChord(modifiers, vk);
        return true;
    }

    private static string Unwrap(string part) =>
        part.Length > 2 && part[0] == '<' && part[^1] == '>' ? part[1..^1] : part;

    /// <summary>The virtual-key code for a key name of <c>HotkeyText</c> (US layout for the symbol keys).</summary>
    public static uint? VirtualKeyFor(string name)
    {
        if (name.Length == 1)
        {
            var c = name[0];
            if (c is >= 'a' and <= 'z')
            {
                return (uint)(c - 'a' + 'A');
            }

            if (c is >= '0' and <= '9')
            {
                return c;
            }

            return c switch
            {
                '-' => 0xBD, // VK_OEM_MINUS
                '=' => 0xBB, // VK_OEM_PLUS
                '+' => 0x6B, // VK_ADD
                '*' => 0x6A, // VK_MULTIPLY
                '/' => 0xBF, // VK_OEM_2
                '.' => 0xBE, // VK_OEM_PERIOD
                ',' => 0xBC, // VK_OEM_COMMA
                ';' => 0xBA, // VK_OEM_1
                '\'' => 0xDE, // VK_OEM_7
                '[' => 0xDB, // VK_OEM_4
                ']' => 0xDD, // VK_OEM_6
                '\\' => 0xDC, // VK_OEM_5
                '`' => 0xC0, // VK_OEM_3
                _ => null,
            };
        }

        if (name.Length is 2 or 3 && name[0] == 'f'
            && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24)
        {
            return (uint)(0x70 + f - 1);
        }

        return name switch
        {
            "space" => 0x20,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "backspace" => 0x08,
            "delete" => 0x2E,
            "insert" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "page_up" => 0x21,
            "page_down" => 0x22,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "pause" => 0x13,
            "print_screen" => 0x2C,
            "scroll_lock" => 0x91,
            "esc" or "escape" => 0x1B,
            _ => null,
        };
    }
}

/// <summary>
/// The quick-launch hotkey over <see cref="GlobalHotkeyThread"/>, whose thread is started on first use. One
/// combination at a time; a conflict comes back as <see cref="HotkeyFailure.InUse"/>, never as an exception.
/// </summary>
public sealed class WindowsGlobalHotkey : IGlobalHotkey
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GlobalHotkeyThread? _thread;
    private int _id;
    private bool _disposed;

    public bool IsAvailable => true;

    public event EventHandler? Pressed;

    public async Task<HotkeyResult> SetAsync(string? chord)
    {
        HotkeyChord parsed = default;
        if (chord is not null && !HotkeyChord.TryParse(chord, out parsed))
        {
            await ClearAsync().ConfigureAwait(false);
            return new HotkeyResult(HotkeyFailure.Invalid, $"'{chord}' is not a key combination.");
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await UnregisterLockedAsync().ConfigureAwait(false);
            if (chord is null)
            {
                return HotkeyResult.Ok;
            }

            _thread ??= CreateThread();
            var result = await _thread.RegisterAsync(parsed.Modifiers | HotkeyModifiers.NoRepeat, parsed.VirtualKey).ConfigureAwait(false);
            if (result.Success)
            {
                _id = result.Id;
                return HotkeyResult.Ok;
            }

            return new HotkeyResult(
                result.Win32Error == HotkeyNative.ErrorHotkeyAlreadyRegistered ? HotkeyFailure.InUse : HotkeyFailure.Other,
                result.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _thread?.Dispose();
        _thread = null;
    }

    private async Task ClearAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await UnregisterLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UnregisterLockedAsync()
    {
        if (_id != 0 && _thread is not null)
        {
            await _thread.UnregisterAsync(_id).ConfigureAwait(false);
        }

        _id = 0;
    }

    private GlobalHotkeyThread CreateThread()
    {
        var thread = new GlobalHotkeyThread();
        thread.Pressed += id =>
        {
            if (id == _id)
            {
                Pressed?.Invoke(this, EventArgs.Empty);
            }
        };
        return thread;
    }
}
