using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Core.Tests.Library;

namespace BlenderLauncher.Core.Tests.Builds;

/// <summary>
/// Upstream's <c>blender_update_manager.py</c> rules (upstream has no tests of its own beyond the two
/// Bforartists halves ported at the end), as a table of behaviours.
/// </summary>
public sealed class BuildUpdateServiceTests : IDisposable
{
    private static readonly IsoTimestamp T1 = IsoTimestamp.Parse("2026-01-01T00:00:00+00:00");
    private static readonly IsoTimestamp T2 = IsoTimestamp.Parse("2026-02-01T00:00:00+00:00");
    private static readonly IsoTimestamp T3 = IsoTimestamp.Parse("2026-03-01T00:00:00+00:00");
    private readonly TempDir _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    private static BuildInfo B(string subversion, string branch = "stable", string? hash = null, IsoTimestamp? time = null, string link = "x") => new()
    {
        Link = link,
        Subversion = subversion,
        BuildHash = hash,
        CommitTime = time ?? T1,
        Branch = branch,
    };

    private static AvailableBuild A(string subversion, string branch = "stable", string? hash = null, IsoTimestamp? time = null) =>
        new(B(subversion, branch, hash, time, link: $"https://example.com/{branch}-{subversion}-{hash}.zip"));

    private static string? Find(BuildInfo current, UpdateBehaviour behaviour, IEnumerable<AvailableBuild> available, params BuildInfo[] alsoInstalled) =>
        BuildUpdateRules.FindCandidate(current, available, [current, .. alsoInstalled], behaviour)?.Build.Subversion;

    // ---- Behaviour: Major, Minor, Patch against the current build ----

    [Theory]
    [InlineData(UpdateBehaviour.Patch, "4.2.3")]
    [InlineData(UpdateBehaviour.Minor, "4.3.0")]
    [InlineData(UpdateBehaviour.Major, "5.0.0")]
    public void Behaviour_picks_the_highest_version_in_scope(UpdateBehaviour behaviour, string expected)
    {
        var current = B("4.2.1");
        var available = new[] { A("4.2.3"), A("4.2.2"), A("4.3.0"), A("5.0.0"), A("4.1.9") };
        Assert.Equal(expected, Find(current, behaviour, available));
    }

    [Theory]
    [InlineData(UpdateBehaviour.Patch)]
    [InlineData(UpdateBehaviour.Minor)]
    [InlineData(UpdateBehaviour.Major)]
    public void Nothing_newer_gives_no_update(UpdateBehaviour behaviour) =>
        Assert.Null(Find(B("4.2.3"), behaviour, [A("4.2.1"), A("4.1.0"), A("3.6.5")]));

    [Fact]
    public void Patch_does_not_cross_a_minor_and_minor_does_not_cross_a_major()
    {
        Assert.Null(Find(B("4.2.1"), UpdateBehaviour.Patch, [A("4.3.0")]));
        Assert.Null(Find(B("4.2.1"), UpdateBehaviour.Minor, [A("5.0.0")]));
    }

    [Fact]
    public void Better_version_is_measured_against_the_highest_installed_version_in_scope()
    {
        // 4.2.5 is installed beside 4.2.1, so 4.2.3 is no better for the 4.2.1 build.
        Assert.Null(Find(B("4.2.1"), UpdateBehaviour.Patch, [A("4.2.3")], B("4.2.5")));
        Assert.Equal("4.2.6", Find(B("4.2.1"), UpdateBehaviour.Patch, [A("4.2.3"), A("4.2.6")], B("4.2.5")));

        // Minor: the highest installed of the same major (upstream compares minor, then patch, against it).
        Assert.Null(Find(B("4.2.1"), UpdateBehaviour.Minor, [A("4.3.0")], B("4.4.0")));

        // Major: the highest installed of any version, on any branch (upstream's library list is every build).
        Assert.Null(Find(B("4.2.1"), UpdateBehaviour.Major, [A("4.5.0")], B("5.0.0-alpha", "daily")));
    }

    [Fact]
    public void Among_equal_better_versions_the_latest_commit_wins()
    {
        var available = new[] { A("4.2.3", time: T2), A("4.2.3", time: T3), A("4.2.3", time: T1) };
        var pick = BuildUpdateRules.FindCandidate(B("4.2.1"), available, [], UpdateBehaviour.Patch);
        Assert.Equal(T3, pick!.Build.CommitTime);
    }

    // ---- Same version, newer build ----

    [Fact]
    public void Same_version_newer_build_is_an_update()
    {
        var current = B("4.3.0-alpha", "daily", "aaaa", T1);
        var pick = BuildUpdateRules.FindCandidate(current, [A("4.3.0-alpha", "daily", "bbbb", T2), A("4.3.0-alpha", "daily", "cccc", T3)], [current], UpdateBehaviour.Patch);
        Assert.Equal("cccc", pick!.Build.BuildHash);
    }

