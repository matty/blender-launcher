using System.Text.Json;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Updates;

/// <summary>A launcher release: its tag as written and the tag's version.</summary>
public sealed record AppRelease(string Tag, SemVer Version);

/// <summary>One release's notes, newest first in <see cref="UpdateCheckResult.Notes"/>.</summary>
/// <param name="Tag">The tag as written, for example <c>v1.2.0</c>.</param>
/// <param name="Body">The Markdown body as GitHub returned it ("" when none).</param>
/// <param name="PlainText"><paramref name="Body"/> through <see cref="MarkdownText.ToPlainText"/>; "" when there are no notes.</param>
public sealed record ReleaseNote(string Tag, SemVer Version, bool IsPrerelease, string Body, string PlainText);

public enum UpdateCheckStatus
{
    /// <summary>No update repository is configured (<see cref="AppIdentity.UpdateRepo"/> unset); nothing was requested.</summary>
    Disabled,

    /// <summary>The running version is a development build (upstream: "dev" in the build metadata); nothing was requested.</summary>
    SkippedDevBuild,

    /// <summary>The repository has no published release (or none for this channel).</summary>
    NoRelease,

    UpToDate,

    UpdateAvailable,

    /// <summary>The latest release could not be determined; see <see cref="UpdateCheckResult.Error"/>.</summary>
    Failed,
}

/// <summary>The outcome of <see cref="AppReleaseService.CheckAsync"/>.</summary>
/// <param name="Latest">The newest release of the channel, when found.</param>
/// <param name="Notes">Notes for the releases after the running version up to <paramref name="Latest"/>, newest first; empty when none or not fetched.</param>
/// <param name="Error">Why the check failed (<see cref="UpdateCheckStatus.Failed"/>), or why the notes are missing while an update is available.</param>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    AppRelease? Latest = null,
    IReadOnlyList<ReleaseNote>? Notes = null,
    ScrapeError? Error = null)
{
    public IReadOnlyList<ReleaseNote> Notes { get; init; } = Notes ?? [];
}

/// <summary>
/// Finds the launcher's newest release and its notes (port of upstream's
/// <c>threads/scraping/launcher_updates.py</c> and <c>window.py:880-904</c>).
/// <list type="bullet">
/// <item><b>Stable channel:</b> <c>GET https://github.com/{repo}/releases/latest</c>; the HTTP pipeline follows the
/// redirect and the tag is the last segment of the final <c>…/releases/tag/{tag}</c> URL.</item>
/// <item><b>Pre-release channel:</b> the API release list; tags with a <c>.zip</c> asset whose name contains
/// <c>windows</c> (both case-insensitive), the highest semver wins.</item>
/// <item><b>Notes:</b> the list at <c>?per_page=100</c>; releases after the running version up to the latest,
/// pre-releases only on the pre-release channel, newest first. The pre-release channel reads this one list for
/// both its tag and the notes (upstream made two requests).</item>
/// </list>
/// With no repository configured the service is disabled and makes no requests. Failures are reported in the
/// result and logged, never thrown, except cancellation by the caller.
/// </summary>
public sealed class AppReleaseService
{
    /// <summary>Source id on reported errors.</summary>
    public const string SourceId = "launcher-update";

    private readonly Func<HttpClient> _http;
    private readonly Log? _log;
    private readonly TimeProvider _time;

    /// <summary>Creates the service for <see cref="AppIdentity.UpdateRepo"/> over the provider's current client.</summary>
    public AppReleaseService(HttpClientProvider http, Log? log = null)
        : this(AppIdentity.UpdateRepo, () => http.Client, log)
    {
        ArgumentNullException.ThrowIfNull(http);
    }

    /// <summary>
    /// Creates the service for <paramref name="repo"/> (<c>owner/name</c>; null or empty disables it).
    /// <paramref name="http"/> is called per request. Throws <see cref="ArgumentException"/> for a malformed repo.
    /// </summary>
    public AppReleaseService(string? repo, Func<HttpClient> http, Log? log = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (!string.IsNullOrEmpty(repo) && !ReleaseAssets.IsValidRepo(repo))
        {
            throw new ArgumentException($"'{repo}' is not a GitHub repository as owner/name.", nameof(repo));
        }

        Repo = string.IsNullOrEmpty(repo) ? null : repo;
        _http = http;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The repository, or null when the service is disabled.</summary>
    public string? Repo { get; }

    public bool IsEnabled => Repo is not null;

    /// <summary><c>https://github.com/{repo}/releases/latest</c>.</summary>
    public Uri LatestReleaseUrl => new($"https://github.com/{RequireRepo()}/releases/latest");

    /// <summary><c>https://api.github.com/repos/{repo}/releases?per_page=100</c>.</summary>
    public Uri ReleaseListUrl => new($"https://api.github.com/repos/{RequireRepo()}/releases?per_page=100");

    /// <summary>
    /// The full check: the newest release of the channel, whether it is newer than <paramref name="currentVersion"/>
    /// (see <see cref="ComparableVersion"/>), and the notes in between.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, bool includePreReleases, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        if (!IsEnabled)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Disabled);
        }

