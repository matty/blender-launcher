using System.IO.Pipes;
using System.Text;
using BlenderLauncher.Core;

namespace BlenderLauncher.Platform.Windows.Ipc;

/// <summary>
/// Single-instance pipe. The first instance owns the pipe and answers one-line UTF-8 requests (for
/// example "show" or a command line); later instances send their request and exit. The pipe is
/// restricted to the current user (<see cref="PipeOptions.CurrentUserOnly"/>).
/// </summary>
public sealed class InstancePipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<string, CancellationToken, Task<string>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeServerStream? _listening;
    private Task? _loop;

    private InstancePipeServer(string pipeName, Func<string, CancellationToken, Task<string>> handler, NamedPipeServerStream first)
    {
        _pipeName = pipeName;
        _handler = handler;
        _listening = first;
    }

    /// <summary>The production pipe name, plus an optional suffix for tests.</summary>
    public static string PipeNameFor(string suffix = "") => AppIdentity.InstancePipeName + suffix;

    /// <summary>
    /// Creates the first pipe instance and starts serving. Returns null when another process already
    /// owns the pipe, which means this process is the second instance.
    /// </summary>
    /// <param name="handler">Called on a thread-pool thread with each request; returns the reply line.</param>
    public static InstancePipeServer? TryStart(string pipeName, Func<string, CancellationToken, Task<string>> handler)
    {
        NamedPipeServerStream first;
        try
        {
            first = CreateInstance(pipeName, firstInstance: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // FILE_FLAG_FIRST_PIPE_INSTANCE fails with access denied when the pipe already exists.
            return null;
        }

        var server = new InstancePipeServer(pipeName, handler, first);
        server._loop = Task.Run(server.ServeAsync);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _listening?.Dispose();
        _cts.Dispose();
    }

    private static NamedPipeServerStream CreateInstance(string pipeName, bool firstInstance)
    {
        var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        if (firstInstance)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        return new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options);
    }

    private async Task ServeAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            var current = _listening!;
            await current.WaitForConnectionAsync(ct).ConfigureAwait(false);

            // Open the next instance before serving this one, so the pipe name never disappears and a
            // third process cannot mistake itself for the first instance.
            _listening = CreateInstance(_pipeName, firstInstance: false);
            await using (current.ConfigureAwait(false))
            {
                await ServeOneAsync(current, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeOneAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var request = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            var reply = await _handler(request, ct).ConfigureAwait(false);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync(reply.AsMemory(), timeout.Token).ConfigureAwait(false);
            await writer.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // A client that disconnects early or stalls must not stop the server.
        }
    }
}

/// <summary>Client side of <see cref="InstancePipeServer"/>.</summary>
public static class InstancePipeClient
{
    /// <summary>Sends one line and returns the reply, or null when no server answered in time.</summary>
    public static async Task<string?> SendAsync(string pipeName, string request, TimeSpan timeout, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using (client.ConfigureAwait(false))
        {
            try
            {
                await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
                var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true);
                await writer.WriteLineAsync(request.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
                await writer.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
                var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                return await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
        }
    }
}
