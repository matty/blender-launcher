using System.Text.Json.Serialization;

namespace BlenderLauncher.Core.Updates;

/// <summary>The fields the launcher reads from one entry of GitHub's <c>GET /repos/{repo}/releases</c>.</summary>
public sealed class GitHubReleaseDocument
{
    public string? TagName { get; set; }

    public string? Name { get; set; }

    public bool Draft { get; set; }

    public bool Prerelease { get; set; }

    /// <summary>Markdown release notes; GitHub sends null when there are none.</summary>
    public string? Body { get; set; }

    public string? HtmlUrl { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public List<GitHubAssetDocument>? Assets { get; set; }
}

/// <summary>One release asset.</summary>
public sealed class GitHubAssetDocument
{
    public string? Name { get; set; }

    public long Size { get; set; }

    public string? BrowserDownloadUrl { get; set; }
}

/// <summary>
/// Source-generated JSON for the self-update code (no reflection, so safe under Native AOT). Kept apart from <c>LauncherJsonContext</c>
/// because these types are read-only views of GitHub's API, never written by the launcher.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(List<GitHubReleaseDocument>))]
internal sealed partial class UpdatesJsonContext : JsonSerializerContext;
