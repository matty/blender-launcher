using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Settings;

/// <summary>
/// Typed access to <see cref="BuildsSettings.VersionSpecificQueries"/>, as upstream's
/// <c>get_version_specific_queries</c> and <c>set_version_specific_queries</c>: blend-file version to the query
/// used to pick a build for it.
/// </summary>
public static class VersionSpecificQueries
{
    /// <summary>
    /// The stored queries, parsed. Unlike upstream, which raised on the first bad entry, an entry whose version or
    /// query does not parse is skipped.
    /// </summary>
    public static IReadOnlyDictionary<SemVer, VersionSearchQuery> Get(BuildsSettings builds)
    {
        ArgumentNullException.ThrowIfNull(builds);
        var result = new Dictionary<SemVer, VersionSearchQuery>();
        foreach (var (version, query) in builds.VersionSpecificQueries)
        {
            if (SemVer.TryParse(version, out var v) && VersionSearchQuery.TryParse(query, out var q))
            {
                result[v] = q;
            }
        }

        return result;
    }

    /// <summary>Returns <paramref name="builds"/> with the queries replaced by <paramref name="queries"/>.</summary>
    public static BuildsSettings With(BuildsSettings builds, IReadOnlyDictionary<SemVer, VersionSearchQuery> queries)
    {
        ArgumentNullException.ThrowIfNull(builds);
        ArgumentNullException.ThrowIfNull(queries);
        return builds with
        {
            VersionSpecificQueries = queries.ToDictionary(p => p.Key.ToString(), p => p.Value.ToString(), StringComparer.Ordinal),
        };
    }

    /// <summary>Returns <paramref name="builds"/> with the query for <paramref name="version"/> added or replaced.</summary>
    public static BuildsSettings WithQuery(BuildsSettings builds, SemVer version, VersionSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(builds);
        ArgumentNullException.ThrowIfNull(query);
        var copy = new Dictionary<string, string>(builds.VersionSpecificQueries, StringComparer.Ordinal)
        {
            [version.ToString()] = query.ToString(),
        };
        return builds with { VersionSpecificQueries = copy };
    }
}
