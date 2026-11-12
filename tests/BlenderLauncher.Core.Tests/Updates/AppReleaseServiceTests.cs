using System.Net;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Tests.Net;
using BlenderLauncher.Core.Updates;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Tests.Updates;

/// <summary>Against releases recorded from upstream's repository (see Fixtures/Net/github-releases/README.md).</summary>
public class AppReleaseServiceTests
{
    private const string Repo = "Victor-IX/Blender-Launcher-V2";
    private const string LatestUrl = "https://github.com/" + Repo + "/releases/latest";
    private const string ListUrl = "https://api.github.com/repos/" + Repo + "/releases?per_page=100";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AppReleaseService Service(RecordedResponseHandler handler, ConnectionOptions? options = null)
    {
        var client = handler.CreateClient(options);
        return new AppReleaseService(Repo, () => client);
    }

    private static RecordedResponseHandler Recorded() => RecordedResponseHandler.FromFixtures("github-releases");

    // ---- Disabled ----

    [Fact]
    public async Task With_no_repository_the_service_is_disabled_and_makes_no_requests()
    {
        var handler = new RecordedResponseHandler();
        using var provider = new HttpClientProvider(ConnectionOptions.Default, null, _ => handler);
        var service = new AppReleaseService(null, () => provider.Client);

        var result = await service.CheckAsync("0.1.0", includePreReleases: true, Ct);

        Assert.False(service.IsEnabled);
        Assert.Equal(UpdateCheckStatus.Disabled, result.Status);
        Assert.Empty(handler.Requests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetLatestStableAsync(Ct));
    }

    [Fact]
    public void The_app_checks_its_own_repository()
    {
        using var provider = new HttpClientProvider(ConnectionOptions.Default, null, _ => new RecordedResponseHandler());

        var service = new AppReleaseService(provider);

        Assert.Equal("matty/blender-launcher", service.Repo);
        Assert.Equal("https://github.com/matty/blender-launcher/releases/latest", service.LatestReleaseUrl.AbsoluteUri);
    }

