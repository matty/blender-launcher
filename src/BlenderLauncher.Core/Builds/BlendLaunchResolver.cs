using System.Globalization;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Builds;

/// <summary>Where the query of a <see cref="BlendLaunchResolution{T}"/> came from.</summary>
public enum BlendLaunchSource
{
    /// <summary>The query given on the command line (<c>-v</c>).</summary>
    ExplicitQuery,

    /// <summary>The query saved for the file's Blender version.</summary>
    SavedQuery,

    /// <summary><c>major.minor.^</c> from the file's header.</summary>
    FileVersion,

    /// <summary>The default build, selected by its folder.</summary>
    DefaultBuild,

    /// <summary><c>^.^.^@^</c>: no file version, nothing matched, and no default build (or it is not installed).</summary>
    DefaultQuery,
}

/// <summary>The chosen query and the builds it matched, in input order.</summary>
public sealed record BlendLaunchResolution<T>(VersionSearchQuery Query, BlendLaunchSource Source, IReadOnlyList<T> Matches);

/// <summary>
/// Chooses the builds to offer when launching, optionally with a .blend file (upstream
/// <c>cli_launching.py:60-85</c> and <c>launching_window.py:335-370</c>, with the changes below):
/// <list type="number">
/// <item>An explicit query is used as is; no fallback applies to it.</item>
/// <item>With a readable header: the query saved for <c>M.m.0</c>, else <c>M.m.^</c>.</item>
/// <item>When the header is missing or unreadable, or step 2 matches nothing: the default build if one is set and
/// installed, else <c>^.^.^@^</c>. (Upstream falls back to <c>^.^.^@^</c> only when the header is unreadable.)</item>
/// </list>
/// </summary>
/// <remarks>
/// Upstream disagrees with itself on an explicit query together with a file: the CLI lets the file's version
/// replace it, the launch window lets only a saved query replace it. Here the explicit query wins, as the most
/// specific thing the user said.
/// </remarks>
public static class BlendLaunchResolver
{
    /// <summary>The key of a saved per-version query: <c>str(Version(major, minor, 0))</c>, for example <c>4.2.0</c>.</summary>
    public static string SavedQueryKey(int major, int minor) =>
        string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}.0");

    /// <summary>
    /// The saved per-version queries (setting <c>versionSpecificQueries</c>) parsed; entries whose text is not a valid
    /// query are skipped.
    /// </summary>
    public static IReadOnlyDictionary<string, VersionSearchQuery> ParseSavedQueries(IReadOnlyDictionary<string, string>? saved)
    {
        var result = new Dictionary<string, VersionSearchQuery>(StringComparer.Ordinal);
        if (saved is null)
        {
            return result;
        }

        foreach (var (key, text) in saved)
        {
            if (VersionSearchQuery.TryParse(text, out var query))
            {
                result[key] = query;
            }
        }

        return result;
    }

    /// <summary>Resolves over plain candidates.</summary>
    public static BlendLaunchResolution<VersionSearchCandidate> Resolve(
        IReadOnlyList<VersionSearchCandidate> candidates,
        BlendFileHeader? header,
        IReadOnlyDictionary<string, VersionSearchQuery>? savedQueries = null,
        VersionSearchQuery? explicitQuery = null,
        string? defaultBuildFolder = null) =>
        Resolve(candidates, static c => c, header, savedQueries, explicitQuery, defaultBuildFolder);

    /// <summary>
    /// Resolves the query and its matches.
    /// </summary>
    /// <param name="builds">The installed builds.</param>
    /// <param name="candidateOf">The match fields of a build; its <see cref="VersionSearchCandidate.Folder"/> must be set for the default build to be found.</param>
    /// <param name="header">The .blend header, or null when there is no file or it could not be read.</param>
    /// <param name="savedQueries">Saved queries keyed by <see cref="SavedQueryKey"/> (upstream <c>version_specific_queries</c>).</param>
    /// <param name="explicitQuery">A query given on the command line.</param>
    /// <param name="defaultBuildFolder">The default build's folder, compared exactly with the candidates' folders.</param>
    public static BlendLaunchResolution<T> Resolve<T>(
        IReadOnlyList<T> builds,
        Func<T, VersionSearchCandidate> candidateOf,
        BlendFileHeader? header,
        IReadOnlyDictionary<string, VersionSearchQuery>? savedQueries = null,
        VersionSearchQuery? explicitQuery = null,
        string? defaultBuildFolder = null)
    {
        ArgumentNullException.ThrowIfNull(builds);
        ArgumentNullException.ThrowIfNull(candidateOf);

        if (explicitQuery is not null)
        {
            return new(explicitQuery, BlendLaunchSource.ExplicitQuery, explicitQuery.Match(builds, candidateOf));
        }

        if (header is not null)
        {
            var (query, source) = savedQueries is not null
                && savedQueries.TryGetValue(SavedQueryKey(header.Major, header.Minor), out var saved)
                ? (saved, BlendLaunchSource.SavedQuery)
                : (VersionSearchQuery.Version(header.Major, header.Minor, VersionSearchPosition.Newest), BlendLaunchSource.FileVersion);
            var matches = query.Match(builds, candidateOf);
            if (matches.Count > 0)
            {
                return new(query, source, matches);
            }
        }

        if (!string.IsNullOrEmpty(defaultBuildFolder))
        {
            var query = VersionSearchQuery.Any() with { Folder = defaultBuildFolder };
            var matches = query.Match(builds, candidateOf);
            if (matches.Count > 0)
            {
                return new(query, BlendLaunchSource.DefaultBuild, matches);
            }
        }

        var fallback = VersionSearchQuery.Default();
        return new(fallback, BlendLaunchSource.DefaultQuery, fallback.Match(builds, candidateOf));
    }
}
