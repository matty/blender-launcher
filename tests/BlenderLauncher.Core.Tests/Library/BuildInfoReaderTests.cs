using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;

namespace BlenderLauncher.Core.Tests.Library;

public class BuildInfoReaderTests
{
    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));

    private static BuildInfoReader Reader(IBlenderVersionProbe probe, params (int, int)[] lts) =>
        new(probe, new FixedLts(lts), time: Clock);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stable_build_from_probe_output_becomes_lts_and_is_written()
    {
        using var temp = new TempDir();
        temp.File("stable/blender-4.2.3-windows-x64/blender.exe");
        var folder = temp.Combine("stable", "blender-4.2.3-windows-x64");

        var build = await Reader(new FakeProbe(Samples.Blender42Lts), (4, 2)).ReadAsync(folder, ct: Ct);

        Assert.Equal("4.2.3 LTS", build.Subversion);
        Assert.Equal("2a1d9b8b4e2b", build.BuildHash);
        Assert.Equal("2024-10-14T10:53:00+00:00", build.CommitTime.ToString());
        Assert.Equal("lts", build.Branch);
        Assert.Equal("", build.CustomName);
        Assert.Null(build.CustomExecutable);
        Assert.Equal(folder, build.Link);

        var written = await BlinfoCodec.ReadFileAsync(folder, Ct);
        Assert.Equal("1.5", written.FileVersion);
        Assert.Equal("lts", written.Blinfo[0].Branch);
    }

    [Fact]
    public async Task Supplied_info_wins_for_subversion_and_time_hash_comes_from_probe()
    {
        using var temp = new TempDir();
        var folder = temp.Folder("daily", "blender-4.3.0-alpha+daily.ddc9f92777cd");
        temp.File("daily/blender-4.3.0-alpha+daily.ddc9f92777cd/blender.exe");
        var scraped = Samples.Info(hash: "scraped") with { CommitTime = IsoTimestamp.Parse("2024-08-01T00:00:00+02:00") };

        var build = await Reader(new FakeProbe(Samples.BlenderDaily)).ReadAsync(folder, info: scraped, ct: Ct);

        Assert.Equal("4.3.0-alpha", build.Subversion);
        Assert.Equal("2024-08-01T00:00:00+02:00", build.CommitTime.ToString());
        Assert.Equal("ddc9f92777cd", build.BuildHash);
        Assert.Equal("daily", build.Branch);
    }

    [Fact]
    public async Task Bforartists_executable_is_preferred_and_upstream_stub_matches()
    {
        using var temp = new TempDir();
        temp.File("bforartists/Bforartists-5.3.0-dev/bforartists.exe");
        temp.File("bforartists/Bforartists-5.3.0-dev/blender.exe");
        var folder = temp.Combine("bforartists", "Bforartists-5.3.0-dev");
        var info = Samples.Info("5.3.0-dev", "bforartists", null) with { CustomExecutable = "bforartists.exe" };
        var probe = new FakeProbe(Samples.Bforartists);

        var build = await Reader(probe).ReadAsync(folder, info: info, ct: Ct);

        Assert.EndsWith("bforartists.exe", Assert.Single(probe.Calls));
        Assert.Equal("5.3.0-dev", build.Subversion);
        Assert.Equal(info.CommitTime, build.CommitTime);
        Assert.Equal("abc123", build.BuildHash);
        Assert.Equal("bforartists.exe", build.CustomExecutable);
        Assert.Equal("bforartists", build.Branch);
    }

    [Fact]
    public async Task Bforartists_subversion_parsed_without_info()
    {
        using var temp = new TempDir();
        temp.File("bforartists/bfa/bforartists.exe");

        var build = await Reader(new FakeProbe(Samples.Bforartists)).ReadAsync(temp.Combine("bforartists", "bfa"), ct: Ct);

        Assert.Equal("5.3.0", build.Subversion);
        Assert.Equal("2026-11-11T09:00:00+00:00", build.CommitTime.ToString()); // no commit date/time: now
    }

    [Fact]
    public async Task Upbge_takes_branch_from_folder_and_info_subversion()
    {
        using var temp = new TempDir();
        temp.File("upbge-weekly/upbge-0.50/blender.exe");
        var info = Samples.Info("0.50.0-alpha", "upbge-weekly", null);

        var build = await Reader(new FakeProbe("Blender 5.0.0 Alpha\n\tbuild hash: 0a1b2c3d\n"))
            .ReadAsync(temp.Combine("upbge-weekly", "upbge-0.50"), info: info, ct: Ct);

        Assert.Equal("upbge-weekly", build.Branch);
        Assert.Equal("0.50.0-alpha", build.Subversion);
        Assert.Equal("0a1b2c3d", build.BuildHash);
    }

    [Fact]
    public async Task Custom_fork_first_line_gives_name_and_version_and_branch_is_folder_name()
    {
        using var temp = new TempDir();
        temp.File("custom/MyFork/blender.exe");

        var build = await Reader(new FakeProbe(Samples.CustomFork)).ReadAsync(temp.Combine("custom", "MyFork"), ct: Ct);

        Assert.Equal("MyFork", build.Branch);
        Assert.Equal("Mesa Fork", build.CustomName);
        Assert.Equal("3.6.2", build.Subversion);
        Assert.Equal("feedbeef", build.BuildHash);
    }

    [Fact]
    public async Task Custom_branch_uses_archive_name_when_given()
    {
        using var temp = new TempDir();
        temp.File("custom/folder/blender.exe");

        var build = await Reader(new FakeProbe(Samples.BlenderDaily)).ReadAsync(temp.Combine("custom", "folder"), archiveName: "my-archive", ct: Ct);

        Assert.Equal("my-archive", build.Branch);
    }

    [Fact]
    public async Task First_line_without_space_and_no_product_name_fails()
    {
        using var temp = new TempDir();
        temp.File("custom/odd/blender.exe");

        await Assert.ThrowsAsync<FormatException>(() => Reader(new FakeProbe("garbage\n")).ReadAsync(temp.Combine("custom", "odd"), ct: Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Probe_failure_falls_back_to_supplied_info(bool timedOut)
    {
        using var temp = new TempDir();
        temp.File("daily/b/blender.exe");
        var info = Samples.Info(hash: null) with { CustomName = "scraped name" };

        var build = await Reader(FakeProbe.Failing(timedOut)).ReadAsync(temp.Combine("daily", "b"), info: info, ct: Ct);

        Assert.Equal(info.Subversion, build.Subversion);
        Assert.Equal(info.CommitTime, build.CommitTime);
        Assert.Equal("", build.BuildHash); // upstream: info.build_hash or ""
        Assert.Equal("scraped name", build.CustomName);
    }

    [Fact]
    public async Task Probe_failure_without_info_throws_and_writes_nothing()
    {
        using var temp = new TempDir();
        temp.File("daily/b/blender.exe");

        await Assert.ThrowsAsync<BlenderProbeException>(() => Reader(FakeProbe.Failing(true)).ReadAsync(temp.Combine("daily", "b"), ct: Ct));
        Assert.False(File.Exists(temp.Combine("daily", "b", ".blinfo")));
    }

    [Fact]
    public async Task Missing_executable_throws_file_not_found_even_with_info()
    {
        using var temp = new TempDir();
        var folder = temp.Folder("daily", "empty");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            Reader(new FakeProbe(Samples.BlenderDaily)).ReadAsync(folder, info: Samples.Info(), ct: Ct));
    }

    [Fact]
    public async Task Auto_write_off_does_not_write()
    {
        using var temp = new TempDir();
        temp.File("daily/b/blender.exe");

        await Reader(new FakeProbe(Samples.BlenderDaily)).ReadAsync(temp.Combine("daily", "b"), autoWrite: false, ct: Ct);

        Assert.False(File.Exists(temp.Combine("daily", "b", ".blinfo")));
    }

    [Fact]
    public async Task Current_blinfo_is_returned_without_running_the_executable()
    {
        using var temp = new TempDir();
        var folder = temp.Folder("stable", "blender-4.2.3");
        await Samples.WriteBlinfoAsync(folder, Samples.Info("4.2.3", "stable", null));
        var probe = new FakeProbe(Samples.Blender42Lts);

        var build = await Reader(probe, (4, 2)).ReadAsync(folder, ct: Ct);

        Assert.Empty(probe.Calls);
        Assert.Equal("lts", build.Branch); // LTS promotion on read
    }

    [Fact]
    public async Task Old_file_version_is_migrated_keeping_user_fields()
    {
        using var temp = new TempDir();
        temp.File("daily/blender-4.3/blender.exe");
        var folder = temp.Combine("daily", "blender-4.3");
        File.WriteAllText(Path.Combine(folder, ".blinfo"),
            """{"file_version": "1.4", "blinfo": [{"branch": "daily", "subversion": "4.3.0-alpha", "build_hash": null, "commit_time": "30-Jul-24-12:34", "custom_name": "Mine", "is_favorite": true, "is_frozen": true}]}""");
        var probe = new FakeProbe(Samples.BlenderDaily);

        var build = await Reader(probe).ReadAsync(folder, ct: Ct);

        Assert.Single(probe.Calls);
        Assert.Equal("4.3.0-alpha", build.Subversion);
        Assert.Equal("ddc9f92777cd", build.BuildHash);
        Assert.Equal("Mine", build.CustomName);
        Assert.True(build.IsFavorite);
        Assert.True(build.IsFrozen);
        Assert.Equal("2024-07-30T12:34:00+00:00", build.CommitTime.ToString()); // old time, read as local (UTC here)

        var rewritten = await BlinfoCodec.ReadFileAsync(folder, Ct);
        Assert.Equal("1.5", rewritten.FileVersion);
        Assert.Equal("ddc9f92777cd", rewritten.Blinfo[0].BuildHash);
        Assert.True(rewritten.Blinfo[0].IsFavorite);
    }

    [Fact]
    public async Task Missing_file_version_and_invalid_info_are_regenerated()
    {
        using var temp = new TempDir();
        temp.File("daily/a/blender.exe");
        temp.File("daily/b/blender.exe");
        File.WriteAllText(temp.Combine("daily", "a", ".blinfo"),
            """{"blinfo": [{"branch": "daily", "subversion": "4.3.0-alpha", "commit_time": "2024-07-30T12:34:00+00:00"}]}""");
        File.WriteAllText(temp.Combine("daily", "b", ".blinfo"),
            """{"file_version": "1.5", "blinfo": [{"branch": "daily", "subversion": "", "commit_time": "2024-07-30T12:34:00+00:00"}]}""");
        var probe = new FakeProbe(Samples.BlenderDaily);
        var reader = Reader(probe);

        var a = await reader.ReadAsync(temp.Combine("daily", "a"), ct: Ct);
        var b = await reader.ReadAsync(temp.Combine("daily", "b"), ct: Ct);

        Assert.Equal(2, probe.Calls.Count);
        Assert.Equal("ddc9f92777cd", a.BuildHash);
        Assert.Equal("4.3.0 Alpha", b.Subversion); // the old subversion was empty, so -v supplies it
        Assert.True(b.IsValid);
    }

    [Fact]
    public async Task Missing_custom_executable_reuses_old_info_without_probing()
    {
        using var temp = new TempDir();
        temp.File("custom/fork/bin/fork.exe");
        temp.File("custom/fork/blender.exe");
        var folder = temp.Combine("custom", "fork");
        File.WriteAllText(Path.Combine(folder, ".blinfo"),
            """{"file_version": "1.0", "blinfo": [{"branch": "fork", "subversion": "3.6.0", "build_hash": "h", "commit_time": "2024-01-01T00:00:00+00:00", "custom_name": "Fork", "custom_executable": "gone/fork.exe"}]}""");
        var probe = new FakeProbe(Samples.BlenderDaily);

        var build = await Reader(probe).ReadAsync(folder, ct: Ct);

        Assert.Empty(probe.Calls);
        Assert.Equal("3.6.0", build.Subversion);
        Assert.Equal("h", build.BuildHash);
        Assert.Equal("blender.exe", build.CustomExecutable);
        Assert.Equal("fork", build.Branch);
    }

    [Fact]
    public async Task Present_custom_executable_is_probed()
    {
        using var temp = new TempDir();
        temp.File("custom/fork/bin/fork.exe");
        var folder = temp.Combine("custom", "fork");
        var old = Samples.Info("", "fork") with { CustomExecutable = "bin/fork.exe" };
        var probe = new FakeProbe(Samples.CustomFork);

        var build = await Reader(probe).DeriveAsync(folder, old, ct: Ct);

        Assert.Equal(Path.Combine(folder, "bin", "fork.exe"), Path.GetFullPath(Assert.Single(probe.Calls)));
        Assert.Equal("bin/fork.exe", build.CustomExecutable);
        Assert.Equal("3.6.2", build.Subversion);
        Assert.Equal("", build.CustomName); // old custom name (empty) wins over the parsed one, as upstream
    }

    [Theory]
    [InlineData("experimental", "blender-4.3.0-alpha+PR12345.abcdef123456-windows.amd64-release", null, "PR12345")]
    [InlineData("experimental", "blender-4.4.0-renamed", "old-branch", "old-branch")]
    [InlineData("experimental", "blender-4.4.0-renamed", null, "experimental")]
    [InlineData("daily", "blender-4.3.0-alpha+daily.abc", null, "daily")]
    [InlineData("custom", "My Build", null, "My Build")]
    [InlineData("upbge-stable", "upbge-0.44", "x", "upbge-stable")]
    public void Branch_derivation(string parent, string name, string? old, string expected) =>
        Assert.Equal(expected, BuildInfoReader.BranchFor(parent, name, old));

    [Fact]
    public async Task Update_rereads_blinfo_before_writing()
    {
        using var temp = new TempDir();
        var folder = temp.Folder("daily", "b");
        var stale = Samples.Info() with { Link = folder };
        await Samples.WriteBlinfoAsync(folder, stale with { IsFavorite = true, CustomName = "Set elsewhere" });

        var updated = await Reader(new FakeProbe("")).UpdateAsync(stale, b => b with { IsFrozen = true }, Ct);

        Assert.True(updated.IsFrozen);
        Assert.True(updated.IsFavorite);
        Assert.Equal("Set elsewhere", updated.CustomName);
        var onDisk = (await BlinfoCodec.ReadFileAsync(folder, Ct)).Blinfo[0];
        Assert.True(onDisk.IsFrozen && onDisk.IsFavorite);
    }

    [Fact]
    public async Task Update_uses_memory_when_blinfo_is_missing()
    {
        using var temp = new TempDir();
        var folder = temp.Folder("daily", "b");

        var updated = await Reader(new FakeProbe("")).UpdateAsync(Samples.Info() with { Link = folder }, b => b with { IsFavorite = true }, Ct);

        Assert.True(updated.IsFavorite);
        Assert.True(File.Exists(Path.Combine(folder, ".blinfo")));
    }

    [Fact]
    public void Parses_commit_time_and_hash_with_crlf()
    {
        var parsed = new BuildInfoReader(new FakeProbe(""), time: Clock).ParseVersionOutput(Samples.Blender42Lts);

        Assert.Equal("2a1d9b8b4e2b", parsed.BuildHash);
        Assert.Equal("4.2.3 LTS", parsed.Subversion);
        Assert.Equal("2024-10-14T10:53:00+00:00", parsed.CommitTime.ToString());
        Assert.Equal("Blender 4.2.3 LTS", parsed.FirstLine);
    }

    [Fact]
    public void Detects_executables_in_upstream_order()
    {
        using var temp = new TempDir();
        var folder = temp.Folder("b");
        Assert.Equal(Path.Combine(folder, "blender.exe"), BuildInfoReader.DetectExecutable(folder));
        Assert.Null(BuildInfoReader.FindExecutable(folder, null));

        temp.File("b/blender.exe");
        Assert.Equal(Path.Combine(folder, "blender.exe"), BuildInfoReader.FindExecutable(folder, "missing.exe"));

        temp.File("b/bforartists.exe");
        Assert.Equal(Path.Combine(folder, "bforartists.exe"), BuildInfoReader.DetectExecutable(folder));

        temp.File("b/custom.exe");
        Assert.Equal(Path.Combine(folder, "custom.exe"), BuildInfoReader.FindExecutable(folder, "custom.exe"));
    }
}
