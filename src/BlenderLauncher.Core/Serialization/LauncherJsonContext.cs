using System.Text.Json.Serialization;
using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Scraping;

namespace BlenderLauncher.Core.Serialization;

/// <summary>
/// The single source-generated JSON context (no reflection, so safe under Native AOT). Every serialised type is registered here.
/// Upstream writes snake_case keys and keeps nulls (for example <c>"custom_executable": null</c>).
/// Dictionary keys (version strings) are not renamed.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(BlinfoDocument))]
[JsonSerializable(typeof(ApiDataDocument))]
[JsonSerializable(typeof(ScraperCacheDocument))]
public sealed partial class LauncherJsonContext : JsonSerializerContext;
