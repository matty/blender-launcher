using System.Runtime.CompilerServices;
using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.App.ViewModels.Lists;

/// <summary>
/// Filtering and sorting of list rows, kept apart from the view model so the rules are testable on their own.
/// </summary>
/// <remarks>
/// The filter is upstream's search-bar rule (<see cref="VersionSearchQuery.ParseSearchText"/>): the first word is tried
/// as a version query (<c>5.2.^</c>, <c>^.*.*</c>, <c>4.2.14+6a3d0b8</c>) with the rest as fuzzy text; otherwise the
/// whole text is fuzzy text, matched by <see cref="FuzzyMatcher"/> over the build's version, branch, name, hash, commit
/// time and folder. Rows without readable build information (unrecognised or damaged folders) have nothing to match a
/// query against, so they match when their folder name contains the text.
/// </remarks>
public sealed class BuildListQuery
{
    // A row's search candidate, rebuilt only when its BuildInfo instance changes.
    private readonly ConditionalWeakTable<BuildRowViewModel, CandidateEntry> _candidates = [];

    /// <summary>The rows of <paramref name="rows"/> that match <paramref name="text"/>, in input order.</summary>
    public List<T> Filter<T>(IReadOnlyList<T> rows, string? text)
        where T : BuildRowViewModel
    {
        ArgumentNullException.ThrowIfNull(rows);
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return [.. rows];
        }

        var query = VersionSearchQuery.ParseSearchText(trimmed);
        var searchable = new List<(T Row, VersionSearchCandidate Candidate)>(rows.Count);
        var plain = new HashSet<T>(ReferenceEqualityComparer.Instance);
        foreach (var row in rows)
        {
            if (CandidateOf(row) is { } candidate)
            {
                searchable.Add((row, candidate));
            }
            else if (row.Version.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                     || row.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                plain.Add(row);
            }
        }

        var matched = new HashSet<T>(ReferenceEqualityComparer.Instance);
        foreach (var (row, _) in query.Match(searchable, static e => e.Candidate))
        {
            matched.Add(row);
        }

        var result = new List<T>(matched.Count + plain.Count);
        foreach (var row in rows)
        {
            if (matched.Contains(row) || plain.Contains(row))
            {
                result.Add(row);
            }
        }

        return result;
    }

    /// <summary>
    /// Sorts <paramref name="rows"/> in place by <paramref name="sort"/> (stable). Version ties fall back to the build
    /// time and the reverse, as upstream. Rows without build information always sort last, by name.
    /// </summary>
    public static void Sort<T>(List<T> rows, ViewSort sort)
        where T : BuildRowViewModel
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(sort);
        var indexed = new (T Row, int Index)[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            indexed[i] = (rows[i], i);
        }

        var sign = sort.Descending ? -1 : 1;
        Array.Sort(indexed, (a, b) =>
        {
            var aInfo = a.Row.Build is not null;
            var bInfo = b.Row.Build is not null;
            if (aInfo != bInfo)
            {
                return aInfo ? -1 : 1;
            }

            int result;
            if (!aInfo)
            {
                result = string.Compare(a.Row.Name, b.Row.Name, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                result = sort.Column == SortColumn.Version
                    ? sign * Chain(CompareVersion(a.Row, b.Row), CompareBuilt(a.Row, b.Row))
                    : sign * Chain(CompareBuilt(a.Row, b.Row), CompareVersion(a.Row, b.Row));
            }

            return result != 0 ? result : a.Index.CompareTo(b.Index);
        });

        for (var i = 0; i < indexed.Length; i++)
        {
            rows[i] = indexed[i].Row;
        }
    }

    /// <summary>The settings key a view's sort is remembered under (<see cref="SortViews"/>).</summary>
    public static string SortKeyOf(SidebarView view) =>
        view.Section == SidebarSection.Download ? SortViews.Download
        : view.Key == SidebarView.FavoritesKey ? SortViews.Favorites
        : SortViews.Installed;

    /// <summary>Whether an installed row belongs to the installed view <paramref name="viewKey"/>.</summary>
    public static bool InInstalledView(InstalledBuildRowViewModel row, string viewKey)
    {
        ArgumentNullException.ThrowIfNull(row);
        return viewKey switch
        {
            SidebarView.AllKey => true,
            SidebarView.FavoritesKey => row.IsFavourite,
            _ => DisplayFormat.ChannelFromKey(viewKey) is { } channel && row.Channel == channel,
        };
    }

    /// <summary>Whether a download row belongs to the download view of <paramref name="channel"/>.</summary>
    public static bool InDownloadView(AvailableBuildRowViewModel row, Channel? channel)
    {
        ArgumentNullException.ThrowIfNull(row);
        return channel is { } c && row.Channel == c;
    }

    private static int Chain(int first, int second) => first != 0 ? first : second;

    private static int CompareVersion(BuildRowViewModel a, BuildRowViewModel b) =>
        (a.Build?.SemVersion, b.Build?.SemVersion) switch
        {
            ({ } x, { } y) => x.CompareTo(y),
            (null, null) => 0,
            (null, _) => -1,
            _ => 1,
        };

    private static int CompareBuilt(BuildRowViewModel a, BuildRowViewModel b) =>
        Nullable.Compare(a.BuiltAt, b.BuiltAt);

    private VersionSearchCandidate? CandidateOf(BuildRowViewModel row)
    {
        if (row.Build is not { } build)
        {
            return null;
        }

        if (_candidates.TryGetValue(row, out var entry) && ReferenceEquals(entry.Build, build))
        {
            return entry.Candidate;
        }

        var folder = row is InstalledBuildRowViewModel installed ? installed.FolderName : null;
        VersionSearchCandidate.TryFromBuildInfo(build, out var candidate, folder);
        _candidates.AddOrUpdate(row, new CandidateEntry(build, candidate));
        return candidate;
    }

    private sealed record CandidateEntry(BuildInfo Build, VersionSearchCandidate? Candidate);
}