    [Fact]
    public void Same_version_older_or_equal_build_is_not_an_update()
    {
        var current = B("4.3.0-alpha", "daily", "aaaa", T2);
        Assert.Null(BuildUpdateRules.FindCandidate(current, [A("4.3.0-alpha", "daily", "bbbb", T1), A("4.3.0-alpha", "daily", "cccc", T2)], [current], UpdateBehaviour.Patch));
    }

    [Fact]
    public void Installed_hashes_are_skipped()
    {
        var current = B("4.3.0-alpha", "daily", "aaaa", T1);
        var other = B("4.3.0-alpha", "daily", "bbbb", T3);
        Assert.Null(BuildUpdateRules.FindCandidate(current, [A("4.3.0-alpha", "daily", "bbbb", T3)], [current, other], UpdateBehaviour.Patch));
    }

    [Fact]
    public void Daily_newer_build_with_a_lower_prerelease_is_not_an_update()
    {
        var current = B("4.3.0-beta", "daily", "aaaa", T1);
        Assert.Null(BuildUpdateRules.FindCandidate(current, [A("4.3.0-alpha", "daily", "bbbb", T2)], [current], UpdateBehaviour.Patch));
        Assert.Equal("bbbb", BuildUpdateRules.FindCandidate(current, [A("4.3.0-rc", "daily", "bbbb", T2)], [current], UpdateBehaviour.Patch)!.Build.BuildHash);
    }

    [Fact]
    public void Lower_prerelease_rule_applies_to_daily_only()
    {
        var current = B("4.3.0-rc", "upbge-weekly", "aaaa", T1);
        Assert.Equal("bbbb", BuildUpdateRules.FindCandidate(current, [A("4.3.0-beta", "upbge-weekly", "bbbb", T2)], [current], UpdateBehaviour.Patch)!.Build.BuildHash);
    }

    [Fact]
    public void Better_version_is_preferred_over_a_newer_build_of_the_same_version()
    {
        var current = B("4.2.1", "stable", null, T1);
        var available = new[] { A("4.2.1", time: T3), A("4.2.2", time: T2) };
        Assert.Equal("4.2.2", Find(current, UpdateBehaviour.Patch, available));
        Assert.Equal(T3, BuildUpdateRules.FindCandidate(current, [A("4.2.1", time: T3)], [current], UpdateBehaviour.Patch)!.Build.CommitTime);
    }

    [Fact]
    public void Only_downloads_of_the_same_branch_count()
    {
        Assert.Null(Find(B("4.2.1", "stable"), UpdateBehaviour.Major, [A("4.5.0", "lts"), A("5.0.0-alpha", "daily")]));
        Assert.Equal("4.2.9", Find(B("4.2.1", "lts"), UpdateBehaviour.Patch, [A("4.2.9", "lts")]));
    }

    [Fact]
    public void Installed_experimental_builds_find_no_update_as_upstream()
    {
        // Installed patch builds take the branch from the archive (PR12345); scraped ones are "patch".
        var current = B("4.4.0-alpha", "PR12345", "aaaa", T1);
        Assert.Null(BuildUpdateRules.FindCandidate(current, [A("4.4.0-alpha", "patch", "bbbb", T2)], [current], UpdateBehaviour.Major));
    }

    [Fact]
    public void Unparsable_versions_are_skipped()
    {
        Assert.Null(Find(B("not a version"), UpdateBehaviour.Major, [A("5.0.0")]));
        Assert.Null(Find(B("4.2.1"), UpdateBehaviour.Major, [A("garbage")]));
    }

    // ---- Eligibility and behaviour per branch ----

    [Theory]
    [InlineData("stable", true)]
    [InlineData("daily", true)]
    [InlineData("PR12345", true)]
    [InlineData("bforartists", true)]
    [InlineData("upbge-stable", true)]
    [InlineData("upbge-weekly", true)]
    [InlineData("main", false)]
    public void Simple_update_button_covers_every_known_branch(string branch, bool eligible)
    {
        Assert.Equal(eligible, BuildUpdateRules.IsEligible(branch, new BuildsSettings()));
        Assert.False(BuildUpdateRules.IsEligible(branch, new BuildsSettings { ShowUpdateButton = false }));
    }

    [Fact]
    public void Experimental_branches_are_matched_without_regard_to_case()
    {
        Assert.True(BuildUpdateRules.IsExperimentalBranch("PR123"));
        Assert.True(BuildUpdateRules.IsExperimentalBranch("NPR-x"));
        Assert.False(BuildUpdateRules.IsExperimentalBranch("main"));
    }

