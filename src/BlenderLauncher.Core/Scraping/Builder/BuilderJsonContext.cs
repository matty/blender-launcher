using System.Text.Json.Serialization;

namespace BlenderLauncher.Core.Scraping.Builder;

/// <summary>
/// One entry of builder.blender.org's <c>?format=json&amp;v=1</c> listing. Only the fields
/// upstream reads, plus <c>file_size</c>. Every field is nullable: an entry missing one is skipped or handled as
/// upstream would, never a reason to fail the whole listing.
/// </summary>
public sealed class BuilderEntry
{
    public string? Url { get; set; }

    public string? Version { get; set; }

    public string? Branch { get; set; }

    public string? Patch { get; set; }

    public string? Hash { get; set; }

    public string? Platform { get; set; }

    public string? Architecture { get; set; }

    /// <summary>Seconds since the Unix epoch, UTC.</summary>
    public double? FileMtime { get; set; }

    public string? FileName { get; set; }

    public long? FileSize { get; set; }

    public string? ReleaseCycle { get; set; }
}

/// <summary>Source-generated JSON for the builder listing (no reflection, so safe under Native AOT); separate from <c>LauncherJsonContext</c> as it is read-only remote data.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(List<BuilderEntry>))]
internal sealed partial class BuilderJsonContext : JsonSerializerContext;
