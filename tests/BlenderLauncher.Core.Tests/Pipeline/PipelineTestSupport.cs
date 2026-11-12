using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.PlatformServices;

namespace BlenderLauncher.Core.Tests.Pipeline;

/// <summary>A temp folder deleted with the test.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bl-pipe-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>A library root with the standard staging folder.</summary>
    public string Staging => System.IO.Path.Combine(Path, ".temp", AppIdentity.TechnicalId);

    public void Dispose()
    {
        try
        {
            PermanentDelete.Run(Path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>An HTTP handler answering from a delegate, counting requests.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private int _count;

    public int Requests => Volatile.Read(ref _count);

    public HttpClient Client() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _count);
        return respond(request, n, cancellationToken);
    }

    public static HttpResponseMessage Bytes(byte[] body, string? contentType = "application/zip", bool withLength = true, HttpStatusCode status = HttpStatusCode.OK)
    {
        HttpContent content = withLength ? new ByteArrayContent(body) : new StreamContent(new NoLengthStream(body));
        if (contentType is not null)
        {
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        if (!withLength)
        {
            content.Headers.ContentLength = null;
        }

        return new HttpResponseMessage(status) { Content = content };
    }
}

/// <summary>A readable stream that cannot report its length, so no Content-Length is computed.</summary>
internal sealed class NoLengthStream(byte[] data) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = Math.Min(count, Math.Min(data.Length - _position, 4096));
        Array.Copy(data, _position, buffer, offset, n);
        _position += n;
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A stream that yields one chunk, then blocks until cancelled.</summary>
internal sealed class StallingStream : Stream
{
    private bool _sent;

    public TaskCompletionSource FirstChunkSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_sent)
        {
            _sent = true;
            buffer.Span[..10].Fill(1);
            FirstChunkSent.TrySetResult();
            return 10;
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A recycle bin that records calls and deletes permanently, or refuses, or throws as told.</summary>
internal sealed class FakeRecycleBin : IRecycleBin
{
    public ConcurrentQueue<string> Binned { get; } = new();

    /// <summary>When set, <see cref="SendToBin"/> leaves the item and returns NotRecycled with this reason (or deletes when allowed).</summary>
    public string? RefuseReason { get; set; }

    /// <summary>Throw this many sharing violations before succeeding.</summary>
    public int LockFailures { get; set; }

    public int Calls;

    public bool IsAvailable => true;

    public RecycleResult SendToBin(string path, bool allowPermanentDelete)
    {
        Interlocked.Increment(ref Calls);
        if (LockFailures > 0)
        {
            LockFailures--;
            throw new IOException("locked") { HResult = unchecked((int)0x80070020) };
        }

        if (!Path.Exists(path))
        {
            return new RecycleResult(RecycleOutcome.Missing);
        }

        if (RefuseReason is { } reason)
        {
            if (!allowPermanentDelete)
            {
                return new RecycleResult(RecycleOutcome.NotRecycled, reason);
            }

            PermanentDelete.Run(path);
            return new RecycleResult(RecycleOutcome.DeletedPermanently, reason);
        }

        Binned.Enqueue(path);
        PermanentDelete.Run(path);
        return new RecycleResult(RecycleOutcome.Recycled);
    }

    public void DeletePermanently(string path)
    {
        Interlocked.Increment(ref Calls);
        if (LockFailures > 0)
        {
            LockFailures--;
            throw new IOException("locked") { HResult = unchecked((int)0x80070020) };
        }

        PermanentDelete.Run(path);
    }
}

internal static class TestArchives
{
    /// <summary>A zip of the given entries (name, text); a name ending in '/' is a folder entry.</summary>
    public static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                if (!name.EndsWith('/'))
                {
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(text);
                }
            }
        }

        return memory.ToArray();
    }

    public static string WriteZip(string path, params (string Name, string Text)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Zip(entries));
        return path;
    }

    public static BuildInfo Build(string link, string subversion = "4.2.0", string branch = "stable") => new()
    {
        Link = link,
        Subversion = subversion,
        CommitTime = IsoTimestamp.Epoch,
        Branch = branch,
    };
}
