using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Pipeline;
using BlenderLauncher.Core.PlatformServices;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace BlenderLauncher.App.SelfTest.Checks;

/// <summary>
/// Spike S04: a small solid .7z (embedded fixture, made with 7-Zip) extracts with <c>WriteToDirectory</c>, with the
/// forward-only <c>ExtractAllEntries</c> reader, and through the install pipeline's <see cref="Extractor"/> (which uses
/// that reader for 7z); and
/// the zstd and gzip blobs from upstream's blend-reader test decode through <see cref="BlendFileHeaderReader"/>,
/// which uses SharpCompress's public <c>SharpCompress.Compressors.ZStandard.DecompressionStream</c>.
/// </summary>
internal sealed class S04SharpCompressCheck : ISelfTestCheck
{
    public string Id => "S04";
    public string Name => "SharpCompress: solid 7z and zstd";
    public bool NeedsUiThread => false;
    public bool NeedsNetwork => false;

    public async Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct)
    {
        var archivePath = Path.Combine(ctx.WorkDirectory, "solid-sample.7z");
        await CopyFixtureAsync("solid-sample.7z", archivePath, ct);
        var target = Directory.CreateDirectory(Path.Combine(ctx.WorkDirectory, "out")).FullName;

        bool solid;
        using (var archive = SevenZipArchive.OpenArchive(archivePath, new ReaderOptions()))
        {
            solid = ((SevenZipArchive)archive).IsSolid;
            archive.WriteToDirectory(target, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
        }

        var sizes = new[] { "readme.txt", "blender.exe", Path.Combine("2.0", "scripts", "startup.py") }
            .Select(p => new FileInfo(Path.Combine(target, "upbge-sample", p)))
            .Select(f => f.Exists ? f.Length : -1)
            .ToArray();
        if (!solid || !sizes.SequenceEqual([620L, 1000L, 960L]))
        {
            return SelfTestResult.Fail($"7z: solid={solid}, sizes={string.Join(',', sizes)}");
        }

        // The reader path: one forward pass over the solid block, as the Extractor does it.
        var readerTarget = Directory.CreateDirectory(Path.Combine(ctx.WorkDirectory, "reader")).FullName;
        var readerFiles = 0;
        using (var archive = SevenZipArchive.OpenArchive(archivePath, new ReaderOptions()))
        using (var reader = archive.ExtractAllEntries())
        {
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory)
                {
                    continue;
                }

                var file = Path.Combine(readerTarget, (reader.Entry.Key ?? "").Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                using var input = reader.OpenEntryStream();
                using var output = File.Create(file);
                await input.CopyToAsync(output, ct);
                readerFiles++;
            }
        }

        var readerSizes = SizesUnder(Path.Combine(readerTarget, "upbge-sample"));
        if (readerFiles != 3 || !readerSizes.SequenceEqual([620L, 1000L, 960L]))
        {
            return SelfTestResult.Fail($"7z reader: files={readerFiles}, sizes={string.Join(',', readerSizes)}");
        }

        // The product path: the install pipeline's Extractor into a staging folder, with progress.
        var staging = Directory.CreateDirectory(Path.Combine(ctx.WorkDirectory, "library", ".temp", "BlenderLauncherNet")).FullName;
        var stagedArchive = Path.Combine(staging, "upbge-sample.7z");
        File.Copy(archivePath, stagedArchive);
        var reports = 0;
        var staged = await new Extractor(NullRecycleBin.Instance).ExtractToStagingAsync(
            new ExtractRequest(stagedArchive, staging), new SyncProgress<TransferProgress>(_ => Interlocked.Increment(ref reports)), ct);
        var stagedSizes = SizesUnder(staged.BuildFolder);
        if (!stagedSizes.SequenceEqual([620L, 1000L, 960L]) || reports == 0)
        {
            return SelfTestResult.Fail($"Extractor: folder={staged.BuildFolder}, sizes={string.Join(',', stagedSizes)}, progress reports={reports}");
        }

        var headers = new List<string>();
        foreach (var (name, major, minor, format, compression) in new[]
                 {
                     ("blend-header-v0.gz", 4, 4, 0, BlendCompression.Gzip),
                     ("blend-header-v0.zst", 4, 4, 0, BlendCompression.Zstd),
                     ("blend-header-v1.zst", 5, 1, 1, BlendCompression.Zstd),
                 })
        {
            var path = Path.Combine(ctx.WorkDirectory, name);
            await CopyFixtureAsync(name, path, ct);
            var header = BlendFileHeaderReader.Read(path);
            if (header != new BlendFileHeader(major, minor, format, compression))
            {
                return SelfTestResult.Fail($"{name} decoded as {header}");
            }

            headers.Add($"{name}={header.Major}.{header.Minor}/v{header.FormatVersion}/{header.Compression}");
        }

        var detail = $"solid 7z extracted (3 files) by WriteToDirectory, ExtractAllEntries and the Extractor ({reports} progress reports); {string.Join("; ", headers)}";
        ctx.Log(detail);
        return SelfTestResult.Pass(detail);
    }

    private static long[] SizesUnder(string folder) =>
        new[] { "readme.txt", "blender.exe", Path.Combine("2.0", "scripts", "startup.py") }
            .Select(p => new FileInfo(Path.Combine(folder, p)))
            .Select(f => f.Exists ? f.Length : -1)
            .ToArray();

    private static async Task CopyFixtureAsync(string name, string path, CancellationToken ct)
    {
        await using var source = typeof(S04SharpCompressCheck).Assembly.GetManifestResourceStream("Fixtures/" + name)
                                 ?? throw new FileNotFoundException("embedded fixture missing", name);
        await using var file = File.Create(path);
        await source.CopyToAsync(file, ct);
    }
}
