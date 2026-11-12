using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Scraping.Stable;

namespace BlenderLauncher.App.SelfTest.Checks;

/// <summary>
/// Spike S02: saved pages (embedded from tests/BlenderLauncher.Core.Tests/Fixtures) parse with AngleSharp's
/// <c>a[href]</c> and <c>details</c> selectors: the download.blender.org release index and both Bforartists indexes.
/// </summary>
internal sealed class S02AngleSharpCheck : ISelfTestCheck
{
    public string Id => "S02";
    public string Name => "AngleSharp parses saved index pages";
    public bool NeedsUiThread => false;
    public bool NeedsNetwork => false;

    public async Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct)
    {
        var folders = StableIndexParser.ParseIndex(await ReadFixtureAsync("download-blender-org-release-index.html", ct));
        var blender41 = folders.FirstOrDefault(f => f.Href == "Blender4.1/");
        if (folders.Count != 90 || blender41 is not { Version: "4.1", Modified: "16-Apr-2024 08:42" })
        {
            return SelfTestResult.Fail($"release index: {folders.Count} folders, Blender4.1 = {blender41}");
        }

        var stable = BfaIndexParser.Parse(
            await ReadFixtureAsync("bforartists-downloads.html", ct), new Uri("https://downloads.bforartists.com/"));
        var windowsZip = new Uri("https://downloads.bforartists.com/Bforartists-5.2.3-Windows.zip");
        if (stable.Count < 40 || !stable.Any(s => s.Title.StartsWith("Bforartists 5.2.3", StringComparison.Ordinal) && s.Links.Contains(windowsZip)))
        {
            return SelfTestResult.Fail($"Bforartists stable: {stable.Count} sections, 5.2.3 Windows zip not found");
        }

        var dev = BfaIndexParser.Parse(
            await ReadFixtureAsync("bforartists-devbuilds.html", ct), new Uri("https://devbuilds.bforartists.com/"));
        if (!dev.Any(s => s.Title.StartsWith("2026-11-10", StringComparison.Ordinal) && s.Links.Count > 0))
        {
            return SelfTestResult.Fail($"Bforartists dev: {dev.Count} sections, 2026-11-10 not found");
        }

        var detail = $"release index {folders.Count} folders (Blender4.1 {blender41.Modified}); " +
                     $"bfa stable {stable.Count} sections/{stable.Sum(s => s.Links.Count)} links; " +
                     $"bfa dev {dev.Count} sections/{dev.Sum(s => s.Links.Count)} links";
        ctx.Log(detail);
        return SelfTestResult.Pass(detail);
    }

    private static async Task<string> ReadFixtureAsync(string name, CancellationToken ct)
    {
        await using var stream = typeof(S02AngleSharpCheck).Assembly.GetManifestResourceStream("Fixtures/" + name)
                                 ?? throw new FileNotFoundException("embedded fixture missing", name);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
}
