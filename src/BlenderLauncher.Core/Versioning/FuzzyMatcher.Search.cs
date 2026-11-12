using System.Buffers;

namespace BlenderLauncher.Core.Versioning;

// A port of the search paths fuzzysearch 0.8.1 takes for upstream's parameters (max_deletions,
// max_substitutions and max_insertions all 1). Only "is there any match" is needed, so each search returns true
// at the point where fuzzysearch would yield its first match; match positions and the overlap consolidation that
// find_near_matches applies afterwards cannot change whether the list is empty.
//
//   d = 0  ExactSearch: plain substring search.
//   d = 1  LevenshteinSearch (max_l_dist <= every per-kind limit): the n-gram search when
//          len(needle) // 2 >= 3, otherwise the candidate ("linear programming") search.
//   d >= 2 GenericSearch with max_l_dist = min(d, 3): the n-gram search when len(needle) // (d + 1) >= 3,
//          otherwise the generic candidate search.
//
// The candidate searches are greedy: a candidate that meets an equal character always advances and never also
// tries an edit there. That is fuzzysearch's behaviour, not a slip in the port, so it is kept.
//
// Python compares code points. Text without surrogates is searched as UTF-16 units, which is the same thing;
// text with a surrogate pair is first widened to code points so that a pair counts as one character.
public static partial class FuzzyMatcher
{
    private const int KindLimit = 1;

    private static bool MatchesFolded(ReadOnlySpan<char> needle, ReadOnlySpan<char> haystack, int distance)
    {
        if (needle.IsEmpty)
        {
            return true;
        }

        if (needle.ContainsAnyInRange('\uD800', '\uDFFF') || haystack.ContainsAnyInRange('\uD800', '\uDFFF'))
        {
            var needlePoints = ArrayPool<int>.Shared.Rent(needle.Length);
            var haystackPoints = ArrayPool<int>.Shared.Rent(Math.Max(haystack.Length, 1));
            try
            {
                var n = ToCodePoints(needle, needlePoints);
                var h = ToCodePoints(haystack, haystackPoints);
                return Matches<int>(needlePoints.AsSpan(0, n), haystackPoints.AsSpan(0, h), distance);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(needlePoints);
                ArrayPool<int>.Shared.Return(haystackPoints);
            }
        }

        return Matches(needle, haystack, distance);
    }

