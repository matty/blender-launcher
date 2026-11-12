using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BlenderLauncher.Core.Tests.Net;

/// <summary>
/// Pins what .NET's own <see cref="SocketsHttpHandler"/> redirect support does with <c>Authorization</c>, against a
/// tiny loopback HTTP server. The launcher does not rely on it (its primary handler has <c>AllowAutoRedirect</c> off
/// and <c>RedirectHandler</c> decides per hop), but this documents why: .NET drops the header on every redirect,
/// same host included, so it would neither leak the token nor keep it where it belongs.
/// </summary>
public sealed class BuiltInRedirectBehaviourTests : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentQueue<(string Path, string? Authorization, string Host)> _seen = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;

    public BuiltInRedirectBehaviourTests()
    {
        _listener.Start();
        _server = Task.Run(ServeAsync);
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    [Theory]
    [InlineData("/cross")] // 127.0.0.1 -> localhost
    [InlineData("/same")] // 127.0.0.1 -> 127.0.0.1
    public async Task Built_in_redirects_drop_authorization(string start)
    {
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, UseProxy = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port}{start}");
        request.Headers.TryAddWithoutValidation("Authorization", "token secret-value");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = _seen.ToList();
        Assert.Equal(2, seen.Count);
        Assert.Equal("token secret-value", seen[0].Authorization);
        Assert.Equal("/end", seen[1].Path);
        Assert.Null(seen[1].Authorization);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _server;
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            using var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
            await using var stream = socket.GetStream();
            var head = await ReadHeadAsync(stream);
            var lines = head.Split("\r\n");
            var path = lines[0].Split(' ')[1];
            string? auth = null;
            var host = "";
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    continue;
                }

                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    auth = value;
                }
                else if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    host = value;
                }
            }

            _seen.Enqueue((path, auth, host));
            var reply = path switch
            {
                "/cross" => $"HTTP/1.1 302 Found\r\nLocation: http://localhost:{Port}/end\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                "/same" => "HTTP/1.1 302 Found\r\nLocation: /end\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok",
            };
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), _stop.Token);
        }
    }

    private async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), _stop.Token);
            if (n == 0)
            {
                break;
            }

            total += n;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                return text;
            }
        }

        return Encoding.ASCII.GetString(buffer, 0, total);
    }
}
