using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.Core.Tests.Builds;

public class BlinfoCodecTests
{
    // As upstream's json.dump writes it, including its ", " and ": " separators.
    private const string UpstreamSample =
        """{"file_version": "1.5", "blinfo": [{"branch": "daily", "subversion": "4.3.0-alpha", "build_hash": "ddc9f92777cd", "commit_time": "2024-07-30T12:34:00+00:00", "custom_name": "", "is_favorite": false, "custom_executable": null, "is_frozen": false}]}""";

    private static readonly TimeZoneInfo PlusTwo =
        TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

    private static readonly FixedTime Time = new(new DateTimeOffset(2026, 11, 11, 9, 30, 0, TimeSpan.Zero), PlusTwo);

    /// <summary>A clock fixed at one instant in a fixed zone.</summary>
    private sealed class FixedTime(DateTimeOffset utcNow, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone => zone;
    }

    [Fact]
    public void Upstream_sample_round_trips()
    {
        var document = BlinfoCodec.Read(UpstreamSample);

        Assert.Equal(BlinfoCodec.CurrentFileVersion, document.FileVersion);
        var entry = Assert.Single(document.Blinfo);
        Assert.Equal(
            new BlinfoEntry
            {
                Branch = "daily",
                Subversion = "4.3.0-alpha",
                BuildHash = "ddc9f92777cd",
                CommitTime = "2024-07-30T12:34:00+00:00",
                CustomName = "",
                IsFavorite = false,
                CustomExecutable = null,
                IsFrozen = false,
            },
            entry);

        // The default encoder writes ASCII only, as upstream's json.dump (ensure_ascii) does, so upstream can
        // read the file back with its locale encoding. It also escapes "+", which every JSON reader accepts.
        var written = BlinfoCodec.Write(document);
        Assert.Equal(UpstreamSample.Replace(": ", ":").Replace(", ", ",").Replace("+", "\\u002B"), written);
        Assert.Equal(entry, Assert.Single(BlinfoCodec.Read(written).Blinfo));
    }

    [Fact]
    public void Upstream_sample_becomes_a_build_and_writes_back_unchanged()
    {
        var entry = BlinfoCodec.Read(UpstreamSample).Blinfo[0];
        var build = BlinfoCodec.ToBuildInfo(entry, "C:/Library/daily/blender-4.3.0-alpha+daily.ddc9f92777cd");

        Assert.Equal("daily", build.Branch);
        Assert.Equal("4.3.0-alpha", build.Subversion);
        Assert.Equal("ddc9f92777cd", build.BuildHash);
        Assert.Equal(new DateTimeOffset(2024, 7, 30, 12, 34, 0, TimeSpan.Zero), build.CommitTime.ToDateTimeOffset());
        Assert.Null(build.CustomExecutable);
        Assert.Equal("4.3.0-alpha+daily.ddc9f92777cd", build.FullSemVersion.ToString());

        var document = BlinfoCodec.ToDocument(build);
        Assert.Equal("1.5", document.FileVersion);
        Assert.Equal(entry, Assert.Single(document.Blinfo));
    }

    [Fact]
    public void Missing_keys_take_upstreams_from_dict_defaults()
    {
        var document = BlinfoCodec.Read("""{"blinfo": [{"branch": "stable"}]}""");

        Assert.Null(document.FileVersion);
        var entry = Assert.Single(document.Blinfo);
        Assert.Equal("stable", entry.Branch);
        Assert.Equal("", entry.Subversion);
        Assert.Null(entry.BuildHash);
        Assert.Equal("", entry.CustomName);
        Assert.Equal("", entry.CustomExecutable); // from_dict's default is "", not None
        Assert.False(entry.IsFavorite);
        Assert.False(entry.IsFrozen);

        var build = BlinfoCodec.ToBuildInfo(entry, "x", time: Time);
        Assert.False(build.IsValid);
        Assert.Equal(Time.GetLocalNow(), build.CommitTime.ToDateTimeOffset()); // missing commit time: now
    }