    [Fact]
    public void Advanced_update_button_uses_the_channel_switches_and_behaviours()
    {
        var s = new BuildsSettings
        {
            UseAdvancedUpdateButton = true,
            ShowUpdateButton = false,
            ShowStableUpdateButton = true,
            ShowDailyUpdateButton = false,
            ShowExperimentalUpdateButton = true,
            ShowBforartistsUpdateButton = false,
            ShowUpbgeStableUpdateButton = true,
            ShowUpbgeWeeklyUpdateButton = false,
            UpdateBehaviour = UpdateBehaviour.Patch,
            StableUpdateBehaviour = UpdateBehaviour.Major,
            DailyUpdateBehaviour = UpdateBehaviour.Minor,
            ExperimentalUpdateBehaviour = UpdateBehaviour.Major,
            BforartistsUpdateBehaviour = UpdateBehaviour.Minor,
            UpbgeStableUpdateBehaviour = UpdateBehaviour.Major,
            UpbgeWeeklyUpdateBehaviour = UpdateBehaviour.Minor,
        };

        Assert.True(BuildUpdateRules.IsEligible("lts", s));
        Assert.False(BuildUpdateRules.IsEligible("daily", s));
        Assert.True(BuildUpdateRules.IsEligible("pr1", s));
        Assert.False(BuildUpdateRules.IsEligible("bforartists", s));
        Assert.True(BuildUpdateRules.IsEligible("upbge-stable", s));
        Assert.False(BuildUpdateRules.IsEligible("upbge-weekly", s));

        Assert.Equal(UpdateBehaviour.Major, BuildUpdateRules.BehaviourFor("stable", s));
        Assert.Equal(UpdateBehaviour.Minor, BuildUpdateRules.BehaviourFor("daily", s));
        Assert.Equal(UpdateBehaviour.Major, BuildUpdateRules.BehaviourFor("NPR-x", s));
        Assert.Equal(UpdateBehaviour.Minor, BuildUpdateRules.BehaviourFor("bforartists", s));
        Assert.Equal(UpdateBehaviour.Major, BuildUpdateRules.BehaviourFor("upbge-stable", s));
        Assert.Equal(UpdateBehaviour.Minor, BuildUpdateRules.BehaviourFor("upbge-weekly", s));
        Assert.Equal(UpdateBehaviour.Patch, BuildUpdateRules.BehaviourFor("something-else", s));
        Assert.Equal(UpdateBehaviour.Patch, BuildUpdateRules.BehaviourFor("stable", s with { UseAdvancedUpdateButton = false }));
    }

    [Fact]
    public void Major_version_update_means_major_or_minor_differ()
    {
        Assert.True(BuildUpdateRules.IsMajorVersionUpdate(B("4.2.1"), B("4.3.0")));
        Assert.True(BuildUpdateRules.IsMajorVersionUpdate(B("4.2.1"), B("5.2.1")));
        Assert.False(BuildUpdateRules.IsMajorVersionUpdate(B("4.2.1"), B("4.2.9-rc")));
        Assert.False(BuildUpdateRules.IsMajorVersionUpdate(B("5.3.0-dev", "bforartists"), B("0.0.20261027-dev", "bforartists")));
    }

    // ---- The service: frozen, custom, eligibility, refresh ----

    private async Task<(LibraryService Library, LibraryState State)> LibraryAsync(params (string Folder, BuildInfo Build)[] builds)
    {
        var root = _temp.Folder("Lib");
        foreach (var (folder, build) in builds)
        {
            var path = _temp.Folder(["Lib", .. folder.Split('/')]);
            await Samples.WriteBlinfoAsync(path, build);
        }

        var state = await LibraryState.LoadAsync(_temp.Combine("state"), ct: Ct);
        var library = new LibraryService(new BuildInfoReader(FakeProbe.Failing()), state);
        await library.ScanAsync(root, ct: Ct);
        return (library, state);
    }

