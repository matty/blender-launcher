using System.Text.Json.Serialization;

namespace BlenderLauncher.Core.Scraping.Upbge;

/// <summary>The fields of a GitHub release that the UPBGE scraper reads.</summary>
public sealed class GitHubRelease
{
    public string? TagName { get; set; }

    public bool Draft { get; set; }

    public bool Prerelease { get; set; }

    public string? PublishedAt { get; set; }

    public List<GitHubReleaseAsset>? Assets { get; set; }
}

/// <summary>The fields of a GitHub release asset that the UPBGE scraper reads.</summary>
public sealed class GitHubReleaseAsset
{
    public string? Name { get; set; }

    public string? BrowserDownloadUrl { get; set; }

    public long? Size { get; set; }
}

/// <summary>An entry of GitHub's tag list.</summary>
public sealed class GitHubTag
{
    public string? Name { get; set; }

    public GitHubTagCommit? Commit { get; set; }
}

/// <summary>The commit a tag points at.</summary>
public sealed class GitHubTagCommit
{
    public string? Sha { get; set; }
}

/// <summary>Source-generated JSON metadata for the GitHub release and tag lists (Native AOT; no reflection).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(List<GitHubRelease>))]
[JsonSerializable(typeof(List<GitHubTag>))]
public sealed partial class UpbgeJsonContext : JsonSerializerContext;
