using System.Text.Json;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;

namespace BlenderLauncher.Core.Tests.Library;

/// <summary>
/// A library laid out and written exactly as upstream writes it (<c>json.dump(to_dict())</c>, Python's
/// default separators) is recognised build by build, and the <c>.blinfo</c> files the port rewrites (favourite,
/// rename, freeze) read back with the same meaning. Set <c>BLENDER_LAUNCHER_ROUNDTRIP_DIR</c> to keep the library
/// in that folder, so upstream's reader (or a script that follows its <c>from_dict</c>) can be run over it.
/// </summary>
public sealed class UpstreamLibraryRoundTripTests : IDisposable
{
    private const string KeepVariable = "BLENDER_LAUNCHER_ROUNDTRIP_DIR";
    private readonly TempDir _temp = new();
    private readonly string _root;

    public UpstreamLibraryRoundTripTests()
    {
        var keep = Environment.GetEnvironmentVariable(KeepVariable);
        _root = string.IsNullOrWhiteSpace(keep) ? _temp.Folder("Upstream library") : Path.GetFullPath(keep);
        Directory.CreateDirectory(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    // (folder relative to the library, .blinfo text as upstream's json.dump writes it, executable)
    private static readonly (string Folder, string Blinfo, string Exe)[] Builds =
    [
        ("stable/blender-4.2.3-lts.2a1d9b8b4e2b",
            """{"file_version": "1.5", "blinfo": [{"branch": "lts", "subversion": "4.2.3", "build_hash": "2a1d9b8b4e2b", "commit_time": "2024-10-14T10:53:00+00:00", "custom_name": "", "is_favorite": false, "custom_executable": null, "is_frozen": false}]}""",
            "blender.exe"),
        ("daily/blender-4.4.0-alpha+daily.ddc9f92777cd",
            """{"file_version": "1.5", "blinfo": [{"branch": "daily", "subversion": "4.4.0-alpha", "build_hash": "ddc9f92777cd", "commit_time": "2024-11-20T12:34:00+01:00", "custom_name": "My daily", "is_favorite": true, "custom_executable": "", "is_frozen": true}]}""",
            "blender.exe"),
        ("bforartists/Bforartists-5.3.0",
            """{"file_version": "1.5", "blinfo": [{"branch": "bforartists", "subversion": "5.3.0-dev", "build_hash": "abc123", "commit_time": "2026-10-19T00:00:00+00:00", "custom_name": "", "is_favorite": false, "custom_executable": "bforartists.exe", "is_frozen": false}]}""",
            "bforartists.exe"),
        ("experimental/blender-4.4.0-alpha+PR12345.0123456789ab",
            """{"file_version": "1.5", "blinfo": [{"branch": "PR12345", "subversion": "4.4.0-alpha", "build_hash": null, "commit_time": "2024-11-01T08:00:00", "custom_name": "", "is_favorite": false, "custom_executable": "", "is_frozen": false}]}""",
            "blender.exe"),
    ];

    private async Task<LibraryService> ScanAsync(LibraryState state)
    {
        var library = new LibraryService(new BuildInfoReader(FakeProbe.Failing()), state);
        await library.ScanAsync(_root, ct: Ct);
        return library;
    }

    [Fact]
    public async Task Upstream_library_is_recognised_and_port_rewrites_keep_its_meaning()
    {
        foreach (var name in Channels.LibraryFolders)
        {
            Directory.CreateDirectory(Path.Combine(_root, name));
        }

        foreach (var (folder, blinfo, exe) in Builds)
        {
            var path = Path.Combine(_root, folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, ".blinfo"), blinfo);
            File.WriteAllBytes(Path.Combine(path, exe), []);
        }

        var state = await LibraryState.LoadAsync(Path.Combine(_temp.Path, "state"), ct: Ct);
        await using var _ = state;
        using var library = await ScanAsync(state);

        // Every build recognised and read without re-deriving (no probe call would succeed).
        Assert.Equal(Builds.Length, library.Current.Entries.Length);
        Assert.All(library.Current.Entries, e => Assert.Equal(LibraryEntryKind.Build, e.Kind));
        var before = library.Current.Builds.ToDictionary(b => Path.GetRelativePath(_root, b.Link).Replace('\\', '/'));
        foreach (var (folder, blinfo, _) in Builds)
        {
            Assert.Equal(UpstreamMeaning(blinfo), Meaning(before[folder]));
        }

        // The port rewrites every file: favourite and frozen toggled, the name extended.
        foreach (var build in before.Values)
        {
            await library.SetFavouriteAsync(build.Link, !build.IsFavorite, Ct);
            await library.SetCustomNameAsync(build.Link, build.CustomName + " (port)", Ct);
            await library.SetFrozenAsync(build.Link, !build.IsFrozen, Ct);
        }

        foreach (var (folder, blinfo, _) in Builds)
        {
            var path = Path.Combine(_root, folder.Replace('/', Path.DirectorySeparatorChar), ".blinfo");
            var rewritten = File.ReadAllText(path);
            var expected = UpstreamMeaning(blinfo) with
            {
                IsFavorite = !UpstreamMeaning(blinfo).IsFavorite,
                CustomName = UpstreamMeaning(blinfo).CustomName + " (port)",
                IsFrozen = !UpstreamMeaning(blinfo).IsFrozen,
            };

            // Read the way upstream's from_dict does, straight from the JSON.
            Assert.Equal(expected, UpstreamMeaning(rewritten));
            using var json = JsonDocument.Parse(rewritten);
            Assert.Equal("1.5", json.RootElement.GetProperty("file_version").GetString());
            Assert.Equal(
                ["branch", "subversion", "build_hash", "commit_time", "custom_name", "is_favorite", "custom_executable", "is_frozen"],
                json.RootElement.GetProperty("blinfo")[0].EnumerateObject().Select(p => p.Name));
        }

        // And a fresh scan by the port reads the rewritten files with the same meaning.
        using var again = await ScanAsync(state);
        foreach (var (folder, _, _) in Builds)
        {
            var path = Path.Combine(_root, folder.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(UpstreamMeaning(File.ReadAllText(Path.Combine(path, ".blinfo"))), Meaning(again.Current.Find(path)!.Build!));
        }
    }

    /// <summary>The fields upstream's <c>from_dict</c> builds, with its defaults, and the commit time as Python's <c>fromisoformat</c> reads it.</summary>
    private sealed record BlinfoMeaning(
        string Branch,
        string Subversion,
        string? BuildHash,
        string CommitTime,
        string CustomName,
        bool IsFavorite,
        string? CustomExecutable,
        bool IsFrozen);

    private static BlinfoMeaning UpstreamMeaning(string text)
    {
        using var json = JsonDocument.Parse(text);
        var b = json.RootElement.GetProperty("blinfo")[0];
        string? Str(string name, string? fallback) =>
            b.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Null ? null : v.GetString() : fallback;
        bool Bool(string name) => b.TryGetProperty(name, out var v) && v.GetBoolean();
        return new BlinfoMeaning(
            Str("branch", "")!,
            Str("subversion", "")!,
            Str("build_hash", null),
            IsoTimestamp.Parse(Str("commit_time", "")!).ToString(),
            Str("custom_name", "")!,
            Bool("is_favorite"),
            Str("custom_executable", ""),
            Bool("is_frozen"));
    }

    private static BlinfoMeaning Meaning(BuildInfo b) =>
        new(b.Branch, b.Subversion, b.BuildHash, b.CommitTime.ToString(), b.CustomName, b.IsFavorite, b.CustomExecutable, b.IsFrozen);
}
