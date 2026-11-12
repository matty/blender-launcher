namespace BlenderLauncher.Core.Builds;

/// <summary>How a .blend file is compressed (upstream <c>CompressionType</c>).</summary>
public enum BlendCompression
{
    /// <summary>Not compressed. Used by every Blender version.</summary>
    None,

    /// <summary>gzip, used by Blender before 3.0.</summary>
    Gzip,

    /// <summary>Zstandard, used by Blender 3.0 and later.</summary>
    Zstd,
}

/// <summary>
/// The Blender version a .blend file was saved with (major and minor only; the header has no patch number),
/// its low-level header format (0 or 1) and how the file is compressed.
/// </summary>
public sealed record BlendFileHeader(int Major, int Minor, int FormatVersion, BlendCompression Compression);

/// <summary>Thrown when a file is not a .blend file: no known header, raw, gzip or zstd.</summary>
public sealed class NotABlendFileException : Exception
{
    public NotABlendFileException()
        : base("Could not decode blendfile header")
    {
    }

    public NotABlendFileException(string message)
        : base(message)
    {
    }

    public NotABlendFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
