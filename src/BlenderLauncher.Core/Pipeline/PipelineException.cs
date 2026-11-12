namespace BlenderLauncher.Core.Pipeline;

/// <summary>Why a pipeline step failed, so the UI can pick a message and an action (for example "Open settings").</summary>
public enum PipelineErrorKind
{
    /// <summary>A network failure: no connection, a timeout, the body cut short.</summary>
    Network,

    /// <summary>The server answered with status 400 or above (<see cref="PipelineException.StatusCode"/>).</summary>
    HttpStatus,

    /// <summary>The response's <c>Content-Type</c> is not an archive type.</summary>
    BadContentType,

    /// <summary>Writing was refused; <see cref="PipelineException.Folder"/> is the folder (upstream's <c>permission_error</c>).</summary>
    Permission,

    /// <summary>Not enough free space; <see cref="PipelineException.RequiredBytes"/> and <see cref="PipelineException.AvailableBytes"/> hold the numbers.</summary>
    DiskSpace,

    /// <summary>
    /// The staging folder or a file in it disappeared mid-job, typically because a running upstream purged
    /// <c>.temp</c>.
    /// </summary>
    StagingVanished,

    /// <summary>The archive could not be read. It has been deleted.</summary>
    CorruptArchive,

    /// <summary>An entry would be written outside the extraction folder (zip-slip). The archive has been deleted.</summary>
    UnsafeArchive,

    /// <summary>The archive's format is not supported on this platform.</summary>
    UnsupportedArchive,

    /// <summary>A file stayed locked after the retries (antivirus, a running Blender).</summary>
    FileInUse,

    /// <summary>The Recycle Bin refused an item that had to go before the step could continue.</summary>
    RecycleRefused,

    /// <summary>Reading the build's information failed.</summary>
    BuildInfo,

    /// <summary>Any other file-system error.</summary>
    Io,
}

/// <summary>A pipeline failure with a <see cref="Kind"/> and a message fit for the log and, once translated, the UI.</summary>
public sealed class PipelineException : Exception
{
    public PipelineException(PipelineErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public PipelineErrorKind Kind { get; }

    /// <summary>The folder involved, for <see cref="PipelineErrorKind.Permission"/>, <see cref="PipelineErrorKind.DiskSpace"/> and <see cref="PipelineErrorKind.StagingVanished"/>.</summary>
    public string? Folder { get; init; }

    /// <summary>The HTTP status for <see cref="PipelineErrorKind.HttpStatus"/>.</summary>
    public int? StatusCode { get; init; }

    /// <summary>For <see cref="PipelineErrorKind.DiskSpace"/>: the bytes the step wanted free.</summary>
    public long? RequiredBytes { get; init; }

    /// <summary>For <see cref="PipelineErrorKind.DiskSpace"/>: the bytes free on the volume.</summary>
    public long? AvailableBytes { get; init; }

    internal static PipelineException StagingVanished(string folder, Exception? inner = null) =>
        new(PipelineErrorKind.StagingVanished,
            $"The temporary folder was cleared by another program ({folder})", inner)
        {
            Folder = folder,
        };

    internal static PipelineException Permission(string folder, Exception? inner = null) =>
        new(PipelineErrorKind.Permission, $"No permission to write to {folder}", inner) { Folder = folder };
}
