using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using BlenderLauncher.Core;
using static BlenderLauncher.Platform.Windows.Tray.TrayInterop;

namespace BlenderLauncher.Platform.Windows.Tray;

/// <summary>
/// A notification-area icon owned by the launcher, through <c>Shell_NotifyIconW</c> with
/// <c>NOTIFYICON_VERSION_4</c>: left, double, middle and right click, a native popup menu, balloons and
/// balloon clicks, and re-adding the icon when Explorer restarts.
/// </summary>
/// <remarks>
/// <para>
/// Threading: each instance owns a dedicated STA thread that creates a hidden window and runs a Win32
/// message loop. All Win32 calls happen on that thread; the public methods post work to it and can be
/// called from any thread. <b>Every event is raised on the tray thread</b>; the caller marshals to its UI
/// thread (for example with <c>Dispatcher.UIThread.Post</c>). A handler must not block for long, because
/// the icon does not respond while it runs.
/// </para>
/// <para>
/// The window is a hidden top-level tool window, not a message-only (<c>HWND_MESSAGE</c>) window:
/// message-only windows do not receive broadcasts, so they never see <c>TaskbarCreated</c> and the icon
/// would be lost for good when Explorer restarts. The self-test (S06) checks this both ways.
/// </para>
/// </remarks>
public sealed unsafe class ShellTrayIcon : IDisposable
{
    private const uint CallbackMessage = WM_APP + 1;
    private const uint InvokeMessage = WM_APP + 2;
    private const uint IconId = 1;
    private const string ProbeMessageName = AppIdentity.TechnicalId + ".TrayBroadcastProbe";

    private static readonly string ClassName = AppIdentity.TechnicalId + ".TrayWindow";
    private static readonly Lock ClassLock = new();
    private static readonly ConcurrentDictionary<nint, ShellTrayIcon> Windows = new();
    private static int s_classRefs;

    [ThreadStatic]
    private static ShellTrayIcon? t_creating;

    private readonly bool _messageOnly;
    private readonly bool _addIcon;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly uint _taskbarCreatedMessage;
    private readonly uint _probeMessage;
    private Thread? _thread;
    private int _threadId;
    private nint _hwnd;
    private nint _icon;
    private string _tooltip;
    private volatile IReadOnlyList<TrayMenuItem> _menu = [];
    private volatile TaskCompletionSource? _probe;
    private int _disposed;

    private ShellTrayIcon(string tooltip, bool messageOnly, bool addIcon)
    {
        _tooltip = tooltip;
        _messageOnly = messageOnly;
        _addIcon = addIcon;
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        _probeMessage = RegisterWindowMessageW(ProbeMessageName);
    }

    /// <summary>Left, double, middle or right click. Raised on the tray thread.</summary>
    public event EventHandler<TrayClickEventArgs>? Clicked;

    /// <summary>A menu item was chosen; the argument is its <see cref="TrayMenuItem.Id"/>. Raised on the tray thread.</summary>
    public event EventHandler<int>? MenuItemInvoked;

    /// <summary>The user clicked the balloon (NIN_BALLOONUSERCLICK). Raised on the tray thread.</summary>
    public event EventHandler? BalloonClicked;

    /// <summary>The balloon appeared (NIN_BALLOONSHOW). Not raised when notifications are turned off.</summary>
    public event EventHandler? BalloonShown;

    /// <summary>The balloon went away without a click (NIN_BALLOONTIMEOUT or NIN_BALLOONHIDE).</summary>
    public event EventHandler? BalloonClosed;

    /// <summary>Explorer restarted (TaskbarCreated) and the icon was added again. Raised on the tray thread.</summary>
    public event EventHandler? Recreated;

    /// <summary>One line per message received, for logging and the manual spike. Raised on the tray thread.</summary>
    public event EventHandler<string>? Trace;

    /// <summary>Where the icon image came from: "exe" (the running exe's own icon) or "stock" (SIID_APPLICATION).</summary>
    public string IconSource { get; private set; } = "none";

    /// <summary>True while Explorer has the icon, as far as this instance knows.</summary>
    public bool IsAdded { get; private set; }

    internal bool IsWindowAlive => _hwnd != 0 && IsWindow(_hwnd);

    internal bool IsThreadAlive => _thread?.IsAlive == true;

    /// <summary>Starts the tray thread, creates the window and adds the icon. Throws if the window cannot be created.</summary>
    public static ShellTrayIcon Create(string tooltip) => Create(tooltip, messageOnly: false, addIcon: true);

