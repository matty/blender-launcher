using System.Text.Json;
using System.Text.Json.Serialization;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Net;

namespace BlenderLauncher.Core.Settings;

/// <summary>
/// Source-generated JSON for <c>settings.json</c>: camelCase members, indented, enums as camelCase names.
/// Kept apart from <c>LauncherJsonContext</c>, whose snake_case policy belongs to upstream's file formats.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DictionaryKeyPolicy = JsonKnownNamingPolicy.Unspecified,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    GenerationMode = JsonSourceGenerationMode.Default,
    Converters =
    [
        typeof(LenientEnumConverter<DeleteAction>),
        typeof(LenientEnumConverter<OnLaunchAction>),
        typeof(LenientEnumConverter<UpdateBehaviour>),
        typeof(LenientEnumConverter<NewDownloadDefault>),
        typeof(LenientEnumConverter<Accent>),
        typeof(LenientEnumConverter<Density>),
        typeof(LenientEnumConverter<SortColumn>),
        typeof(LenientEnumConverter<LogLevel>),
        typeof(LenientEnumConverter<ProxyType>),
    ])]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Enums as camelCase names. Reading accepts any case and a number; a name this build does not know (written by
/// a newer version) reads as the enum's zero value instead of failing the whole file, and
/// <see cref="SettingsValidation"/> then puts the default in its place.
/// </summary>
internal sealed class LenientEnumConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var value) ? value : default;
            case JsonTokenType.Number when reader.TryGetInt32(out var number):
                return Enum.TryParse<T>(number.ToString(System.Globalization.CultureInfo.InvariantCulture), out var n) ? n : default;
            default:
                reader.Skip();
                return default;
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        var name = value.ToString();
        writer.WriteStringValue(name.Length > 0 && char.IsUpper(name[0]) ? char.ToLowerInvariant(name[0]) + name[1..] : name);
    }
}
