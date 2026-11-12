using System.IO.Compression;
using BlenderLauncher.Core.Builds;
using SharpCompress.Compressors.ZStandard;

namespace BlenderLauncher.Core.Tests.Builds;

/// <summary>
/// Port of upstream <c>tests/backend/test_blendfile_reader.py</c>, then our own edge cases. The upstream byte
/// blobs are saved byte for byte as Fixtures/blend-header-v0.gz, blend-header-v0.zst and blend-header-v1.zst.
/// </summary>
public sealed class BlendFileHeaderReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bl-blendheader-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // --- test_blendfile_reader.py ---

    [Fact]
    public void Header_parser_version_0()
    {
        Assert.Equal((4, 4, 0), BlendFileHeaderReader.ParseHeaderVersion("BLENDER-v404"u8));

        var gb = WriteFile("gzip.blend", File.ReadAllBytes(Fixtures.PathOf("blend-header-v0.gz")));
        var zb = WriteFile("zstd.blend", File.ReadAllBytes(Fixtures.PathOf("blend-header-v0.zst")));

        Assert.Equal(new BlendFileHeader(4, 4, 0, BlendCompression.Gzip), BlendFileHeaderReader.Read(gb));
        Assert.Equal(new BlendFileHeader(4, 4, 0, BlendCompression.Zstd), BlendFileHeaderReader.Read(zb));
    }

    [Fact]
    public void Header_parser_version_1()
    {
        // Commented out upstream; it passes here.
        Assert.Equal((5, 1, 1), BlendFileHeaderReader.ParseHeaderVersion("BLENDER17-01v0501REND"u8));

        var zb = WriteFile("zstd.blend", File.ReadAllBytes(Fixtures.PathOf("blend-header-v1.zst")));

        Assert.Equal(new BlendFileHeader(5, 1, 1, BlendCompression.Zstd), BlendFileHeaderReader.Read(zb));
    }

    // --- our own ---

    [Theory]
    [InlineData("blend-header-v0.gz", 4, 4, 0, BlendCompression.Gzip)]
    [InlineData("blend-header-v1.zst", 5, 1, 1, BlendCompression.Zstd)]
    public async Task Async_overload_matches(string fixture, int major, int minor, int format, BlendCompression compression) =>
        Assert.Equal(
            new BlendFileHeader(major, minor, format, compression),
            await BlendFileHeaderReader.ReadAsync(Fixtures.PathOf(fixture), TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("BLENDER-v279REND", 2, 79, 0)]
    [InlineData("BLENDER_V300REND", 3, 0, 0)]
    [InlineData("BLENDER-v404", 4, 4, 0)]
    [InlineData("BLENDER17-01v0405REND", 4, 5, 1)]
    [InlineData("BULLETf_v285", 2, 85, 0)] // upstream accepts BULLETf and reads its digits the same way
    public void Raw_headers_are_not_compressed(string header, int major, int minor, int format) =>
        Assert.Equal(new BlendFileHeader(major, minor, format, BlendCompression.None), ReadBytes(System.Text.Encoding.ASCII.GetBytes(header)));

    [Fact]
    public void Gzip_of_a_v1_header_reads()
    {
        var bytes = Gzip("BLENDER17-01v0501REND"u8.ToArray());
        Assert.Equal(new BlendFileHeader(5, 1, 1, BlendCompression.Gzip), ReadBytes(bytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a blend file at all, just some text")]
    [InlineData("BLENDER-v4")] // truncated raw header
    [InlineData("BLENDER-vXYZ")] // magic but no version digits
    public async Task Not_a_blend_file_is_rejected(string content)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(content);
        Assert.Throws<NotABlendFileException>(() => ReadBytes(bytes));
        var path = WriteFile("x.blend", bytes);
        Assert.Throws<NotABlendFileException>(() => BlendFileHeaderReader.Read(path));
        await Assert.ThrowsAsync<NotABlendFileException>(() => BlendFileHeaderReader.ReadAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Random_bytes_are_rejected()
    {
        var bytes = new byte[4096];
        new Random(1234).NextBytes(bytes);
        Assert.Throws<NotABlendFileException>(() => ReadBytes(bytes));
    }

    [Fact]
    public void Gzip_or_zstd_of_something_else_is_rejected()
    {
        Assert.Throws<NotABlendFileException>(() => ReadBytes(Gzip("hello, this is not a blend file"u8.ToArray())));
        Assert.Throws<NotABlendFileException>(() => ReadBytes(Zstd("hello, this is not a blend file"u8.ToArray())));
        Assert.Throws<NotABlendFileException>(() => ReadBytes(Gzip([])));
    }

    [Theory]
    [InlineData("blend-header-v0.gz", 12)]
    [InlineData("blend-header-v0.gz", 5)]
    [InlineData("blend-header-v0.zst", 6)]
    public void Truncated_container_is_rejected(string fixture, int keep)
    {
        var bytes = File.ReadAllBytes(Fixtures.PathOf(fixture))[..keep];
        Assert.Throws<NotABlendFileException>(() => ReadBytes(bytes));
    }

    [Fact]
    public void Missing_file_is_an_io_error_not_a_format_error() =>
        Assert.Throws<FileNotFoundException>(() => BlendFileHeaderReader.Read(Path.Combine(_dir, "missing.blend")));

    [Fact]
    public async Task Cancelled_token_cancels()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BlendFileHeaderReader.ReadAsync(Fixtures.PathOf("blend-header-v0.zst"), cts.Token));
    }

    public static TheoryData<BlendCompression> Compressions => [BlendCompression.None, BlendCompression.Gzip, BlendCompression.Zstd];

    /// <summary>
    /// A 16 MB body of random (incompressible) bytes after the header: only the head is read from the
    /// underlying stream, never the whole file, in both overloads.
    /// </summary>
    [Theory]
    [MemberData(nameof(Compressions))]
    public async Task Large_file_reads_only_the_head(BlendCompression compression)
    {
        var body = new byte[16 * 1024 * 1024];
        new Random(42).NextBytes(body);
        "BLENDER17-01v0501REND"u8.CopyTo(body);
        var bytes = compression switch
        {
            BlendCompression.Gzip => Gzip(body),
            BlendCompression.Zstd => Zstd(body),
            _ => body,
        };
        Assert.True(bytes.Length > 15 * 1024 * 1024);

        var counting = new CountingStream(new MemoryStream(bytes));
        Assert.Equal(new BlendFileHeader(5, 1, 1, compression), BlendFileHeaderReader.Read(counting));
        Assert.InRange(counting.BytesRead, 1, 1024 * 1024);

        counting = new CountingStream(new MemoryStream(bytes));
        Assert.Equal(
            new BlendFileHeader(5, 1, 1, compression),
            await BlendFileHeaderReader.ReadAsync(counting, TestContext.Current.CancellationToken));
        Assert.InRange(counting.BytesRead, 1, 1024 * 1024);
    }

    [Fact]
    public void Raw_file_reads_exactly_the_limit()
    {
        var counting = new CountingStream(new MemoryStream([.. "BLENDER-v404"u8.ToArray(), .. new byte[1000]]));
        BlendFileHeaderReader.Read(counting);
        Assert.Equal(BlendFileHeaderReader.ByteReadLimit, counting.BytesRead);
    }

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static BlendFileHeader ReadBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return BlendFileHeaderReader.Read(stream);
    }

    private static byte[] Gzip(byte[] data)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Zstd(byte[] data)
    {
        var output = new MemoryStream();
        using (var zstd = new CompressionStream(output, 1, 0, true))
        {
            zstd.Write(data);
        }

        return output.ToArray();
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int n)
        {
            BytesRead += n;
            return n;
        }
    }
}
