using System.Text.Json;
using BlenderLauncher.Core.Serialization;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Api;

/// <summary>Answers whether a Blender series is LTS. Implemented by <see cref="ApiData"/>; queried at use, never cached.</summary>
public interface ILtsLookup
{
    /// <summary>True when the series <paramref name="major"/>.<paramref name="minor"/> is marked LTS.</summary>
    bool IsLts(int major, int minor);
}

/// <summary>One entry of <c>blender_versions</c>: a series and whether it is LTS.</summary>
public readonly record struct BlenderSeries(int Major, int Minor, bool IsLts)
{
    /// <summary><c>"M.m"</c>, as upstream keys the dropdown.</summary>
    public override string ToString() => $"{Major}.{Minor}";
}

/// <summary>
/// The hosted or bundled <c>blender_launcher_api.json</c>: its file version and the Blender
/// series list, newest first, each marked LTS or not. Immutable; a refresh produces a new instance, so holders
/// swap the reference rather than freezing the LTS list at start-up as upstream does.
/// </summary>
public sealed class ApiData : ILtsLookup
{
    private ApiData(ApiFileVersion? fileVersion, IReadOnlyList<BlenderSeries> series)
    {
        FileVersion = fileVersion;
        Series = series;
    }

    /// <summary>An instance with no series, for when no API file could be read.</summary>
    public static ApiData Empty { get; } = new(null, []);

    /// <summary>The file's <c>api_file_version</c>, or null when absent or not <c>major[.minor]</c>.</summary>
    public ApiFileVersion? FileVersion { get; }

    /// <summary>Every series in file order (newest first). The order drives the version dropdown index.</summary>
    public IReadOnlyList<BlenderSeries> Series { get; }

    /// <summary>The LTS series, in file order (upstream's <c>lts_blender_version</c>).</summary>
    public IEnumerable<BlenderSeries> LtsSeries => Series.Where(s => s.IsLts);

    /// <summary>Exact major and minor match, so 4.2 does not cover 4.20 (upstream used string <c>startswith</c>).</summary>
    public bool IsLts(int major, int minor) => Series.Any(s => s.IsLts && s.Major == major && s.Minor == minor);

    /// <summary>
    /// Parses the file's JSON. Throws <see cref="JsonException"/> when it is not valid JSON of that shape.
    /// A version key that does not parse (upstream would raise) is skipped. Only the exact value <c>"LTS"</c>
    /// marks a series LTS, as upstream.
    /// </summary>
    public static ApiData Parse(string json)
    {
        var doc = JsonSerializer.Deserialize(json, LauncherJsonContext.Default.ApiDataDocument)
                  ?? throw new JsonException("API file is the JSON literal null");
        var series = new List<BlenderSeries>();
        foreach (var (key, kind) in doc.BlenderVersions ?? [])
        {
            if (SemVer.TryParse(key, out var v, optionalMinorAndPatch: true))
            {
                series.Add(new BlenderSeries(v.Major, v.Minor, kind == "LTS"));
            }
        }

        return new ApiData(ApiFileVersion.TryParse(doc.ApiFileVersion, out var fileVersion) ? fileVersion : null, series);
    }

    /// <summary><see cref="Parse"/>, returning false instead of throwing.</summary>
    public static bool TryParse(string json, out ApiData data)
    {
        try
        {
            data = Parse(json);
            return true;
        }
        catch (JsonException)
        {
            data = Empty;
            return false;
        }
    }

    /// <summary>
    /// Upstream's <c>dropdown_blender_version</c>: <c>"M.m"</c> to its index in the file. A repeated series keeps
    /// its last index, as a Python dict comprehension would.
    /// </summary>
    public IReadOnlyDictionary<string, int> DropdownIndex()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Series.Count; i++)
        {
            map[Series[i].ToString()] = i;
        }

        return map;
    }
}

/// <summary>JSON shape of <c>blender_launcher_api.json</c>.</summary>
public sealed class ApiDataDocument
{
    public string? ApiFileVersion { get; set; }

    /// <summary>Series to <c>"LTS"</c> or <c>"non-LTS"</c>, in file order.</summary>
    public Dictionary<string, string?>? BlenderVersions { get; set; }
}
