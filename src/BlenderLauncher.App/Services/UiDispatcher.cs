using Avalonia.Threading;

namespace BlenderLauncher.App.Services;

/// <summary>
/// Marshals service events to the UI thread (services raise events on worker threads; view models
/// marshal). Code already on the UI thread runs inline, so a handler fired by a UI action is not delayed.
/// </summary>
public static class UiDispatcher
{
    /// <summary>True on the Avalonia UI thread.</summary>
    public static bool IsOnUiThread => Dispatcher.UIThread.CheckAccess();

    /// <summary>Runs <paramref name="action"/> on the UI thread: inline when already there, otherwise queued.</summary>
    public static void Post(Action action, DispatcherPriority? priority = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action, priority ?? DispatcherPriority.Normal);
        }
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread and completes when it has run.</summary>
    public static Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    /// <summary>Runs <paramref name="func"/> on the UI thread and returns its result.</summary>
    public static Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        return Dispatcher.UIThread.CheckAccess()
            ? Task.FromResult(func())
            : Dispatcher.UIThread.InvokeAsync(func).GetTask();
    }

    /// <summary>Wraps an event handler so it always runs on the UI thread.</summary>
    public static EventHandler<T> Marshal<T>(EventHandler<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return (sender, e) => Post(() => handler(sender, e));
    }

    /// <summary>Wraps an <see cref="Action{T}"/> event handler so it always runs on the UI thread.</summary>
    public static Action<T> Marshal<T>(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return value => Post(() => handler(value));
    }

    /// <summary>Wraps a two-argument <see cref="Action{T1, T2}"/> handler so it always runs on the UI thread.</summary>
    public static Action<T1, T2> Marshal<T1, T2>(Action<T1, T2> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return (a, b) => Post(() => handler(a, b));
    }
}