        if (IsDevBuild(currentVersion))
        {
            _log?.Info($"Update check skipped for development build {currentVersion}");
            return new UpdateCheckResult(UpdateCheckStatus.SkippedDevBuild);
        }

        if (!TryParseTag(currentVersion, out var current))
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: new ScrapeError(SourceId, ScrapeMessageKeys.Generic, null,
                $"Running version '{currentVersion}' is not semver", [new("err", $"'{currentVersion}' is not a version")]));
        }

        try
        {
            AppRelease? latest;
            IReadOnlyList<GitHubReleaseDocument>? list = null;
            if (includePreReleases)
            {
                list = await FetchReleaseListAsync(ct).ConfigureAwait(false);
                latest = LatestWithWindowsZip(list);
            }
            else
            {
                latest = await GetLatestStableAsync(ct).ConfigureAwait(false);
            }

            if (latest is null)
            {
                _log?.Info($"No launcher release found in {Repo}");
                return new UpdateCheckResult(UpdateCheckStatus.NoRelease);
            }

            _log?.Info($"Latest launcher release: {latest.Tag}");
            if (latest.Version <= ComparableVersion(current, includePreReleases))
            {
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, latest);
            }

            ScrapeError? notesError = null;
            IReadOnlyList<ReleaseNote> notes = [];
            try
            {
                list ??= await FetchReleaseListAsync(ct).ConfigureAwait(false);
                notes = NotesBetween(list, current, latest.Version, includePreReleases);
            }
            catch (Exception e) when (IsFailure(e, ct))
            {
                notesError = ToError(e);
                _log?.Warning("Could not fetch launcher release notes", e);
            }

            return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, latest, notes, notesError);
        }
        catch (Exception e) when (IsFailure(e, ct))
        {
            _log?.Warning("Launcher update check failed", e);
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: ToError(e));
        }
    }

    /// <summary>
    /// The stable channel's newest release, or null when the repository has none. Throws
    /// <see cref="ScrapeSourceException"/> (or a network exception) on failure; disabled services throw
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task<AppRelease?> GetLatestStableAsync(CancellationToken ct = default)
    {
        var url = LatestReleaseUrl;
        using var cts = NetTimeouts.Linked(ct, NetTimeouts.Request);

        // Only the final URL matters, so the tag page's body is never read.
        using var response = await _http().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ScrapeSourceException(ScrapeError.HttpStatusError(SourceId, response.StatusCode, $"GET {url} returned {(int)response.StatusCode}"));
        }

        var final = response.RequestMessage?.RequestUri ?? url;
        return TagFromReleaseUrl(final, RequireRepo()) is { } tag && TryParseTag(tag, out var version)
            ? new AppRelease(tag, version)
            : null;
    }

    /// <summary>
    /// The pre-release channel's newest release (any release, pre-release or not, with a Windows zip), or null
    /// when there is none. Throws as <see cref="GetLatestStableAsync"/>.
    /// </summary>
    public async Task<AppRelease?> GetLatestWithPreReleasesAsync(CancellationToken ct = default) =>
        LatestWithWindowsZip(await FetchReleaseListAsync(ct).ConfigureAwait(false));

    /// <summary>Notes for the releases after <paramref name="current"/> up to <paramref name="latest"/>. Throws as <see cref="GetLatestStableAsync"/>.</summary>
    public async Task<IReadOnlyList<ReleaseNote>> GetReleaseNotesAsync(SemVer current, SemVer latest, bool includePreReleases, CancellationToken ct = default) =>
        NotesBetween(await FetchReleaseListAsync(ct).ConfigureAwait(false), current, latest, includePreReleases);

    /// <summary>
    /// Upstream's rule for the running version: with the pre-release channel off, a release candidate (an
    /// <c>rc</c> in the pre-release text) compares as 0.0.0, so the stable release that follows it is offered.
    /// </summary>
    public static SemVer ComparableVersion(SemVer current, bool includePreReleases) =>
        !includePreReleases && current.Prerelease is { } pre && pre.Contains("rc", StringComparison.Ordinal)
            ? new SemVer(0, 0, 0)
            : current;

    /// <summary>
    /// True when <paramref name="version"/> is a development build, which never checks for updates: "dev" in the
    /// build metadata (upstream's rule) or in the pre-release text (<see cref="AppIdentity.Version"/> drops build
    /// metadata, so a development build is marked there).
    /// </summary>
    public static bool IsDevBuild(string version) =>
        TryParseTag(version, out var v) &&
        ((v.Build?.Contains("dev", StringComparison.Ordinal) ?? false) || (v.Prerelease?.Contains("dev", StringComparison.Ordinal) ?? false));

    /// <summary>A tag or version with its leading <c>v</c>s removed (as upstream's <c>lstrip("v")</c>), as strict semver.</summary>
    public static bool TryParseTag(string? tag, out SemVer version)
    {
        version = default;
        return tag is not null && SemVer.TryParse(tag.TrimStart('v'), out version);
    }

    /// <summary>
    /// The tag in <c>https://github.com/{repo}/releases/tag/{tag}</c>, or null for any other URL (GitHub sends
    /// <c>/releases/latest</c> to <c>/releases</c> when there is no release; upstream took "releases" as the tag).
    /// </summary>
    public static string? TagFromReleaseUrl(Uri url, string repo)
    {
        ArgumentNullException.ThrowIfNull(url);
        var prefix = $"/{repo}/releases/tag/";
        var path = url.AbsolutePath;
        if (!url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tag = Uri.UnescapeDataString(path[prefix.Length..].TrimEnd('/'));
        return tag.Length == 0 || tag.Contains('/', StringComparison.Ordinal) ? null : tag;
    }

    /// <summary>The highest-versioned release with a Windows zip, drafts excluded; the tag is kept as written.</summary>
    public static AppRelease? LatestWithWindowsZip(IEnumerable<GitHubReleaseDocument> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        AppRelease? best = null;
        foreach (var release in releases)
        {
            if (release.Draft || !TryParseTag(release.TagName, out var version) ||
                !(release.Assets ?? []).Any(IsWindowsZip))
            {
                continue;
            }

            if (best is null || version > best.Version)
            {
                best = new AppRelease(release.TagName!, version);
            }
        }

        return best;
    }

    /// <summary>
    /// Releases with <paramref name="current"/> &lt; version ≤ <paramref name="latest"/>, drafts excluded and
    /// pre-releases excluded unless <paramref name="includePreReleases"/>, newest first.
    /// </summary>
    public static IReadOnlyList<ReleaseNote> NotesBetween(IEnumerable<GitHubReleaseDocument> releases, SemVer current, SemVer latest, bool includePreReleases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        var notes = new List<ReleaseNote>();
        foreach (var release in releases)
        {
            if (release.Draft || (release.Prerelease && !includePreReleases) || !TryParseTag(release.TagName, out var version))
            {
                continue;
            }

            if (current < version && version <= latest)
            {
                var body = release.Body ?? "";
                notes.Add(new ReleaseNote(release.TagName!, version, release.Prerelease, body, MarkdownText.ToPlainText(body)));
            }
        }

        // Stable sort, so equal versions keep the list's order.
        return [.. notes.OrderByDescending(n => n.Version)];
    }

    private static bool IsWindowsZip(GitHubAssetDocument asset) =>
        asset.Name is { } name &&
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
        name.Contains("windows", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<GitHubReleaseDocument>> FetchReleaseListAsync(CancellationToken ct)
    {
        var url = ReleaseListUrl;
        using var cts = NetTimeouts.Linked(ct, NetTimeouts.Request);
        using var response = await _http().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (GitHubRateLimit.IsRateLimited(response, body))
        {
            throw new ScrapeSourceException(ScrapeError.GitHubRateLimited(SourceId, response));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ScrapeSourceException(ScrapeError.HttpStatusError(SourceId, response.StatusCode, $"GET {url} returned {(int)response.StatusCode}"));
        }

        return JsonSerializer.Deserialize(body, UpdatesJsonContext.Default.ListGitHubReleaseDocument)
               ?? throw new JsonException("Release list is the JSON literal null");
    }

    private string RequireRepo() => Repo ?? throw new InvalidOperationException("Self-update is disabled: no update repository is configured.");

    private ScrapeError ToError(Exception e) => ScrapeError.FromException(SourceId, e, _time);

    /// <summary>A failure to report: anything from the network or parsing, but not a cancellation the caller asked for.</summary>
    private static bool IsFailure(Exception e, CancellationToken ct) =>
        e is ScrapeSourceException or HttpRequestException or IOException or JsonException ||
        (e is OperationCanceledException && !ct.IsCancellationRequested);
}
