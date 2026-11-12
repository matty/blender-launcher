using System.Net;
using BlenderLauncher.Core.Pipeline;

namespace BlenderLauncher.Core.Tests.Pipeline;

public sealed class DownloaderTests : IDisposable
{
    private static readonly Uri Url = new("https://download.example.org/release/blender-4.2.0-windows-x64.zip");
    private readonly TempDir _temp = new();
    private readonly byte[] _body = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();

    private static DownloaderOptions FastOptions => new()
    {
        FirstByteTimeout = TimeSpan.FromMilliseconds(300),
        RetryFirstByteTimeout = TimeSpan.FromMilliseconds(300),
        StallTimeout = TimeSpan.FromMilliseconds(300),
        ProgressInterval = TimeSpan.Zero,
        BufferSize = 64 * 1024,
        FreeSpace = _ => long.MaxValue,
    };

    [Fact]
    public async Task Downloads_with_content_length_through_a_part_file()
    {
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Bytes(_body)));
        var reports = new List<TransferProgress>();
        var path = await new Downloader(handler.Client, FastOptions).DownloadAsync(
            new DownloadRequest(Url, _temp.Staging), new SyncProgress<TransferProgress>(reports.Add), CancellationToken.None);

        Assert.Equal(Path.Combine(_temp.Staging, "blender-4.2.0-windows-x64.zip"), path);
        Assert.Equal(_body, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".part"));
        Assert.True(reports[0].IsIndeterminate);
        Assert.Equal(new TransferProgress(_body.Length, _body.Length), reports[^1]);
        Assert.Contains(reports, r => r.Total == _body.Length && r.Done > 0 && r.Done < _body.Length);
    }

    [Fact]
    public async Task Downloads_without_content_length_with_indeterminate_progress()
    {
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Bytes(_body, withLength: false)));
        var reports = new List<TransferProgress>();
        var path = await new Downloader(handler.Client, FastOptions).DownloadAsync(
            new DownloadRequest(Url, _temp.Staging), new SyncProgress<TransferProgress>(reports.Add), CancellationToken.None);

        Assert.Equal(_body, File.ReadAllBytes(path));
        Assert.All(reports.Take(reports.Count - 1), r => Assert.Null(r.Total));
        Assert.Contains(reports, r => r.IsIndeterminate && r.Done > 0);
        Assert.Equal(new TransferProgress(_body.Length, _body.Length), reports[^1]);
    }

    [Theory]
    [InlineData("text/html; charset=utf-8")]
    public async Task Rejects_a_non_archive_content_type(string contentType)
    {
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Bytes(_body, contentType)));
        var ex = await Assert.ThrowsAsync<PipelineException>(() => new Downloader(handler.Client, FastOptions).DownloadAsync(
            new DownloadRequest(Url, _temp.Staging), null, CancellationToken.None));
        Assert.Equal(PipelineErrorKind.BadContentType, ex.Kind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Staging));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("application/octet-stream", true)]
    [InlineData("Application/X-7z-Compressed; application/octet-stream", true)]
    [InlineData("application/x-zip-compressed", true)]
    [InlineData("application/x-7z-compressed", false)]
    [InlineData("text/html", false)]
    public void Content_type_allow_list_matches_upstream(string? contentType, bool allowed) =>
        Assert.Equal(allowed, Downloader.IsAllowedContentType(contentType));

    [Fact]
    public async Task Http_error_status_fails_without_retry()
    {
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Bytes([], "text/html", status: HttpStatusCode.NotFound)));
        var ex = await Assert.ThrowsAsync<PipelineException>(() => new Downloader(handler.Client, FastOptions).DownloadAsync(
            new DownloadRequest(Url, _temp.Staging), null, CancellationToken.None));
        Assert.Equal(PipelineErrorKind.HttpStatus, ex.Kind);
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_slow_first_response_is_retried_once()
    {
        var handler = new StubHandler(async (_, n, ct) =>
        {
            if (n == 1)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return StubHandler.Bytes(_body);
        });
        var path = await new Downloader(handler.Client, FastOptions).DownloadAsync(new DownloadRequest(Url, _temp.Staging), null, CancellationToken.None);
        Assert.Equal(2, handler.Requests);
        Assert.Equal(_body.Length, new FileInfo(path).Length);
    }

    [Fact]
    public async Task A_connection_failure_is_retried_once_then_reported()
    {
        var handler = new StubHandler((_, _, _) => throw new HttpRequestException("connection refused"));
        var ex = await Assert.ThrowsAsync<PipelineException>(() => new Downloader(handler.Client, FastOptions).DownloadAsync(
            new DownloadRequest(Url, _temp.Staging), null, CancellationToken.None));
        Assert.Equal(PipelineErrorKind.Network, ex.Kind);
        Assert.Equal(2, handler.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Staging));
    }

    [Fact]
    public async Task A_stalled_body_fails_and_deletes_the_part_file()
    {
        var handler = new StubHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream()),
        }));
        var ex = await Assert.ThrowsAsync<PipelineException>(() => new Downloader(handler.Client, FastOptions).DownloadAsync(
            new DownloadRequest(Url, _temp.Staging), null, CancellationToken.None));
        Assert.Equal(PipelineErrorKind.Network, ex.Kind);
        Assert.Contains("stalled", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Staging));
    }

    [Fact]
    public async Task Cancel_mid_download_deletes_the_part_file()
    {
        var stalling = new StallingStream();
        var handler = new StubHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalling) }));
        using var cts = new CancellationTokenSource();
        var options = FastOptions with { StallTimeout = TimeSpan.FromMinutes(1) };
        var download = new Downloader(handler.Client, options).DownloadAsync(new DownloadRequest(Url, _temp.Staging), null, cts.Token);
        await stalling.FirstChunkSent.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await TaskQueueTests.WaitUntil(() => File.Exists(Path.Combine(_temp.Staging, "blender-4.2.0-windows-x64.zip.part")));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Staging));
    }

    [Fact]
    public async Task A_short_body_is_an_error()
    {
        var handler = new StubHandler((_, _, _) =>
        {
            var response = StubHandler.Bytes(_body);
            response.Content.Headers.ContentLength = _body.Length + 100;
            return Task.FromResult(response);
        });
        var ex = await Assert.ThrowsAsync<PipelineException>(() => new Downloader(handler.Client, FastOptions).DownloadAsync(
            new DownloadRequest(Url, _temp.Staging), null, CancellationToken.None));
        Assert.Equal(PipelineErrorKind.Network, ex.Kind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Staging));
    }

    [Fact]
    public async Task Listed_size_fails_the_free_space_check_before_any_request()
    {
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Bytes(_body)));
        var options = FastOptions with { FreeSpace = _ => 250_000_000 };
        var ex = await Assert.ThrowsAsync<PipelineException>(() => new Downloader(handler.Client, options).DownloadAsync(
            new DownloadRequest(Url, _temp.Staging) { ExpectedSize = 100_000_000 }, null, CancellationToken.None));

        Assert.Equal(PipelineErrorKind.DiskSpace, ex.Kind);
        Assert.Equal(300_000_000, ex.RequiredBytes);
        Assert.Equal(250_000_000, ex.AvailableBytes);
        Assert.Equal(_temp.Staging, ex.Folder);
        Assert.Contains("MB", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Content_length_fails_the_free_space_check()
    {
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Bytes(_body)));
        var options = FastOptions with { FreeSpace = _ => _body.Length * 2L };
        var ex = await Assert.ThrowsAsync<PipelineException>(() => new Downloader(handler.Client, options).DownloadAsync(
            new DownloadRequest(Url, _temp.Staging), null, CancellationToken.None));
        Assert.Equal(PipelineErrorKind.DiskSpace, ex.Kind);
        Assert.Equal(_body.Length * 3L, ex.RequiredBytes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Staging));
    }

    [Fact]
    public async Task Unknown_free_space_skips_the_check()
    {
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Bytes(_body)));
        var options = FastOptions with { FreeSpace = _ => null };
        var path = await new Downloader(handler.Client, options).DownloadAsync(
            new DownloadRequest(Url, _temp.Staging) { ExpectedSize = long.MaxValue / 4 }, null, CancellationToken.None);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Overwrites_an_existing_archive_of_the_same_name()
    {
        Directory.CreateDirectory(_temp.Staging);
        await File.WriteAllTextAsync(Path.Combine(_temp.Staging, "blender-4.2.0-windows-x64.zip"), "old", TestContext.Current.CancellationToken);
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Bytes(_body)));
        var path = await new Downloader(handler.Client, FastOptions).DownloadAsync(new DownloadRequest(Url, _temp.Staging), null, CancellationToken.None);
        Assert.Equal(_body.Length, new FileInfo(path).Length);
    }

    [Theory]
    [InlineData("https://download.blender.org/release/Blender4.2/blender-4.2.0-windows-x64.zip", "blender-4.2.0-windows-x64.zip")]
    [InlineData("https://example.org/a/My%20Build.7z?x=1", "My Build.7z")]
    [InlineData("https://cloud.bforartists.de/index.php/s/abc/download?path=%2F&files=Bforartists-4.1.1-Windows.zip", "Bforartists-4.1.1-Windows.zip")]
    public void File_name_comes_from_the_url(string url, string expected) =>
        Assert.Equal(expected, ArchiveNames.FromUrl(new Uri(url)));

    [Fact]
    public void A_url_without_a_file_name_is_refused() =>
        Assert.Throws<ArgumentException>(() => ArchiveNames.FromUrl(new Uri("https://example.org/")));

    public void Dispose() => _temp.Dispose();
}
