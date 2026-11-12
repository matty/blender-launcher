using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.Core.Scraping;

/// <summary>
/// A downloadable build as a source reports it: the <see cref="BuildInfo"/> in upstream's shape
/// (its <see cref="BuildInfo.Link"/> is the download URL) plus what the downloads list shows beside it.
/// Immutable; <c>with</c> changes a field (the scrape service sets <see cref="IsNew"/> that way).
/// </summary>
public sealed record AvailableBuild
{
    public AvailableBuild(BuildInfo build)
    {
        ArgumentNullException.ThrowIfNull(build);
        Build = build;
    }

    public BuildInfo Build { get; init; }

    /// <summary>Archive size in bytes, when the source gives it (listing text, JSON <c>file_size</c>, <c>Content-Length</c>).</summary>
    public long? SizeBytes { get; init; }

    /// <summary>
    /// Newer than the last check (upstream's "new" badge). Sources always leave this false; the scrape service
    /// sets it.
    /// </summary>
    public bool IsNew { get; init; }

    /// <summary>Extra text for the row, for example a pull request's title; null when there is none.</summary>
    public string? Description { get; init; }
}
