using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.Views.Dialogs;
using BlenderLauncher.Core;
using BlenderLauncher.Platform.Windows.Ipc;

namespace BlenderLauncher.App.Lifetime;

/// <summary>What a second launch asks the running instance to do.</summary>
public enum InstanceCommand
{
    /// <summary>Bring the window to the front (restore it from the tray or minimised).</summary>
    Show,

    /// <summary>Open a <c>.blend</c> file (a bare path on the command line): the running instance opens the launch picker.</summary>
    Open,
}

/// <summary>One request over the instance pipe.</summary>
public sealed record InstanceRequest(InstanceCommand Command, string Version, string? Path = null);

/// <summary>
/// The one-line protocol on the instance pipe (<see cref="InstancePipeServer"/>): a request is
/// <c>show&lt;TAB&gt;version</c> or <c>open&lt;TAB&gt;version&lt;TAB&gt;path</c>; the reply is
/// <c>ok&lt;TAB&gt;version</c> with the running instance's version, so the second instance can show upstream's
/// "different version" notice (<c>msg.popup.blver_mismatch</c>).
/// </summary>
public static class InstanceProtocol
{
    private const char Separator = '\t';

    public static string Format(InstanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var command = request.Command == InstanceCommand.Open ? "open" : "show";
        return request.Path is null
            ? $"{command}{Separator}{request.Version}"
            : $"{command}{Separator}{request.Version}{Separator}{request.Path}";
    }

    public static bool TryParse(string? line, out InstanceRequest request)
    {
        request = new InstanceRequest(InstanceCommand.Show, "");
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        var parts = line.Split(Separator, 3);
        var version = parts.Length > 1 ? parts[1] : "";
        switch (parts[0])
        {
            case "show":
                request = new InstanceRequest(InstanceCommand.Show, version);
                return true;
            case "open" when parts.Length == 3 && parts[2].Length > 0:
                request = new InstanceRequest(InstanceCommand.Open, version, parts[2]);
                return true;
            default:
                return false;
        }
    }

    public static string Reply(string version) => $"ok{Separator}{version}";

    /// <summary>The running instance's version from a reply, or null when the reply is not one.</summary>
    public static string? ParseReply(string? reply)
    {
        if (reply is null || !reply.StartsWith("ok" + Separator, StringComparison.Ordinal))
        {
            return null;
        }

        return reply[3..];
    }
}

/// <summary>
/// Hands pipe requests (received on a thread-pool thread) to the app. Requests that arrive before the app subscribes
/// (the window is still being built) are kept and delivered on subscription.
/// </summary>
public static class InstanceRequestHub
{
    private static readonly ConcurrentQueue<InstanceRequest> Pending = new();
    private static Action<InstanceRequest>? _handler;

    /// <summary>The pipe server's handler: queues or delivers the request and replies with this version.</summary>
    public static Task<string> HandleAsync(string line, CancellationToken ct)
    {
        if (InstanceProtocol.TryParse(line, out var request))
        {
            if (Volatile.Read(ref _handler) is { } handler)
            {
                handler(request);
            }
            else
            {
                Pending.Enqueue(request);
            }
        }

        return Task.FromResult(InstanceProtocol.Reply(AppIdentity.Version));
    }

    /// <summary>Sets the handler (called on the pipe's thread) and delivers anything queued.</summary>
    public static void Subscribe(Action<InstanceRequest> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Volatile.Write(ref _handler, handler);
        while (Pending.TryDequeue(out var request))
        {
            handler(request);
        }
    }

    public static void Unsubscribe() => Volatile.Write(ref _handler, null);
}

/// <summary>The second launch: forwards its request to the running instance and exits.</summary>
internal static class SecondInstance
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Returns the process exit code (0) when the running instance answered, or null when it did not.</summary>
    public static int? Run(string[] args, InstanceRequest request)
    {
        // This process was started by the user, so it may pass on the right to take the foreground: the running
        // instance can then bring its window (or the launch picker) to the front instead of only flashing it.
        Platform.Windows.Processes.ForegroundRights.AllowAny();
        string? reply;
        try
        {
            reply = InstancePipeClient.SendAsync(InstancePipeServer.PipeNameFor(), InstanceProtocol.Format(request), ReplyTimeout)
                .GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        {
            reply = null;
        }

        if (InstanceProtocol.ParseReply(reply) is not { } running)
        {
            return null;
        }

        if (!string.Equals(running, AppIdentity.Version, StringComparison.Ordinal))
        {
            // Upstream's notice; the language setting is not read here (the running instance owns the settings).
            var message = Loc.Tr("msg.popup.blver_mismatch", ("running", running), ("given", AppIdentity.Version));
            App.MainWindowFactory = () =>
            {
                var notice = new DialogWindow(new DialogRequest
                {
                    Kind = DialogKind.Warning,
                    Title = Loc.Tr("ui.instance.title"),
                    Message = message,
                    Buttons = DialogService.OkButtons(),
                })
                {
                    ShowInTaskbar = true,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };
                WindowPositionOverride.Apply(notice);
                return notice;
            };
            Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        return 0;
    }
}
