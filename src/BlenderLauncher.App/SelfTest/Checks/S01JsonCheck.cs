using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.App.SelfTest.Checks;

/// <summary>
/// Spike S01: a sample <c>.blinfo</c> round-trips through the
/// source-generated <c>LauncherJsonContext</c> and <see cref="BuildInfo"/>, including an atomic write to disk;
/// upstream's bundled API file and stable cache (the copies embedded in Core, <see cref="BundledApiFiles"/>) load,
/// the cache through its custom
/// array converter; and the generated regexes of the version parsers run.
/// </summary>
internal sealed class S01JsonCheck : ISelfTestCheck
{
    // As upstream's json.dump writes it.
    private const string Sample =
        """{"file_version": "1.5", "blinfo": [{"branch": "daily", "subversion": "4.3.0-alpha", "build_hash": "ddc9f92777cd", "commit_time": "2024-07-30T12:34:00+00:00", "custom_name": "Mein Build é", "is_favorite": true, "custom_executable": null, "is_frozen": false}]}""";

    public string Id => "S01";
    public string Name => "Source-generated JSON (.blinfo, API data, stable cache)";
    public bool NeedsUiThread => false;
    public bool NeedsNetwork => false;

    public async Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct)
    {
        var first = BlinfoCodec.Read(Sample);
        var entry = first.Blinfo.Single();
        if (first.FileVersion != BlinfoCodec.CurrentFileVersion || entry.Subversion != "4.3.0-alpha" ||
            entry.CommitTime != "2024-07-30T12:34:00+00:00" || !entry.IsFavorite || entry.CustomName != "Mein Build é")
        {
            return SelfTestResult.Fail($"sample read wrongly: {entry}");
        }

        var build = BlinfoCodec.ToBuildInfo(entry, ctx.WorkDirectory);
        if (build.FullSemVersion?.ToString() != "4.3.0-alpha+daily.ddc9f92777cd" || build.CommitTime.Offset != TimeSpan.Zero)
        {
            return SelfTestResult.Fail($"sample became the wrong build: {build}");
        }

        // Write as the port does: temp file in the same folder, then rename over the target.
        await BlinfoCodec.WriteFileAsync(ctx.WorkDirectory, build, ct);
        var written = await File.ReadAllTextAsync(Path.Combine(ctx.WorkDirectory, BlinfoCodec.FileName), ct);
        var reread = await BlinfoCodec.ReadFileAsync(ctx.WorkDirectory, ct);
        ctx.Log(written);

        if (reread.FileVersion != first.FileVersion || reread.Blinfo.Single() != entry ||
            BlinfoCodec.ToBuildInfo(reread.Blinfo.Single(), ctx.WorkDirectory) != build)
        {
            return SelfTestResult.Fail($"round trip changed the entry: {reread.Blinfo.Single()}");
        }

        if (!written.Contains("\"custom_executable\":null", StringComparison.Ordinal) ||
            !written.Contains("\"is_favorite\":true", StringComparison.Ordinal) ||
            written.Any(c => c > 0x7F) ||
            Directory.GetFiles(ctx.WorkDirectory).Length != 1)
        {
            return SelfTestResult.Fail($"unexpected JSON shape or leftover temp file: {written}");
        }

        var api = ApiData.Parse(BundledApiFiles.ReadApi());
        if (api.Series.Count != 25 || !api.IsLts(4, 2) || api.IsLts(4, 20) || api.FileVersion != new ApiFileVersion(3, 0))
        {
            return SelfTestResult.Fail($"bundled API file read wrongly: {api.Series.Count} series");
        }

        var cache = ScraperCache.FromJson(BundledApiFiles.ReadStableSeed(), api);
        var assets = cache.Folders.Values.SelectMany(f => f.Assets).ToList();
        var again = ScraperCache.FromJson(cache.ToJson(), api);
        if (cache.Folders.Count != 54 || assets.Count != 255 || assets.Count(b => b.Branch == "lts") != 147 ||
            !cache.Folders.Values.All(f => f.ModifiedDate.IsNaive) ||
            again.ToJson() != cache.ToJson())
        {
            return SelfTestResult.Fail($"bundled stable cache read wrongly: {cache.Folders.Count} folders, {assets.Count} assets");
        }

        if (BlenderVersionParser.Parse("blender-3.3.21-stable+v33.e016c21db151-linux.x86_64-release.tar.xz", search: true).ToString()
                != "3.3.21-stable+v33.e016c21db151" ||
            BlenderVersionParser.Parse("2.79rc1").ToString() != "2.79.0-rc1" ||
            SemVer.Parse("5.2", optionalMinorAndPatch: true) != new SemVer(5, 2, 0))
        {
            return SelfTestResult.Fail("version parsers gave the wrong result");
        }

        return SelfTestResult.Pass(
            $".blinfo {written.Length} chars (snake_case, nulls kept, ASCII-only); API {api.Series.Count} series; " +
            $"stable cache {cache.Folders.Count} folders, {assets.Count} assets");
    }
}
