namespace BlenderLauncher.Core.Api;

/// <summary>
/// Upstream's API files as shipped inside Core (embedded as <c>Api/&lt;file&gt;</c>; copied unchanged from upstream's
/// <c>source/resources/api/</c>). They are the fallback when no fetched copy exists or it cannot be read.
/// </summary>
public static class BundledApiFiles
{
    /// <summary>The LTS table's file name, hosted, bundled and stored alike.</summary>
    public const string ApiFileName = "blender_launcher_api.json";

    /// <summary>The Windows stable-build seed's hosted and bundled file name.</summary>
    public const string StableSeedFileName = "stable_builds_api_windows.json";

    /// <summary>Reads the bundled <c>blender_launcher_api.json</c>.</summary>
    public static string ReadApi() => Read(ApiFileName);

    /// <summary>Reads the bundled Windows stable-build seed (about 210 KB).</summary>
    public static string ReadStableSeed() => Read(StableSeedFileName);

    private static string Read(string fileName)
    {
        var name = "Api/" + fileName;
        using var stream = typeof(BundledApiFiles).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Embedded resource {name} is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