    /// <summary>For the self-test: <paramref name="messageOnly"/> builds an HWND_MESSAGE window to compare against.</summary>
    internal static ShellTrayIcon Create(string tooltip, bool messageOnly, bool addIcon)
    {
        var tray = new ShellTrayIcon(tooltip, messageOnly, addIcon);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => tray.ThreadMain(ready))
        {
            IsBackground = true,
            Name = "Tray icon",
        };
        thread.SetApartmentState(ApartmentState.STA);
        tray._thread = thread;
        thread.Start();
        ready.Task.GetAwaiter().GetResult();
        return tray;
    }

    /// <summary>Replaces the popup menu shown on right click. Item IDs must be greater than zero.</summary>
    public void SetMenu(IReadOnlyList<TrayMenuItem> items)
    {
        foreach (var item in items)
        {
            if (!item.IsSeparator && item.Id <= 0)
            {
                throw new ArgumentException($"Menu item '{item.Text}' has ID {item.Id}; IDs must be greater than zero.", nameof(items));
            }
        }

        _menu = items;
    }

    /// <summary>Changes the hover text (truncated to 127 characters). Returns false if Explorer refused.</summary>
    public Task<bool> SetTooltipAsync(string tooltip) => InvokeAsync(() =>
    {
        _tooltip = tooltip;
        var data = NewData(NIF_TIP | NIF_SHOWTIP);
        CopyTo(tooltip, data.szTip, 128);
        return Shell_NotifyIconW(NIM_MODIFY, &data);
    });

    /// <summary>
    /// Shows a balloon (a toast on Windows 10 and 11, attributed to this app). Returns false if Explorer
    /// refused. Whether it is actually displayed depends on the user's notification settings and Focus.
    /// </summary>
    public Task<bool> ShowBalloonAsync(string title, string text, TrayBalloonIcon icon = TrayBalloonIcon.Info, bool silent = false) =>
        InvokeAsync(() =>
        {
            var data = NewData(NIF_INFO);
            CopyTo(text, data.szInfo, 256);
            CopyTo(title, data.szInfoTitle, 64);
            data.dwInfoFlags = icon switch
            {
                TrayBalloonIcon.Info => NIIF_INFO,
                TrayBalloonIcon.Warning => NIIF_WARNING,
                TrayBalloonIcon.Error => NIIF_ERROR,
                _ => NIIF_NONE,
            };
            if (silent)
            {
                data.dwInfoFlags |= NIIF_NOSOUND;
            }

            return Shell_NotifyIconW(NIM_MODIFY, &data);
        });

    /// <summary>Removes a balloon that is still showing (an NIF_INFO modify with empty text).</summary>
    public Task<bool> HideBalloonAsync() => InvokeAsync(() =>
    {
        var data = NewData(NIF_INFO);
        return Shell_NotifyIconW(NIM_MODIFY, &data);
    });

    /// <summary>Asks Explorer whether the icon exists: NIM_MODIFY fails for an icon it does not know.</summary>
    internal Task<bool> IsPresentAsync() => InvokeAsync(() =>
    {
        var data = NewData(NIF_TIP | NIF_SHOWTIP);
        CopyTo(_tooltip, data.szTip, 128);
        return Shell_NotifyIconW(NIM_MODIFY, &data);
    });

    /// <summary>Deletes the icon but keeps the window and thread, as Explorer crashing would.</summary>
    internal Task<bool> RemoveIconAsync() => InvokeAsync(RemoveIcon);

    /// <summary>Delivers TaskbarCreated to this window only, as if Explorer had restarted.</summary>
    internal bool SimulateTaskbarCreated() => PostMessageW(_hwnd, _taskbarCreatedMessage, 0, 0);

    /// <summary>Arms this window to wait for the broadcast probe; see <see cref="BroadcastProbe"/>.</summary>
    internal Task WaitForBroadcastProbeAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _probe = tcs;
        return tcs.Task;
    }

    /// <summary>
    /// Posts the launcher's private registered message to every top-level window, the same delivery path as
    /// Explorer's TaskbarCreated. Other programs ignore a registered message they did not register.
    /// </summary>
    internal static bool BroadcastProbe() =>
        PostMessageW(HWND_BROADCAST, RegisterWindowMessageW(ProbeMessageName), 0, 0);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Environment.CurrentManagedThreadId == _threadId)
        {
            Shutdown();
            return;
        }

        _queue.Enqueue(Shutdown);
        if (PostMessageW(_hwnd, InvokeMessage, 0, 0))
        {
            _thread?.Join(TimeSpan.FromSeconds(5));
        }
    }

    private void ThreadMain(TaskCompletionSource ready)
    {
        _threadId = Environment.CurrentManagedThreadId;
        var registered = false;
        try
        {
            RegisterWindowClass();
            registered = true;

            t_creating = this;
            _hwnd = CreateWindowExW(
                _messageOnly ? 0 : WS_EX_TOOLWINDOW, ClassName, AppIdentity.DisplayName, WS_POPUP,
                0, 0, 0, 0, _messageOnly ? HWND_MESSAGE : 0, 0, GetModuleHandleW(null), 0);
            t_creating = null;
            if (_hwnd == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowExW failed for the tray window");
            }

            Windows[_hwnd] = this;

            // When the launcher runs elevated, UIPI would otherwise drop Explorer's broadcast.
            ChangeWindowMessageFilterEx(_hwnd, _taskbarCreatedMessage, MSGFLT_ALLOW, 0);

            if (_addIcon)
            {
                LoadIcon();

                // If Explorer is not up yet (start-up at logon) this fails; TaskbarCreated adds it later.
                AddIcon();
            }

            ready.SetResult();
        }
        catch (Exception ex)
        {
            t_creating = null;
            Cleanup(registered);
            ready.SetException(ex);
            return;
        }

        MSG msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        Cleanup(registered);
    }

    private void Cleanup(bool registered)
    {
        if (_hwnd != 0)
        {
            Windows.TryRemove(_hwnd, out _);
            if (IsWindow(_hwnd))
            {
                DestroyWindow(_hwnd);
            }
        }

        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }

        // Anything posted after the loop ended will never run.
        while (_queue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (registered)
        {
            UnregisterWindowClass();
        }
    }

    private void Shutdown()
    {
        RemoveIcon();
        DestroyWindow(_hwnd); // WM_DESTROY posts WM_QUIT, which ends the loop.
    }

    private static void RegisterWindowClass()
    {
        lock (ClassLock)
        {
            if (s_classRefs++ > 0)
            {
                return;
            }

            fixed (char* name = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WndProc,
                    hInstance = GetModuleHandleW(null),
                    lpszClassName = name,
                };
                if (RegisterClassExW(&wc) == 0)
                {
                    s_classRefs--;
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterClassExW failed for the tray window");
                }
            }
        }
    }

    private static void UnregisterWindowClass()
    {
        lock (ClassLock)
        {
            if (--s_classRefs == 0)
            {
                UnregisterClassW(ClassName, GetModuleHandleW(null));
            }
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            var tray = Windows.TryGetValue(hwnd, out var found) ? found : t_creating;
            if (tray is not null && tray.OnMessage(msg, wParam, lParam))
            {
                return 0;
            }
        }
        catch (Exception ex)
        {
            // An exception must not cross the native frame: that would end the process.
            System.Diagnostics.Debug.WriteLine($"Tray window procedure: {ex}");
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private bool OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg == CallbackMessage)
        {
            OnCallback(LoWord(lParam), wParam);
            return true;
        }

        if (msg == InvokeMessage)
        {
            while (_queue.TryDequeue(out var action))
            {
                action();
            }

            return true;
        }

        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return true;
        }

        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            Raise(Trace, "TaskbarCreated: adding the icon again");
            if (_addIcon && _disposed == 0)
            {
                AddIcon();
                Raise(Recreated);
            }

            return true;
        }

        if (msg == _probeMessage && _probeMessage != 0)
        {
            _probe?.TrySetResult();
            return true;
        }

        return false;
    }

    /// <summary>NOTIFYICON_VERSION_4: LOWORD(lParam) is the event, wParam holds the anchor point.</summary>
    private void OnCallback(uint evt, nint wParam)
    {
        var (x, y) = PointFromParam(wParam);
        Raise(Trace, $"{EventName(evt)} at {x},{y}");
        switch (evt)
        {
            case NIN_SELECT:
            case NIN_KEYSELECT:
                Raise(Clicked, new TrayClickEventArgs(TrayClick.Left, x, y));
                break;
            case WM_LBUTTONDBLCLK:
                Raise(Clicked, new TrayClickEventArgs(TrayClick.LeftDouble, x, y));
                break;
            case WM_MBUTTONUP:
                Raise(Clicked, new TrayClickEventArgs(TrayClick.Middle, x, y));
                break;
            case WM_CONTEXTMENU:
                Raise(Clicked, new TrayClickEventArgs(TrayClick.Right, x, y));
                ShowMenu(x, y);
                break;
            case NIN_BALLOONUSERCLICK:
                Raise(BalloonClicked);
                break;
            case NIN_BALLOONSHOW:
                Raise(BalloonShown);
                break;
            case NIN_BALLOONTIMEOUT:
            case NIN_BALLOONHIDE:
                Raise(BalloonClosed);
                break;
        }
    }

    private void ShowMenu(int x, int y)
    {
        var items = _menu;
        if (items.Count == 0)
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    AppendMenuW(menu, MF_SEPARATOR, 0, null);
                    continue;
                }

                var flags = MF_STRING | (item.IsEnabled ? 0 : MF_GRAYED) | (item.IsChecked ? MF_CHECKED : 0);
                AppendMenuW(menu, flags, (nuint)item.Id, item.Text);
                if (item.IsDefault)
                {
                    SetMenuDefaultItem(menu, (uint)item.Id, 0);
                }
            }

            // Without this the menu does not close when the user clicks elsewhere (KB135788).
            SetForegroundWindow(_hwnd);
            var align = GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN;
            var command = TrackPopupMenuEx(
                menu, align | TPM_BOTTOMALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, x, y, _hwnd, 0);

            // ...and without this a second right click may show the menu and close it again at once.
            PostMessageW(_hwnd, WM_NULL, 0, 0);

            Raise(Trace, command == 0 ? "menu dismissed" : $"menu item {command}");
            if (command != 0)
            {
                Raise(MenuItemInvoked, command);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void LoadIcon()
    {
        var exe = Environment.ProcessPath;
        if (exe is not null)
        {
            nint small = 0;
            if (ExtractIconExW(exe, 0, null, &small, 1) > 0 && small != 0)
            {
                _icon = small;
                IconSource = "exe";
                return;
            }
        }

        var info = new SHSTOCKICONINFO { cbSize = (uint)sizeof(SHSTOCKICONINFO) };
        if (SHGetStockIconInfo(SIID_APPLICATION, SHGSI_ICON | SHGSI_SMALLICON, &info) == 0)
        {
            _icon = info.hIcon;
            IconSource = "stock";
        }
    }

    private bool AddIcon()
    {
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        CopyTo(_tooltip, data.szTip, 128);

        // NIM_ADD can time out on a busy Explorer and still add the icon; a successful modify confirms it.
        var ok = Shell_NotifyIconW(NIM_ADD, &data) || Shell_NotifyIconW(NIM_MODIFY, &data);
        if (ok)
        {
            data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
            ok = Shell_NotifyIconW(NIM_SETVERSION, &data);
        }

        IsAdded = ok;
        return ok;
    }

    private bool RemoveIcon()
    {
        if (!_addIcon)
        {
            return false;
        }

        var data = NewData(0);
        var ok = Shell_NotifyIconW(NIM_DELETE, &data);
        IsAdded = false;
        return ok;
    }

    private NOTIFYICONDATAW NewData(uint flags) => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
    };

    private static void CopyTo(string value, char* destination, int capacity)
    {
        var length = Math.Min(value.Length, capacity - 1);
        value.AsSpan(0, length).CopyTo(new Span<char>(destination, capacity));
        destination[length] = '\0';
    }

    private Task<T> InvokeAsync<T>(Func<T> func)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Environment.CurrentManagedThreadId == _threadId)
        {
            return Task.FromResult(func());
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            if (_disposed != 0 && !IsWindow(_hwnd))
            {
                tcs.TrySetException(new ObjectDisposedException(nameof(ShellTrayIcon)));
                return;
            }

            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        if (!PostMessageW(_hwnd, InvokeMessage, 0, 0))
        {
            tcs.TrySetException(new Win32Exception(Marshal.GetLastPInvokeError(), "The tray thread is not running"));
        }

        return tcs.Task;
    }

    private void Raise(EventHandler? handler) => handler?.Invoke(this, EventArgs.Empty);

    private void Raise<T>(EventHandler<T>? handler, T args) => handler?.Invoke(this, args);

    private static string EventName(uint evt) => evt switch
    {
        NIN_SELECT => "NIN_SELECT",
        NIN_KEYSELECT => "NIN_KEYSELECT",
        NIN_BALLOONSHOW => "NIN_BALLOONSHOW",
        NIN_BALLOONHIDE => "NIN_BALLOONHIDE",
        NIN_BALLOONTIMEOUT => "NIN_BALLOONTIMEOUT",
        NIN_BALLOONUSERCLICK => "NIN_BALLOONUSERCLICK",
        NIN_POPUPOPEN => "NIN_POPUPOPEN",
        NIN_POPUPCLOSE => "NIN_POPUPCLOSE",
        WM_CONTEXTMENU => "WM_CONTEXTMENU",
        WM_LBUTTONDOWN => "WM_LBUTTONDOWN",
        WM_LBUTTONUP => "WM_LBUTTONUP",
        WM_LBUTTONDBLCLK => "WM_LBUTTONDBLCLK",
        WM_RBUTTONDOWN => "WM_RBUTTONDOWN",
        WM_RBUTTONUP => "WM_RBUTTONUP",
        WM_MBUTTONDOWN => "WM_MBUTTONDOWN",
        WM_MBUTTONUP => "WM_MBUTTONUP",
        _ => $"0x{evt:X4}",
    };
}
