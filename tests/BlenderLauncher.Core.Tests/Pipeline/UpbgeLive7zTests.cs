using System.Diagnostics;
using System.Text.Json;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Pipeline;

namespace BlenderLauncher.Core.Tests.Pipeline;

/// <summary>
/// Downloads a real UPBGE Windows <c>.7z</c> (BCJ/BCJ2 filters) and extracts it with <see cref="Extractor"/>.
/// Runs only with <c>BL_LIVE_7Z=1</c>:
/// <code>$env:BL_LIVE_7Z = "1"; dotnet test --project tests/BlenderLauncher.Core.Tests -- --filter-method "*Real_upbge_7z*"</code>
/// The newest release with a Windows <c>.7z</c> is used unless it is over 400 MB, in which case the newest
/// smaller one; <c>BL_LIVE_7Z_URL</c> names an asset instead. <c>BL_LIVE_7Z_DIR</c> picks the scratch folder (default: the temp folder); <c>BL_LIVE_7Z_KEEP=1</c>
/// keeps the archive there for inspection. The test's notes are appended to <c>bl-live-7z.txt</c> there.
/// </summary>
public sealed class UpbgeLive7zTests
{
    private const long MaxBytes = 400L * 1024 * 1024;

    [Fact]
    public async Task Real_upbge_7z_extracts_with_blender_exe()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("BL_LIVE_7Z") == "1", "Set BL_LIVE_7Z=1 to download and extract a real UPBGE .7z");
        var ct = TestContext.Current.CancellationToken;
        var lines = new List<string>();
        void Note(string line)
        {
            lines.Add(line);
            TestContext.Current.TestOutputHelper?.WriteLine(line);
        }

        var options = ConnectionOptions.Default;
        using var http = HttpClientProvider.CreateClient(options, HttpClientProvider.CreatePrimaryHandler(options));

        var (tag, name, size, url) = Environment.GetEnvironmentVariable("BL_LIVE_7Z_URL") is { Length: > 0 } fixedUrl
            ? ("(BL_LIVE_7Z_URL)", ArchiveNames.FromUrl(new Uri(fixedUrl)), 0L, fixedUrl)
            : await FindAssetAsync(http, ct);
        Note($"Asset: {tag} {name} {size:N0} bytes ({size / 1048576.0:F1} MB) {url}");

        var scratch = Environment.GetEnvironmentVariable("BL_LIVE_7Z_DIR") is { Length: > 0 } dir ? dir : Path.GetTempPath();
        var library = Path.Combine(scratch, "bl-live-7z-" + Guid.NewGuid().ToString("N")[..8]);
        var staging = Path.Combine(library, ".temp", AppIdentity.TechnicalId);
        try
        {
            var watch = Stopwatch.StartNew();
            var archive = await new Downloader(() => http).DownloadAsync(new DownloadRequest(new Uri(url), staging) { ExpectedSize = size > 0 ? size : null }, null, ct);
            Note($"Downloaded in {watch.Elapsed.TotalSeconds:F1} s");
            if (Environment.GetEnvironmentVariable("BL_LIVE_7Z_KEEP") == "1")
            {
                var kept = Path.Combine(scratch, name);
                File.Copy(archive, kept, overwrite: true);
                Note($"Kept a copy at {kept}");
            }

            watch.Restart();
            long lastTotal = 0;
            var progressReports = 0;
            var result = await new Extractor(BlenderLauncher.Core.PlatformServices.NullRecycleBin.Instance).ExtractAsync(
                new ExtractRequest(archive, staging), Path.Combine(library, "upbge-stable"), isUpbge: true,
                new SyncProgress<TransferProgress>(p =>
                {
                    progressReports++;
                    lastTotal = p.Total ?? lastTotal;
                }), ct);
            var files = Directory.EnumerateFiles(result.BuildFolder, "*", SearchOption.AllDirectories).ToList();
            Note($"Extracted in {watch.Elapsed.TotalSeconds:F1} s into {result.BuildFolder}: {files.Count} files, " +
                              $"{files.Sum(f => new FileInfo(f).Length):N0} bytes (archive said {lastTotal:N0}); {progressReports} progress reports");

            var exe = Path.Combine(result.BuildFolder, "blender.exe");
            Assert.True(File.Exists(exe), $"no blender.exe in {result.BuildFolder}");
            Assert.True(new FileInfo(exe).Length > 1_000_000);
            var header = new byte[2];
            await using (var stream = File.OpenRead(exe))
            {
                await stream.ReadExactlyAsync(header, ct);
            }

            Assert.Equal("MZ"u8.ToArray(), header);
        }
        finally
        {
            // The runner hides a passing test's output, so the notes also go beside the scratch folder.
            await File.AppendAllLinesAsync(Path.Combine(scratch, "bl-live-7z.txt"), lines, CancellationToken.None);
            BlenderLauncher.Core.PlatformServices.PermanentDelete.Run(library);
        }
    }

    private static async Task<(string Tag, string Name, long Size, string Url)> FindAssetAsync(HttpClient http, CancellationToken ct)
    {
        var candidates = new List<(string Tag, DateTimeOffset Published, string Name, long Size, string Url)>();
        for (var page = 1; page <= 3; page++)
        {
            using var response = await http.GetAsync($"https://api.github.com/repos/UPBGE/upbge/releases?per_page=100&page={page}", ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var release in doc.RootElement.EnumerateArray())
            {
                var tag = release.GetProperty("tag_name").GetString()!;
                var published = release.GetProperty("published_at").GetDateTimeOffset();
                foreach (var asset in release.GetProperty("assets").EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString()!;
                    if (name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
                        && name.Contains("windows", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("windows7", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("32", StringComparison.Ordinal))
                    {
                        candidates.Add((tag, published, name, asset.GetProperty("size").GetInt64(), asset.GetProperty("browser_download_url").GetString()!));
                    }
                }
            }
        }

        Assert.NotEmpty(candidates);
        var ordered = candidates.OrderByDescending(c => c.Published).ToList();
        var pick = ordered.FirstOrDefault(c => c.Size <= MaxBytes);
        Assert.NotNull(pick.Url);
        return (pick.Tag, pick.Name, pick.Size, pick.Url);
    }
}
