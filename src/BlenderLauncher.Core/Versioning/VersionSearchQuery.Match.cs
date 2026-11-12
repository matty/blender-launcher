using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.Core.Versioning;

public sealed partial record VersionSearchQuery
{
    /// <summary>Upstream's <c>match</c>; see <see cref="Match{T}"/>.</summary>
    public List<VersionSearchCandidate> Match(IEnumerable<VersionSearchCandidate> candidates) =>
        Match(candidates, static c => c);

    /// <summary>
    /// Upstream's <c>match_versions</c> (<c>version_matcher.py:306-334</c>): for each relevant place, in priority
    /// order, the remaining items are narrowed. Input order is kept and the result stops early once empty.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><see cref="FuzzyText"/>: <see cref="FuzzyMatcher.Filter{T}"/> over <see cref="VersionSearchCandidate.Haystack"/>.</item>
    /// <item><see cref="After"/> and <see cref="Before"/>: commit time at or after, at or before (inclusive).</item>
    /// <item><c>^</c> or <c>-</c>: the items whose value equals the largest or smallest among the remaining
    /// items, so <c>^.^</c> is the newest minor of the newest major.</item>
    /// <item><see cref="Branch"/>: the items whose branch equals one of the alternatives.</item>
    /// <item>Anything else: equality. An exact commit time compares instants; a naive one matches nothing.</item>
    /// </list>
    /// Upstream raises on <c>^</c> or <c>-</c> over an empty input (Python's <c>max</c> of nothing); this returns an
    /// empty list.
    /// </remarks>
    public List<T> Match<T>(IEnumerable<T> items, Func<T, VersionSearchCandidate> candidateOf)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(candidateOf);

        var current = items.Select(i => new Entry<T>(i, candidateOf(i))).ToList();
        if (current.Count == 0)
        {
            return [];
        }

        for (var place = VersionSearchPlace.FuzzyText; place <= VersionSearchPlace.Before; place++)
        {
            if (!IsRelevant(place))
            {
                continue;
            }

            current = Narrow(current, place);
            if (current.Count == 0)
            {
                return [];
            }
        }

        return current.ConvertAll(e => e.Item);
    }

    private List<Entry<T>> Narrow<T>(List<Entry<T>> entries, VersionSearchPlace place)
    {
        switch (place)
        {
            case VersionSearchPlace.FuzzyText:
                return FuzzyMatcher.Filter(entries, static e => e.Candidate.Haystack, FuzzyText!);
            case VersionSearchPlace.After:
                return entries.FindAll(e => e.Candidate.CommitTime >= After!.Value);
            case VersionSearchPlace.Before:
                return entries.FindAll(e => e.Candidate.CommitTime <= Before!.Value);
            case VersionSearchPlace.Folder:
                return Narrow(entries, static c => c.Folder, Folder!, StringComparer.Ordinal);
            case VersionSearchPlace.BuildHash:
                return Narrow(entries, static c => c.BuildHash, BuildHash!, StringComparer.Ordinal);
            case VersionSearchPlace.Major:
                return Narrow(entries, static c => c.Major, Major);
            case VersionSearchPlace.Minor:
                return Narrow(entries, static c => c.Minor, Minor);
            case VersionSearchPlace.Patch:
                return Narrow(entries, static c => c.Patch, Patch);
            case VersionSearchPlace.Branch:
                var branches = Branch!;
                return entries.FindAll(e => branches.Contains(e.Candidate.Branch, StringComparer.Ordinal));
            case VersionSearchPlace.CommitTime:
                var time = CommitTime!.Value;
                return time.Kind switch
                {
                    VersionSearchPositionKind.Newest => KeepExtreme(entries, static c => c.CommitTime, Comparer<DateTimeOffset>.Default, newest: true),
                    VersionSearchPositionKind.Oldest => KeepExtreme(entries, static c => c.CommitTime, Comparer<DateTimeOffset>.Default, newest: false),
                    _ => entries.FindAll(e => IsoTimestamp.Aware(e.Candidate.CommitTime) == time.Value),
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(place));
        }
    }

    private static List<Entry<T>> Narrow<T>(List<Entry<T>> entries, Func<VersionSearchCandidate, int> key, VersionSearchPosition position) =>
        position.Kind switch
        {
            VersionSearchPositionKind.Newest => KeepExtreme(entries, key, Comparer<int>.Default, newest: true),
            VersionSearchPositionKind.Oldest => KeepExtreme(entries, key, Comparer<int>.Default, newest: false),
            _ => entries.FindAll(e => key(e.Candidate) == position.Value),
        };

    // A text place: upstream applies ^ and - to any place, comparing strings by code point; null sorts lowest.
    private static List<Entry<T>> Narrow<T>(
        List<Entry<T>> entries, Func<VersionSearchCandidate, string?> key, string value, StringComparer comparer) =>
        value switch
        {
            "^" => KeepExtreme(entries, key, comparer, newest: true),
            "-" => KeepExtreme(entries, key, comparer, newest: false),
            _ => entries.FindAll(e => comparer.Equals(key(e.Candidate), value)),
        };

    private static List<Entry<T>> KeepExtreme<T, TKey>(
        List<Entry<T>> entries, Func<VersionSearchCandidate, TKey> key, IComparer<TKey> comparer, bool newest)
    {
        var best = key(entries[0].Candidate);
        foreach (var e in entries)
        {
            var k = key(e.Candidate);
            var c = comparer.Compare(k, best);
            if (newest ? c > 0 : c < 0)
            {
                best = k;
            }
        }

        return entries.FindAll(e => comparer.Compare(key(e.Candidate), best) == 0);
    }

    private readonly record struct Entry<T>(T Item, VersionSearchCandidate Candidate);
}
