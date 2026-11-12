using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using BlenderLauncher.Platform.Windows.Interop;

namespace BlenderLauncher.Platform.Windows.Hotkeys;

/// <summary>RegisterHotKey modifier flags.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000,
}

/// <summary>The outcome of a registration. On failure <see cref="Error"/> is a sentence for the user.</summary>
public sealed record HotkeyRegistration(bool Success, int Id, int Win32Error, string? Error);

/// <summary>
/// Owns global hot keys on a dedicated thread with its own message loop. A hot key registered with a
/// null window belongs to the registering thread and posts WM_HOTKEY to that thread's queue, so
/// registration, unregistration and delivery all happen here, independent of the UI thread.
/// </summary>
public sealed class GlobalHotkeyThread : IDisposable
{
    private const uint WmRunQueue = HotkeyNative.WmApp + 1;
    private const uint WmStop = HotkeyNative.WmApp + 2;

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly HashSet<int> _registered = [];
    private readonly Thread _thread;
    private readonly TaskCompletionSource<uint> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _threadId;
    private int _nextId = 1;
    private bool _disposed;

    public GlobalHotkeyThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Global hot keys" };
        _thread.Start();
        _threadId = _started.Task.GetAwaiter().GetResult();
    }

    /// <summary>Raised on the hot-key thread with the ID from <see cref="HotkeyRegistration.Id"/>.</summary>
    public event Action<int>? Pressed;

    /// <summary>Registers a hot key. A combination already taken by another program is reported, not thrown.</summary>
    public Task<HotkeyRegistration> RegisterAsync(HotkeyModifiers modifiers, uint virtualKey) =>
        Invoke(() =>
        {
            var id = _nextId++;
            if (HotkeyNative.RegisterHotKey(0, id, (uint)modifiers, virtualKey))
            {
                _registered.Add(id);
                return new HotkeyRegistration(true, id, 0, null);
            }

            var error = Marshal.GetLastPInvokeError();
            var message = error == HotkeyNative.ErrorHotkeyAlreadyRegistered
                ? "This key combination is already in use by another program."
                : new Win32Exception(error).Message;
            return new HotkeyRegistration(false, 0, error, message);
        });

    public Task<bool> UnregisterAsync(int id) =>
        Invoke(() => _registered.Remove(id) && HotkeyNative.UnregisterHotKey(0, id));

    /// <summary>Unregisters everything and stops the thread.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        HotkeyNative.PostThreadMessage(_threadId, WmStop, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private Task<T> Invoke<T>(Func<T> func)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        if (!HotkeyNative.PostThreadMessage(_threadId, WmRunQueue, 0, 0))
        {
            tcs.TrySetException(new Win32Exception(Marshal.GetLastPInvokeError(), "PostThreadMessageW failed"));
        }

        return tcs.Task;
    }

    private void Run()
    {
        // Any message call creates the thread's queue; PostThreadMessage fails until it exists.
        HotkeyNative.PeekMessage(out _, 0, HotkeyNative.WmUser, HotkeyNative.WmUser, HotkeyNative.PmNoRemove);
        _threadId = HotkeyNative.GetCurrentThreadId();
        _started.SetResult(_threadId);

        try
        {
            while (HotkeyNative.GetMessage(out var msg, 0, 0, 0) > 0)
            {
                if (msg.Message == HotkeyNative.WmHotkey)
                {
                    Pressed?.Invoke((int)msg.WParam);
                }
                else if (msg.Message == WmRunQueue)
                {
                    while (_work.TryDequeue(out var item))
                    {
                        item();
                    }
                }
                else if (msg.Message == WmStop)
                {
                    break;
                }
            }
        }
        finally
        {
            foreach (var id in _registered)
            {
                HotkeyNative.UnregisterHotKey(0, id);
            }

            _registered.Clear();
        }
    }
}
