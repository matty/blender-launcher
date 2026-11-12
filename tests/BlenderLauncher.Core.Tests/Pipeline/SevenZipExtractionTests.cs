using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;

namespace BlenderLauncher.Core.Tests.Pipeline;

/// <summary>
/// Fixtures/solid-sample.7z was made with 7-Zip 26.04 (<c>7z a -t7z -mx=9 -ms=on</c>): one solid LZMA2 block
/// holding upbge-sample/{readme.txt, blender.exe, 2.0/scripts/startup.py}.
/// </summary>
public sealed class SevenZipExtractionTests : IDisposable
{
    private readonly string _target = Path.Combine(Path.GetTempPath(), "bl-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Solid_archive_extracts_with_WriteToDirectory()
    {
        using var archive = SevenZipArchive.OpenArchive(Fixtures.PathOf("solid-sample.7z"), new SharpCompress.Readers.ReaderOptions());
        Assert.True(((SevenZipArchive)archive).IsSolid);

        // WriteToDirectory refuses a target folder that does not exist yet.
        Directory.CreateDirectory(_target);
        archive.WriteToDirectory(_target, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });

        Assert.Equal(620, new FileInfo(Path.Combine(_target, "upbge-sample", "readme.txt")).Length);
        Assert.Equal(1000, new FileInfo(Path.Combine(_target, "upbge-sample", "blender.exe")).Length);
        Assert.StartsWith(
            "print('hello from the fixture')",
            File.ReadAllText(Path.Combine(_target, "upbge-sample", "2.0", "scripts", "startup.py")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_target))
        {
            Directory.Delete(_target, recursive: true);
        }
    }
}
