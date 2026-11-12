using System.IO.Compression;
using SharpCompress.Compressors.ZStandard;

namespace BlenderLauncher.Core.Builds;

/// <summary>
/// Port of upstream <c>modules/blendfile_reader.py</c>.
/// Tries the file as raw, then gzip, then zstd, as upstream does (no magic-byte sniffing of the container), and
/// reads at most the first <see cref="ByteReadLimit"/> decompressed bytes, never the whole file. The zstd decoder
/// is SharpCompress's public streaming <see cref="DecompressionStream"/> with <c>checkEndOfStream: false</c>, so
/// a frame without a content size, or one cut short, still yields its first bytes.
/// </summary>
/// <remarks>
/// Header layouts (from Blender's <c>BLO_core_blend_header.hh</c>):
/// <list type="bullet">
/// <item>Format 0, 12 bytes: <c>BLENDER</c>, <c>-</c> or <c>_</c> (pointer size), <c>v</c> or <c>V</c>
/// (endianness), three digits (<c>404</c> is 4.4).</item>
/// <item>Format 1, 17 bytes: <c>BLENDER</c>, two digits (header size, <c>17</c>), <c>-</c>, two digits (format,
/// <c>01</c>), <c>v</c>, four digits (<c>0501</c> is 5.1).</item>
/// </list>
/// Upstream also accepts a raw file starting <c>BULLETf</c> and reads its three digits as a version; that is kept.
/// Unlike upstream, a decoded header must match one of the layouts, so an empty or unrelated gzip file is
/// reported as <see cref="NotABlendFileException"/> rather than crashing the parser.
/// </remarks>
public static class BlendFileHeaderReader
{
    /// <summary>Upstream <c>BYTE_READ_LIMIT</c>: must grow if a future header format is longer.</summary>
    public const int ByteReadLimit = 20;

    private static readonly BlendCompression[] Attempts = [BlendCompression.None, BlendCompression.Gzip, BlendCompression.Zstd];

    /// <exception cref="NotABlendFileException">The file has no recognisable header.</exception>
    public static BlendFileHeader Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        return Read(file);
    }

    /// <summary>Reads the header from a seekable stream positioned anywhere. The stream is left open.</summary>
    /// <exception cref="NotABlendFileException">The stream has no recognisable header.</exception>
    public static BlendFileHeader Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buffer = stackalloc byte[ByteReadLimit];
        foreach (var compression in Attempts)
        {
            stream.Position = 0;
            int count;
            try
            {
                using var decoder = OpenDecoder(stream, compression);
                count = decoder.ReadAtLeast(buffer, ByteReadLimit, throwOnEndOfStream: false);
            }
            catch (Exception e) when (IsDecodeFailure(e))
            {
                continue;
            }

            if (TryParseHeader(buffer[..count], out var major, out var minor, out var format))
            {
                return new BlendFileHeader(major, minor, format, compression);
            }
        }

        throw new NotABlendFileException();
    }

    /// <exception cref="NotABlendFileException">The file has no recognisable header.</exception>
    public static async Task<BlendFileHeader> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, useAsync: true);
        await using (file.ConfigureAwait(false))
        {
            return await ReadAsync(file, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the header from a seekable stream positioned anywhere. The stream is left open.</summary>
    /// <exception cref="NotABlendFileException">The stream has no recognisable header.</exception>
    public static async Task<BlendFileHeader> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[ByteReadLimit];
        foreach (var compression in Attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            int count;
            try
            {
                var decoder = OpenDecoder(stream, compression);
                await using (decoder.ConfigureAwait(false))
                {
                    count = await decoder.ReadAtLeastAsync(buffer, ByteReadLimit, throwOnEndOfStream: false, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception e) when (IsDecodeFailure(e))
            {
                continue;
            }

            if (TryParseHeader(buffer.AsSpan(0, count), out var major, out var minor, out var format))
            {
                return new BlendFileHeader(major, minor, format, compression);
            }
        }

        throw new NotABlendFileException();
    }

    /// <summary>
    /// Upstream <c>parse_header_version</c>: format 1 first, then format 0. Returns the major and minor version
    /// and the header format.
    /// </summary>
    /// <exception cref="NotABlendFileException">Neither layout matches.</exception>
    public static (int Major, int Minor, int FormatVersion) ParseHeaderVersion(ReadOnlySpan<byte> header) =>
        TryParseHeader(header, out var major, out var minor, out var format)
            ? (major, minor, format)
            : throw new NotABlendFileException("Not a blendfile header");

    public static bool TryParseHeader(ReadOnlySpan<byte> header, out int major, out int minor, out int formatVersion)
    {
        major = minor = formatVersion = 0;

        // Format 1: BLENDER17-01v0501
        if (header.Length >= 17
            && header.StartsWith("BLENDER"u8)
            && AllDigits(header[7..9]) && header[9] == '-' && AllDigits(header[10..12]) && header[12] == 'v'
            && AllDigits(header[13..17]))
        {
            major = Number(header[13..15]);
            minor = Number(header[15..17]);
            formatVersion = 1;
            return true;
        }

        // Format 0: BLENDER-v404 (or BULLETf, which upstream also accepts)
        if (header.Length >= 12
            && (header.StartsWith("BLENDER"u8) || header.StartsWith("BULLETf"u8))
            && header[7] is (byte)'-' or (byte)'_' && header[8] is (byte)'v' or (byte)'V'
            && AllDigits(header[9..12]))
        {
            major = header[9] - '0';
            minor = Number(header[10..12]);
            formatVersion = 0;
            return true;
        }

        return false;
    }

    private static Stream OpenDecoder(Stream stream, BlendCompression compression) => compression switch
    {
        BlendCompression.None => new NonClosingStream(stream),
        BlendCompression.Gzip => new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true),
        BlendCompression.Zstd => new DecompressionStream(stream, checkEndOfStream: false, leaveOpen: true),
        _ => throw new ArgumentOutOfRangeException(nameof(compression)),
    };

    // A decoder rejecting the bytes, as opposed to the file itself failing to read (plain IOException) or
    // cancellation. EndOfStreamException is an IOException but means a truncated container.
    private static bool IsDecodeFailure(Exception e) => e switch
    {
        OperationCanceledException or OutOfMemoryException => false,
        EndOfStreamException => true,
        IOException => false,
        UnauthorizedAccessException => false,
        _ => true,
    };

    private static bool AllDigits(ReadOnlySpan<byte> span)
    {
        foreach (var b in span)
        {
            if (b is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
        }

        return true;
    }

    private static int Number(ReadOnlySpan<byte> digits)
    {
        var value = 0;
        foreach (var b in digits)
        {
            value = value * 10 + (b - '0');
        }

        return value;
    }

    /// <summary>Lets the raw attempt share the decoder code path without disposing the caller's stream.</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