    // The repository grammar itself is covered by ReleaseAssetsTests.Repository_names.
    [Fact]
    public void A_malformed_repository_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new AppReleaseService("https://github.com/a/b", () => throw new InvalidOperationException()));

    // ---- Stable channel ----

    [Fact]
    public async Task Stable_tag_is_the_last_segment_of_the_redirect()
    {
        var handler = Recorded();

        var latest = await Service(handler).GetLatestStableAsync(Ct);

        Assert.Equal(new AppRelease("v2.8.2", new SemVer(2, 8, 2)), latest);
        Assert.Equal([LatestUrl, "https://github.com/" + Repo + "/releases/tag/v2.8.2"], handler.Requests.Select(r => r.Uri.AbsoluteUri));
    }

    [Fact]
    public async Task Stable_check_offers_the_latest_with_stable_notes_newest_first()
    {
        var handler = Recorded();

        var result = await Service(handler).CheckAsync("2.7.1", includePreReleases: false, Ct);

        handler.AssertAllMatched();
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("v2.8.2", result.Latest?.Tag);
        Assert.Null(result.Error);
        Assert.Equal(["v2.8.2", "v2.8.1", "v2.8.0", "v2.7.6"], result.Notes.Select(n => n.Tag));
        Assert.All(result.Notes, n => Assert.False(n.IsPrerelease));
    }

    [Fact]
    public async Task Up_to_date_makes_no_release_list_request()
    {
        var handler = Recorded();

        var result = await Service(handler).CheckAsync("2.8.2", includePreReleases: false, Ct);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Empty(result.Notes);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.Host == "api.github.com");
    }

    [Fact]
    public async Task A_newer_running_version_is_up_to_date()
    {
        var result = await Service(Recorded()).CheckAsync("3.0.0", includePreReleases: false, Ct);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task A_release_candidate_on_the_stable_channel_is_offered_the_stable_release()
    {
        // Upstream treats the running rc as 0.0.0; the notes still start after the rc itself.
        var result = await Service(Recorded()).CheckAsync("2.7.0-rc.1", includePreReleases: false, Ct);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(["v2.8.2", "v2.8.1", "v2.8.0", "v2.7.6", "v2.7.1", "v2.7.0"], result.Notes.Select(n => n.Tag));
    }

    [Fact]
    public async Task The_token_goes_to_the_api_host_only()
    {
        var handler = Recorded();

        await Service(handler, ConnectionOptions.Default with { GitHubToken = "ghp_secret" }).CheckAsync("2.8.0", false, Ct);

        Assert.All(handler.Requests.Where(r => r.Uri.Host == "github.com"), r => Assert.Null(r.Authorization));
        Assert.Contains(handler.Requests, r => r.Uri.Host == "api.github.com" && r.Authorization is not null);
    }

    [Fact]
    public async Task No_release_redirects_to_the_release_list_and_is_reported_as_none()
    {
        var handler = new RecordedResponseHandler()
            .AddRedirect(LatestUrl, "https://github.com/" + Repo + "/releases")
            .AddText("https://github.com/" + Repo + "/releases", "<html></html>");

        var result = await Service(handler).CheckAsync("0.1.0", includePreReleases: false, Ct);

        Assert.Equal(UpdateCheckStatus.NoRelease, result.Status);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("https://github.com/Victor-IX/Blender-Launcher-V2/releases/tag/v2.8.2", "v2.8.2")]
    [InlineData("https://github.com/victor-ix/blender-launcher-v2/releases/tag/v1.0.0-rc.1", "v1.0.0-rc.1")]
    [InlineData("https://github.com/Victor-IX/Blender-Launcher-V2/releases/tag/v2.8.2/", "v2.8.2")]
    [InlineData("https://github.com/Victor-IX/Blender-Launcher-V2/releases", null)] // upstream took "releases" as the tag
    [InlineData("https://github.com/Other/Repo/releases/tag/v1.0.0", null)]
    [InlineData("https://example.org/Victor-IX/Blender-Launcher-V2/releases/tag/v1.0.0", null)]
    public void Tag_from_release_url(string url, string? expected) =>
        Assert.Equal(expected, AppReleaseService.TagFromReleaseUrl(new Uri(url), Repo));

    [Fact]
    public async Task A_tag_that_is_not_semver_is_no_release()
    {
        var handler = new RecordedResponseHandler()
            .AddRedirect(LatestUrl, "https://github.com/" + Repo + "/releases/tag/nightly")
            .AddText("https://github.com/" + Repo + "/releases/tag/nightly", "");

        Assert.Equal(UpdateCheckStatus.NoRelease, (await Service(handler).CheckAsync("0.1.0", false, Ct)).Status);
    }

    // ---- Pre-release channel ----

    [Fact]
    public async Task Pre_release_channel_uses_one_list_request_for_tag_and_notes()
    {
        var handler = Recorded();

        var result = await Service(handler).CheckAsync("2.6.1", includePreReleases: true, Ct);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("v2.8.2", result.Latest?.Tag);
        Assert.Equal(["v2.8.2", "v2.8.1", "v2.8.0", "v2.7.6", "v2.7.1", "v2.7.0", "v2.7.0-rc.1"], result.Notes.Select(n => n.Tag));
        Assert.True(result.Notes[^1].IsPrerelease);
        Assert.Equal([ListUrl], handler.Requests.Select(r => r.Uri.AbsoluteUri));
    }

    [Fact]
    public async Task A_release_candidate_on_the_pre_release_channel_compares_as_itself()
    {
        var result = await Service(Recorded()).CheckAsync("2.8.2", includePreReleases: true, Ct);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    private const string InlineList = """
        [
          { "tag_name": "v4.0.0", "draft": true, "prerelease": false, "body": "draft",
            "assets": [ { "name": "Blender_Launcher_v4.0.0_Windows_x64.zip" } ] },
          { "tag_name": "v3.1.0-rc.1", "draft": false, "prerelease": true, "body": "linux only",
            "assets": [ { "name": "Blender_Launcher_v3.1.0-rc.1_Linux_x64.zip" } ] },
          { "tag_name": "v3.0.0-rc.2", "draft": false, "prerelease": true, "body": "windows but not a zip",
            "assets": [ { "name": "Blender_Launcher_v3.0.0-rc.2_Windows_x64.7z" } ] },
          { "tag_name": "3.0.0-rc.1", "draft": false, "prerelease": true, "body": null,
            "assets": [ { "name": "BLENDER_LAUNCHER_3.0.0-RC.1_WINDOWS_X64.ZIP" } ] },
          { "tag_name": "v2.9.0", "draft": false, "prerelease": false, "body": "**Stable**",
            "assets": [ { "name": "Blender_Launcher_v2.9.0_Windows_x64.zip" } ] },
          { "tag_name": "not-a-version", "draft": false, "prerelease": false, "body": "",
            "assets": [ { "name": "x_windows.zip" } ] },
          { "tag_name": null, "assets": null }
        ]
        """;

    [Fact]
    public async Task Pre_release_channel_picks_the_highest_semver_with_a_windows_zip()
    {
        var handler = new RecordedResponseHandler().AddText(ListUrl, InlineList, "application/json");

        var result = await Service(handler).CheckAsync("2.8.0", includePreReleases: true, Ct);

        // Drafts, Linux-only and non-zip releases are skipped; the tag is kept as written (upstream rebuilt "v" + tag).
        // The notes stop at the latest, so rc.2 (above rc.1, but with no Windows zip) has none.
        Assert.Equal(new AppRelease("3.0.0-rc.1", SemVer.Parse("3.0.0-rc.1")), result.Latest);
        Assert.Equal(["3.0.0-rc.1", "v2.9.0"], result.Notes.Select(n => n.Tag));
        Assert.Equal("", result.Notes[0].Body);
        Assert.Equal("", result.Notes[0].PlainText);
        Assert.Equal("Stable", result.Notes[1].PlainText);
    }

    [Fact]
    public async Task Pre_release_channel_with_no_windows_zip_is_no_release()
    {
        var handler = new RecordedResponseHandler().AddText(ListUrl, "[]", "application/json");

        Assert.Equal(UpdateCheckStatus.NoRelease, (await Service(handler).CheckAsync("1.0.0", true, Ct)).Status);
    }

    // ---- Current-version rules ----

    [Theory]
    [InlineData("1.2.0+dev", true)]
    [InlineData("1.2.0-dev.1", true)]
    [InlineData("v1.2.0+build.dev", true)]
    [InlineData("1.2.0", false)]
    [InlineData("1.2.0+abc123", false)]
    [InlineData("not a version", false)]
    public void Development_builds(string version, bool expected) => Assert.Equal(expected, AppReleaseService.IsDevBuild(version));

    [Fact]
    public async Task A_development_build_makes_no_requests()
    {
        var handler = new RecordedResponseHandler();

        var result = await Service(handler).CheckAsync("2.0.0+dev", includePreReleases: false, Ct);

        Assert.Equal(UpdateCheckStatus.SkippedDevBuild, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("2.7.0-rc.1", false, "0.0.0")]
    [InlineData("2.7.0-rc.1", true, "2.7.0-rc.1")]
    [InlineData("2.7.0-beta", false, "2.7.0-beta")]
    [InlineData("2.7.0", false, "2.7.0")]
    [InlineData("2.7.0-src", false, "0.0.0")] // upstream's substring test, kept
    public void Comparable_version(string current, bool preReleases, string expected) =>
        Assert.Equal(SemVer.Parse(expected), AppReleaseService.ComparableVersion(SemVer.Parse(current), preReleases));

    [Fact]
    public async Task An_unparsable_running_version_fails_the_check()
    {
        var result = await Service(new RecordedResponseHandler()).CheckAsync("1.2", false, Ct);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(ScrapeMessageKeys.Generic, result.Error?.MessageKey);
    }

    // ---- Notes ----

    [Fact]
    public async Task Release_notes_are_flattened_to_plain_text()
    {
        var notes = await Service(Recorded()).GetReleaseNotesAsync(SemVer.Parse("2.8.1"), SemVer.Parse("2.8.2"), false, Ct);

        var note = Assert.Single(notes);
        Assert.StartsWith("# What's Changed", note.Body, StringComparison.Ordinal);
        Assert.Equal(
            """
            What's Changed

            🛠️ Fixes:
            • Fix Bforartists builds not being found after Bforartists moved its downloads to new servers
            • Fix the Install button staying visible in the download window while updating a build
            • Fix missing UI translations and fill gaps in the Japanese translation
            """.ReplaceLineEndings("\n"),
            note.PlainText);
    }

    [Fact]
    public async Task Notes_between_exclude_the_current_and_include_the_latest()
    {
        var notes = await Service(Recorded()).GetReleaseNotesAsync(SemVer.Parse("2.7.0"), SemVer.Parse("2.8.0"), true, Ct);

        Assert.Equal(["v2.8.0", "v2.7.6", "v2.7.1"], notes.Select(n => n.Tag));
    }

    // ---- Failures ----

    [Fact]
    public async Task A_connection_failure_is_reported_not_thrown()
    {
        var handler = new RecordedResponseHandler().Add(new FixtureEntry { Url = LatestUrl, Fail = "connection" });

        var result = await Service(handler).CheckAsync("1.0.0", false, Ct);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(AppReleaseService.SourceId, result.Error?.SourceId);
        Assert.Equal(ScrapeMessageKeys.ConnectionFailed, result.Error?.MessageKey);
    }

    [Fact]
    public async Task A_missing_repository_reports_its_status()
    {
        var handler = new RecordedResponseHandler().AddText(LatestUrl, "Not Found", status: 404);

        var result = await Service(handler).CheckAsync("1.0.0", false, Ct);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(HttpStatusCode.NotFound, result.Error?.Status);
    }

    [Fact]
    public async Task A_rate_limited_release_list_is_reported_as_such()
    {
        var handler = new RecordedResponseHandler().AddText(ListUrl,
            """{"message":"API rate limit exceeded for 0.0.0.0."}""", "application/json", status: 403,
            headers: [new("X-RateLimit-Remaining", "0"), new("X-RateLimit-Reset", "1794405160")]);

        var result = await Service(handler).CheckAsync("1.0.0", true, Ct);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(ScrapeMessageKeys.GitHubRateLimited, result.Error?.MessageKey);
    }

    [Fact]
    public async Task A_malformed_release_list_is_reported()
    {
        var handler = new RecordedResponseHandler().AddText(ListUrl, """{"not":"a list"}""", "application/json");

        var result = await Service(handler).CheckAsync("1.0.0", true, Ct);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(ScrapeMessageKeys.Generic, result.Error?.MessageKey);
    }

    [Fact]
    public async Task Failed_notes_still_offer_the_update()
    {
        var handler = new RecordedResponseHandler()
            .AddRedirect(LatestUrl, "https://github.com/" + Repo + "/releases/tag/v2.8.2")
            .AddText("https://github.com/" + Repo + "/releases/tag/v2.8.2", "")
            .Add(new FixtureEntry { Url = ListUrl, Fail = "timeout" });

        var result = await Service(handler).CheckAsync("2.0.0", false, Ct);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Empty(result.Notes);
        Assert.Equal(ScrapeMessageKeys.ConnectionFailed, result.Error?.MessageKey);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(Recorded()).CheckAsync("1.0.0", false, cts.Token));
    }
}
