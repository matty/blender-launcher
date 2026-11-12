namespace BlenderLauncher.Core.Tests.Net;

public class FixtureRecorderTests
{
    /// <summary>
    /// Records live fixtures. Explicit: it never runs in a normal test run. Run it with
    /// <c>dotnet test --project tests/BlenderLauncher.Core.Tests -- --explicit only --filter-method "*RecordLiveFixtures"</c>.
    /// <c>BL_RECORD_SOURCES</c> (comma-separated folder names under <c>Fixtures/Net</c>) limits it to some sources;
    /// otherwise every folder with a <c>record.json</c> is recorded. <c>BL_GITHUB_TOKEN</c> optionally authenticates
    /// GitHub API calls (it is never written to a fixture).
    /// </summary>
    [Fact(Explicit = true)]
    public async Task RecordLiveFixtures()
    {
        var root = FixtureRecorder.SourceFixturesRoot();
        var only = Environment.GetEnvironmentVariable("BL_RECORD_SOURCES")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var folders = Directory.GetDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, FixtureRecorder.RequestsFileName)))
            .Where(d => only is null || only.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(folders);

        foreach (var folder in folders)
        {
            var lines = await FixtureRecorder.RecordAsync(folder, Environment.GetEnvironmentVariable("BL_GITHUB_TOKEN"), TestContext.Current.CancellationToken);
            foreach (var line in lines)
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"[{Path.GetFileName(folder)}] {line}");
            }
        }
    }

    [Theory]
    [InlineData("https://builder.blender.org/download/daily/?format=json&v=1", "daily_format_json_v_1.body")]
    [InlineData("https://download.blender.org/release/", "release.body")]
    [InlineData("https://example.org/", "example.org")]
    [InlineData("https://github.com/UPBGE/upbge/releases/tag/v0.50", "v0.50.body")]
    [InlineData("https://www.bforartists.de/download/index.html", "index.html")]
    public void File_names_are_derived_from_the_url(string url, string expected)
    {
        Assert.Equal(expected, FixtureRecorder.FileNameFor(new Uri(url)));
    }
}