    private static int ToCodePoints(ReadOnlySpan<char> text, int[] destination)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                destination[count++] = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else
            {
                destination[count++] = text[i];
            }
        }

        return count;
    }

    private static bool Matches<T>(ReadOnlySpan<T> needle, ReadOnlySpan<T> haystack, int distance)
        where T : unmanaged, IEquatable<T>
    {
        // LevenshteinSearchParams._normalize_params: max_l_dist = min(max_l_dist, sum of the per-kind limits).
        var maxDistance = Math.Min(distance, 3 * KindLimit);

        if (maxDistance == 0)
        {
            return haystack.IndexOf(needle) >= 0;
        }

        if (maxDistance <= KindLimit)
        {
            return needle.Length / (maxDistance + 1) >= 3
                ? LevenshteinNgrams(needle, haystack, maxDistance)
                : LevenshteinCandidates(needle, haystack, maxDistance);
        }

        return needle.Length / (maxDistance + 1) >= 3
            ? GenericNgrams(needle, haystack, maxDistance)
            : GenericCandidates(needle, haystack, maxDistance);
    }

    // search_exact(subsequence, sequence, start_index, end_index) for one hit: str.find within [start, end).
    private static int FindExact<T>(ReadOnlySpan<T> needle, ReadOnlySpan<T> haystack, int start, int end)
        where T : unmanaged, IEquatable<T>
    {
        start = Math.Clamp(start, 0, haystack.Length);
        end = Math.Clamp(end, start, haystack.Length);
        var found = haystack[start..end].IndexOf(needle);
        return found < 0 ? -1 : start + found;
    }

    // Python's sequence[start:stop] for non-negative indices: both ends clamped, empty when start > stop.
    private static ReadOnlySpan<T> PySlice<T>(ReadOnlySpan<T> sequence, int start, int stop)
    {
        start = Math.Min(start, sequence.Length);
        stop = Math.Clamp(stop, start, sequence.Length);
        return sequence[start..stop];
    }

    // levenshtein.find_near_matches_levenshtein_linear_programming.
    private static bool LevenshteinCandidates<T>(ReadOnlySpan<T> needle, ReadOnlySpan<T> haystack, int maxDistance)
        where T : unmanaged, IEquatable<T>
    {
        var m = needle.Length;
        if (maxDistance >= m)
        {
            // fuzzysearch yields an empty match at every index, including for an empty haystack.
            return true;
        }

        var current = new CandidateList(m);
        var next = new CandidateList(m);
        try
        {
            for (var index = 0; index < haystack.Length; index++)
            {
                var ch = haystack[index];
                next.Clear();

                // char2first_subseq_index: the first of needle[0..maxDistance] equal to this character.
                var first = needle[..(maxDistance + 1)].IndexOf(ch);
                if (first >= 0)
                {
                    if (first + 1 == m)
                    {
                        return true;
                    }

                    next.Add(new Candidate(first + 1, first));
                }

                foreach (var cand in current.Items)
                {
                    if (needle[cand.NeedleIndex].Equals(ch))
                    {
                        if (cand.NeedleIndex + 1 == m)
                        {
                            return true;
                        }

                        next.Add(cand with { NeedleIndex = cand.NeedleIndex + 1 });
                        continue;
                    }

                    if (cand.Distance == maxDistance)
                    {
                        continue;
                    }

                    // Skip a haystack character.
                    next.Add(cand with { Distance = cand.Distance + 1 });

                    // Substitute (note fuzzysearch's guard against doing so on the haystack's last character).
                    if (index + 1 < haystack.Length && cand.NeedleIndex + 1 < m)
                    {
                        next.Add(cand with { NeedleIndex = cand.NeedleIndex + 1, Distance = cand.Distance + 1 });
                    }

                    // Skip needle characters until one equals this character, or the needle runs out.
                    for (var skipped = 1; skipped <= maxDistance - cand.Distance; skipped++)
                    {
                        if (cand.NeedleIndex + skipped == m)
                        {
                            return true;
                        }

                        if (needle[cand.NeedleIndex + skipped].Equals(ch))
                        {
                            if (cand.NeedleIndex + skipped + 1 == m)
                            {
                                return true;
                            }

                            next.Add(new Candidate(cand.NeedleIndex + 1 + skipped, cand.Distance + skipped));
                            break;
                        }
                    }
                }

                (current, next) = (next, current);
            }

            foreach (var cand in current.Items)
            {
                if (cand.Distance + m - cand.NeedleIndex <= maxDistance)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            current.Dispose();
            next.Dispose();
        }
    }

    // levenshtein_ngram.find_near_matches_levenshtein_ngrams.
    private static bool LevenshteinNgrams<T>(ReadOnlySpan<T> needle, ReadOnlySpan<T> haystack, int maxDistance)
        where T : unmanaged, IEquatable<T>
    {
        var m = needle.Length;
        var n = haystack.Length;
        var ngramLength = m / (maxDistance + 1);

        var rented = m > 128 ? ArrayPool<int>.Shared.Rent(m) : null;
        var scores = rented is null ? stackalloc int[m] : rented.AsSpan(0, m);
        try
        {
            for (var ngramStart = 0; ngramStart <= m - ngramLength; ngramStart += ngramLength)
            {
                var ngramEnd = ngramStart + ngramLength;
                var ngram = needle[ngramStart..ngramEnd];
                var after = needle[ngramEnd..];
                var before = needle[..ngramStart];
                var startIndex = Math.Max(0, ngramStart - maxDistance);
                var endIndex = Math.Min(n, n - m + ngramEnd + maxDistance);

                var index = FindExact(ngram, haystack, startIndex, endIndex);
                while (index >= 0)
                {
                    var right = PySlice(haystack, index + ngramLength, index - ngramStart + m + maxDistance);
                    var distRight = Expand(after, right, reversed: false, maxDistance, scores);
                    if (distRight >= 0)
                    {
                        var leftBudget = maxDistance - distRight;
                        var left = PySlice(haystack, Math.Max(0, index - ngramStart - leftBudget), index);
                        if (Expand(before, left, reversed: true, leftBudget, scores) >= 0)
                        {
                            return true;
                        }
                    }

                    index = FindExact(ngram, haystack, index + 1, endIndex);
                }
            }

            return false;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    // levenshtein_ngram._expand: the smallest edit distance between the needle part and any prefix of the
    // sequence (both read back to front when reversed), or -1 when it exceeds maxDistance. fuzzysearch's
    // _expand_short and _expand_long both compute this minimum; the long one only prunes the table.
    private static int Expand<T>(ReadOnlySpan<T> part, ReadOnlySpan<T> sequence, bool reversed, int maxDistance, Span<int> scores)
        where T : unmanaged, IEquatable<T>
    {
        var length = part.Length;
        if (length == 0)
        {
            return 0;
        }

        for (var i = 0; i < length; i++)
        {
            scores[i] = i + 1;
        }

        var best = length;
        for (var s = 0; s < sequence.Length; s++)
        {
            var ch = reversed ? sequence[sequence.Length - 1 - s] : sequence[s];
            var a = s;
            var c = a + 1;
            var rowMin = int.MaxValue;
            for (var i = 0; i < length; i++)
            {
                var b = scores[i];
                var p = reversed ? part[length - 1 - i] : part[i];
                c = Math.Min(Math.Min(a + (p.Equals(ch) ? 0 : 1), b + 1), c + 1);
                scores[i] = c;
                a = b;
                rowMin = Math.Min(rowMin, c);
            }

            if (c <= best)
            {
                best = c;
            }
            else if (rowMin >= best)
            {
                break;
            }
        }

        return best <= maxDistance ? best : -1;
    }

    // generic_search._find_near_matches_generic_linear_programming, with every per-kind limit 1.
    private static bool GenericCandidates<T>(ReadOnlySpan<T> needle, ReadOnlySpan<T> haystack, int maxDistance)
        where T : unmanaged, IEquatable<T>
    {
        var m = needle.Length;
        var current = new CandidateList(m);
        var next = new CandidateList(m);
        try
        {
            for (var index = 0; index < haystack.Length; index++)
            {
                var ch = haystack[index];
                current.Add(new Candidate(0, 0));
                next.Clear();

                foreach (var cand in current.Items)
                {
                    if (ch.Equals(needle[cand.NeedleIndex]))
                    {
                        if (cand.NeedleIndex + 1 == m)
                        {
                            return true;
                        }

                        next.Add(cand with { NeedleIndex = cand.NeedleIndex + 1 });
                        continue;
                    }

                    if (cand.Distance == maxDistance)
                    {
                        continue;
                    }

                    if (cand.Insertions < KindLimit)
                    {
                        // Skip a haystack character.
                        next.Add(cand with { Insertions = cand.Insertions + 1, Distance = cand.Distance + 1 });
                    }

                    if (cand.NeedleIndex + 1 < m)
                    {
                        if (cand.Substitutions < KindLimit)
                        {
                            next.Add(cand with
                            {
                                Substitutions = cand.Substitutions + 1,
                                NeedleIndex = cand.NeedleIndex + 1,
                                Distance = cand.Distance + 1,
                            });
                        }
                        else if (cand.Deletions < KindLimit && cand.Insertions < KindLimit)
                        {
                            // A substitution spelt as an insertion plus a deletion, counted as one edit.
                            next.Add(cand with
                            {
                                Insertions = cand.Insertions + 1,
                                Deletions = cand.Deletions + 1,
                                NeedleIndex = cand.NeedleIndex + 1,
                                Distance = cand.Distance + 1,
                            });
                        }
                    }
                    else if (cand.Substitutions < KindLimit || (cand.Deletions < KindLimit && cand.Insertions < KindLimit))
                    {
                        return true;
                    }

                    // Skip needle characters until one equals this character, or the needle runs out.
                    var maxSkip = Math.Min(KindLimit - cand.Deletions, maxDistance - cand.Distance);
                    for (var skipped = 1; skipped <= maxSkip; skipped++)
                    {
                        if (cand.NeedleIndex + skipped == m)
                        {
                            return true;
                        }

                        if (needle[cand.NeedleIndex + skipped].Equals(ch))
                        {
                            if (cand.NeedleIndex + skipped + 1 == m)
                            {
                                return true;
                            }

                            next.Add(cand with
                            {
                                Deletions = cand.Deletions + skipped,
                                NeedleIndex = cand.NeedleIndex + 1 + skipped,
                                Distance = cand.Distance + skipped,
                            });
                            break;
                        }
                    }
                }

                (current, next) = (next, current);
            }

            foreach (var cand in current.Items)
            {
                var skipped = m - cand.NeedleIndex;
                if (cand.Deletions + skipped <= KindLimit && cand.Distance + skipped <= maxDistance)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            current.Dispose();
            next.Dispose();
        }
    }

    // generic_search.find_near_matches_generic_ngrams: exact n-gram hits, each re-searched by the candidate
    // search over a window around it.
    private static bool GenericNgrams<T>(ReadOnlySpan<T> needle, ReadOnlySpan<T> haystack, int maxDistance)
        where T : unmanaged, IEquatable<T>
    {
        var m = needle.Length;
        var n = haystack.Length;
        var ngramLength = m / (maxDistance + 1);

        for (var ngramStart = 0; ngramStart <= m - ngramLength; ngramStart += ngramLength)
        {
            var ngramEnd = ngramStart + ngramLength;
            var ngram = needle[ngramStart..ngramEnd];
            var startIndex = Math.Max(0, ngramStart - maxDistance);
            var endIndex = Math.Min(n, n - m + ngramEnd + maxDistance);
            if (endIndex <= startIndex)
            {
                continue;
            }

            var index = FindExact(ngram, haystack, startIndex, endIndex);
            while (index >= 0)
            {
                var window = PySlice(haystack, Math.Max(0, index - ngramStart - maxDistance), index - ngramStart + m + maxDistance);
                if (GenericCandidates(needle, window, maxDistance))
                {
                    return true;
                }

                index = FindExact(ngram, haystack, index + 1, endIndex);
            }
        }

        return false;
    }

    private readonly record struct Candidate(int NeedleIndex, int Distance)
    {
        public int Substitutions { get; init; }
        public int Insertions { get; init; }
        public int Deletions { get; init; }
    }

    // A growable list over a pooled array, so a search allocates nothing once the pool is warm.
    private struct CandidateList
    {
        private Candidate[] _items;
        private int _count;

        public CandidateList(int needleLength)
        {
            _items = ArrayPool<Candidate>.Shared.Rent(Math.Max(16, needleLength * 4));
            _count = 0;
        }

        public readonly ReadOnlySpan<Candidate> Items => _items.AsSpan(0, _count);

        public void Clear() => _count = 0;

        public void Add(Candidate candidate)
        {
            if (_count == _items.Length)
            {
                var larger = ArrayPool<Candidate>.Shared.Rent(_items.Length * 2);
                _items.AsSpan(0, _count).CopyTo(larger);
                ArrayPool<Candidate>.Shared.Return(_items);
                _items = larger;
            }

            _items[_count++] = candidate;
        }

        public void Dispose()
        {
            ArrayPool<Candidate>.Shared.Return(_items);
            _items = [];
            _count = 0;
        }
    }
}