    [Fact]
    public async Task Service_skips_frozen_and_custom_builds_and_follows_the_button_switch()
    {
        var (library, state) = await LibraryAsync(
            ("stable/blender-4.2.1", B("4.2.1")),
            ("stable/blender-4.1.0", B("4.1.0") with { IsFrozen = true }),
            ("custom/my-4.0", B("4.0.0")));
        await using var _ = state;
        using var __ = library;
        var settings = new BuildsSettings { UpdateBehaviour = UpdateBehaviour.Major };
        var service = new BuildUpdateService(() => settings, library);
        var raised = 0;
        service.UpdatesChanged += (_, _) => raised++;

        var updates = service.Refresh([A("4.2.4"), A("my-4.0", "my-4.0")]);

        var update = Assert.Single(updates.Values);
        Assert.EndsWith("blender-4.2.1", update.BuildFolder, StringComparison.Ordinal);
        Assert.Equal("4.2.4", update.Candidate.Build.Subversion);
        Assert.False(update.IsMajorVersionUpdate);
        Assert.False(update.HasPortableSettings);
        Assert.True(update.ShowButton);
        Assert.Same(update, service.Get(update.BuildFolder));
        Assert.Equal(1, raised);

        service.Refresh([A("4.2.4")]);
        Assert.Equal(1, raised); // unchanged

        settings = settings with { ShowUpdateButton = false };
        Assert.Empty(service.Refresh());
        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task Replacement_ignores_the_button_switch_but_not_the_freeze()
    {
        var (library, state) = await LibraryAsync(("daily/a", B("4.3.0-alpha", "daily", "aaaa", T1)));
        await using var _ = state;
        using var __ = library;
        var service = new BuildUpdateService(() => new BuildsSettings { ShowUpdateButton = false }, library);
        var current = library.Current.Builds.Single();

        Assert.NotNull(service.FindReplacement(current, [A("4.3.0-alpha", "daily", "bbbb", T2)]));
        Assert.Null(service.FindReplacement(current with { IsFrozen = true }, [A("4.3.0-alpha", "daily", "bbbb", T2)]));
        Assert.Empty(service.Refresh([A("4.3.0-alpha", "daily", "bbbb", T2)]));
    }

    [Fact]
    public async Task Portable_settings_are_reported_and_moved()
    {
        var (library, state) = await LibraryAsync(("stable/blender-4.2.1", B("4.2.1")), ("stable/blender-4.2.4", B("4.2.4")));
        await using var _ = state;
        using var __ = library;
        var old = library.Current.Builds.Single(b => b.Subversion == "4.2.1");
        var created = library.Current.Builds.Single(b => b.Subversion == "4.2.4");
        _temp.File("Lib/stable/blender-4.2.1/portable/config/userpref.blend", "prefs");

        Assert.True(BuildUpdateService.HasPortableSettings(old));
        Assert.Equal(PortableCopyResult.Copied, await BuildUpdateService.MovePortableSettingsAsync(old, created, Ct));
        Assert.Equal("prefs", File.ReadAllText(_temp.Combine("Lib", "stable", "blender-4.2.4", "portable", "config", "userpref.blend")));
    }

    // ---- Upstream: test_bfa_scraper.py, the update-check halves ----

    [Fact]
    public void Upstream_developer_identity_and_update_isolation()
    {
        var date = IsoTimestamp.Aware(new DateTime(2026, 10, 19), TimeSpan.Zero);
        var devTime = IsoTimestamp.Aware(new DateTime(2026, 10, 27), TimeSpan.Zero);
        var stable = B("5.2.0", "bforartists", null, date, "stable");
        var dev = B("0.0.20261027-dev", "bforartists", null, devTime, "dev");
        var installed = B("5.3.0-dev", "bforartists", "abc", devTime, "installed");

        Assert.True(installed.Matches(dev));
        Assert.False(installed.Matches(stable));
        Assert.Null(BuildUpdateRules.FindCandidate(stable, [new AvailableBuild(dev)], [], UpdateBehaviour.Major));
        Assert.Null(BuildUpdateRules.FindCandidate(installed, [new AvailableBuild(stable)], [], UpdateBehaviour.Major));

        var older = B("5.3.0-dev", "bforartists", "def", date, "older");
        var download = new AvailableBuild(dev);
        Assert.Same(download, BuildUpdateRules.FindCandidate(older, [download], [], UpdateBehaviour.Major));

        // And the dev build already installed is not offered again.
        Assert.Null(BuildUpdateRules.FindCandidate(older, [download], [older, installed], UpdateBehaviour.Major));
    }

    [Fact]
    public async Task Upstream_installed_developer_metadata_survives_read()
    {
        var directory = _temp.Folder("bforartists", "Bforartists-5.3.0");
        File.WriteAllText(Path.Combine(directory, "bforartists.exe"), "");
        var date = IsoTimestamp.Aware(new DateTime(2026, 10, 19), TimeSpan.Zero);
        var info = B("5.3.0-dev", "bforartists", null, date, "download") with { CustomExecutable = "bforartists.exe" };
        var reader = new BuildInfoReader(new FakeProbe(Samples.Bforartists));

        var installed = await reader.DeriveAsync(directory, info, ct: Ct);

        Assert.True(installed.IsBfaDev);
        Assert.Equal(date, installed.CommitTime);
        Assert.Equal("5.3.0-dev", installed.Subversion);
        Assert.Equal("bforartists.exe", installed.CustomExecutable);
        Assert.True(installed.Matches(info));
    }
}
