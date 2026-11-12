using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.Core.Scraping;

/// <summary>
/// JSON shape of upstream's <c>ScraperCache</c> files: an optional <c>api_file_version</c>
/// (stable caches only, written first) and the folders keyed by version text.
/// </summary>
public sealed class ScraperCacheDocument
{
    [JsonPropertyOrder(-1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiFileVersion { get; set; }

    /// <summary>Folders keyed by <c>str(Version)</c>, in file order.</summary>
    public Dictionary<string, StableFolderDocument> Folders { get; set; } = [];
}

/// <summary>JSON shape of one cached folder.</summary>
public sealed class StableFolderDocument
{
    public List<ScraperCacheAsset> Assets { get; set; } = [];

    /// <summary>ISO text; naive for stable caches, aware for Bforartists caches.</summary>
    public string? ModifiedDate { get; set; }
}

/// <summary>One cached asset, written as upstream's two-element array <c>[link, {.blinfo document}]</c>.</summary>
[JsonConverter(typeof(ScraperCacheAssetConverter))]
public sealed record ScraperCacheAsset(string Link, BlinfoDocument Build);

/// <summary>Reads and writes <see cref="ScraperCacheAsset"/> as a JSON array, without reflection.</summary>
public sealed class ScraperCacheAssetConverter : JsonConverter<ScraperCacheAsset>
{
    public override ScraperCacheAsset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("A cached asset must be a [link, build] array.");
        }

        reader.Read();
        var link = reader.TokenType == JsonTokenType.String
            ? reader.GetString()!
            : throw new JsonException("A cached asset's first element must be its link.");
        reader.Read();
        var build = JsonSerializer.Deserialize(ref reader, BlinfoTypeInfo(options))
                    ?? throw new JsonException("A cached asset's build must be an object.");
        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("A cached asset must have exactly two elements.");
        }

        return new ScraperCacheAsset(link, build);
    }

    public override void Write(Utf8JsonWriter writer, ScraperCacheAsset value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(value.Link);
        JsonSerializer.Serialize(writer, value.Build, BlinfoTypeInfo(options));
        writer.WriteEndArray();
    }

    private static JsonTypeInfo<BlinfoDocument> BlinfoTypeInfo(JsonSerializerOptions options) =>
        (JsonTypeInfo<BlinfoDocument>)options.GetTypeInfo(typeof(BlinfoDocument));
}