    [Theory]
    [InlineData("\"custom_executable\": null", null)]
    [InlineData("\"custom_executable\": \"\"", "")]
    [InlineData("\"custom_executable\": \"bforartists.exe\"", "bforartists.exe")]
    public void Custom_executable_reads_and_writes_back_as_written(string json, string? expected)
    {
        var entry = BlinfoCodec.Read($$"""{"file_version": "1.5", "blinfo": [{"branch": "bforartists", "subversion": "4.5.0", {{json}}}]}""").Blinfo[0];
        var build = BlinfoCodec.ToBuildInfo(entry, "x", time: Time);

        Assert.Equal(expected, build.CustomExecutable);
        var written = BlinfoCodec.Write(BlinfoCodec.ToDocument(build));
        Assert.Contains(json.Replace(" ", ""), written, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2024-07-30T12:34:00+00:00", "2024-07-30T12:34:00+00:00")] // ISO, offset kept
    [InlineData("2024-07-30T12:34:00", "2024-07-30T12:34:00")]             // ISO without offset stays naive
    [InlineData("30-Jul-24-12:34", "2024-07-30T12:34:00+02:00")]           // legacy %d-%b-%y-%H:%M, read as local
    [InlineData("5-jul-24-9:05", "2024-07-05T09:05:00+02:00")]
    [InlineData("01-Jan-69-00:00", "1969-01-01T00:00:00+02:00")]           // Python's %y pivot
    [InlineData("01-Jan-68-00:00", "2068-01-01T00:00:00+02:00")]
    [InlineData("26-Mar-2024 10:57", "2024-03-26T10:57:00+02:00")]         // dateparser stand-ins
    [InlineData("2024/07/30 12:34", "2024-07-30T12:34:00+02:00")]
    [InlineData("30 Jul 2024 12:34", "2024-07-30T12:34:00+02:00")]
    [InlineData("Tue, 30 Jul 2024 12:34:00 GMT", "2024-07-30T14:34:00+02:00")]
    [InlineData("2024-07-30 12:34:00 -05:00", "2024-07-30T19:34:00+02:00")]
    [InlineData("yesterday", "2026-11-11T11:30:00+02:00")]                 // unparsable: now
    [InlineData(null, "2026-11-11T11:30:00+02:00")]
    public void Commit_time_is_read_tolerantly(string? text, string expected) =>
        Assert.Equal(expected, BlinfoCodec.ParseCommitTime(text, Time).ToString());

    [Fact]
    public void Lts_promotion_is_applied_on_read_when_a_table_is_given()
    {
        var entry = new BlinfoEntry { Branch = "stable", Subversion = "4.2.3", CommitTime = "2024-07-30T12:34:00+00:00" };
        var api = ApiData.Parse(Fixtures.ReadText("upstream-api-blender_launcher_api.json"));

        Assert.Equal("stable", BlinfoCodec.ToBuildInfo(entry, "x").Branch);
        Assert.Equal("lts", BlinfoCodec.ToBuildInfo(entry, "x", api).Branch);
    }

    [Fact]
    public async Task File_write_is_atomic_and_round_trips()
    {
        var folder = Path.Combine(Path.GetTempPath(), "bl-blinfo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var build = BlinfoCodec.ToBuildInfo(BlinfoCodec.Read(UpstreamSample).Blinfo[0], folder) with
            {
                IsFavorite = true,
                CustomName = "Mein Build é",
            };

            await BlinfoCodec.WriteFileAsync(folder, build, TestContext.Current.CancellationToken);
            await BlinfoCodec.WriteFileAsync(folder, build with { IsFrozen = true }, TestContext.Current.CancellationToken);

            Assert.Equal([".blinfo"], Directory.GetFiles(folder).Select(Path.GetFileName));
            var text = await File.ReadAllTextAsync(Path.Combine(folder, ".blinfo"), TestContext.Current.CancellationToken);
            Assert.All(text, c => Assert.True(c < 0x80));

            var document = await BlinfoCodec.ReadFileAsync(folder, TestContext.Current.CancellationToken);
            var reread = BlinfoCodec.ToBuildInfo(document.Blinfo.Single(), folder);
            Assert.Equal(build with { IsFrozen = true }, reread);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
