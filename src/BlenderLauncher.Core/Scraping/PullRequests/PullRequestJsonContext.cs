using System.Text.Json.Serialization;

namespace BlenderLauncher.Core.Scraping.PullRequests;

/// <summary>The fields of a Gitea pull request that the launcher reads.</summary>
public sealed class GiteaPullRequest
{
    public int Number { get; set; }

    public string? Title { get; set; }
}

/// <summary>
/// <c>&lt;data&gt;\cache\pr_labels.json</c>: titles by pull-request number, sorted by number.
/// Upstream's plain-text <c>pr_labels</c> file is not read.
/// </summary>
public sealed class PullRequestTitleCacheDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Trimmed titles by number.</summary>
    public SortedDictionary<int, string>? Titles { get; set; }
}

/// <summary>Source-generated JSON for the Gitea API and the title cache (no reflection, so safe under Native AOT).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(GiteaPullRequest))]
[JsonSerializable(typeof(List<GiteaPullRequest>))]
[JsonSerializable(typeof(PullRequestTitleCacheDocument))]
internal sealed partial class PullRequestJsonContext : JsonSerializerContext;
