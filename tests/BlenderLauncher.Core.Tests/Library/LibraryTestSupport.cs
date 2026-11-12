using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;

namespace BlenderLauncher.Core.Tests.Library;

/// <summary>A temp folder deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bl-lib-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Creates a folder (and parents) under the temp folder and returns its path.</summary>
    public string Folder(params string[] parts)
    {
        var path = Combine(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a file (creating folders) and returns its path.</summary>
    public string File(string relative, string content = "")
    {
        var path = Combine(relative.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A probe returning canned <c>-v</c> output per executable file name, or failing.</summary>
internal sealed class FakeProbe : IBlenderVersionProbe
{
    private readonly Func<string, string> _output;

    public FakeProbe(string output) : this(_ => output)
    {
    }

    public FakeProbe(Func<string, string> output) => _output = output;

    public List<string> Calls { get; } = [];

    public Task<string> RunAsync(string executable, TimeSpan timeout, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add(executable);
        }

        return Task.FromResult(_output(executable));
    }

    public static FakeProbe Failing(bool timedOut = false) =>
        new(_ => throw new BlenderProbeException(timedOut ? "timed out" : "crashed") { TimedOut = timedOut });
}

internal sealed class FixedLts(params (int Major, int Minor)[] series) : ILtsLookup
{
    public bool IsLts(int major, int minor) => series.Contains((major, minor));
}

/// <summary>Fixed clock in UTC.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

internal static class Samples
{
    // The shape of real `blender.exe -v` output on Windows (4.2 LTS), tabs and CRLF included.
    public const string Blender42Lts =
        "Blender 4.2.3 LTS\r\n" +
        "\tbuild date: 2024-10-15\r\n" +
        "\tbuild time: 01:12:45\r\n" +
        "\tbuild commit date: 2024-10-14\r\n" +
        "\tbuild commit time: 10:53\r\n" +
        "\tbuild hash: 2a1d9b8b4e2b\r\n" +
        "\tbuild branch: blender-v4.2-release\r\n" +
        "\tbuild platform: Windows\r\n" +
        "\tbuild type: Release\r\n" +
        "\tbuild c flags:  /W3 /w34062 /wd4018\r\n" +
        "\tbuild system: CMake\r\n\r\n";

    public const string BlenderDaily =
        "Blender 4.3.0 Alpha\r\n" +
        "\tbuild date: 2024-07-30\r\n" +
        "\tbuild commit date: 2024-07-30\r\n" +
        "\tbuild commit time: 12:34\r\n" +
        "\tbuild hash: ddc9f92777cd\r\n" +
        "\tbuild branch: main\r\n";

    // Upstream's stub from tests/backend/test_bfa_scraper.py:114-127.
    public const string Bforartists = "Bforartists 5.3.0\nbuild hash: abc123\n";

    // A fork whose first line names neither Blender nor Bforartists.
    public const string CustomFork = "Mesa Fork 3.6.2\n\tbuild hash: feedbeef\n";

    public static BuildInfo Info(string subversion = "4.3.0-alpha", string branch = "daily", string? hash = "ddc9f92777cd") => new()
    {
        Link = "x",
        Subversion = subversion,
        BuildHash = hash,
        CommitTime = IsoTimestamp.Parse("2024-07-30T12:34:00+00:00"),
        Branch = branch,
    };

    public static Task WriteBlinfoAsync(string folder, BuildInfo info) => BlinfoCodec.WriteFileAsync(folder, info with { Link = folder });
}
