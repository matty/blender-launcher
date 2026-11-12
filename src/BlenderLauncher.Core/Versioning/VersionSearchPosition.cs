using System.Globalization;
using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.Core.Versioning;

/// <summary>What a position in a version search query asks for.</summary>
public enum VersionSearchPositionKind
{
    /// <summary><c>*</c>: any value; the position does not filter.</summary>
    Any,

    /// <summary><c>^</c>: the largest (newest) value among the remaining candidates.</summary>
    Newest,

    /// <summary><c>-</c>: the smallest (oldest) value among the remaining candidates.</summary>
    Oldest,

    /// <summary>A given value.</summary>
    Exact,
}

/// <summary>
/// The major, minor or patch place of a <see cref="VersionSearchQuery"/>: upstream's <c>int | str</c>, where the
/// text may only be <c>^</c>, <c>*</c> or <c>-</c>. <c>default</c> is <c>*</c>.
/// </summary>
public readonly struct VersionSearchPosition : IEquatable<VersionSearchPosition>
{
    private VersionSearchPosition(VersionSearchPositionKind kind, int value)
    {
        Kind = kind;
        Value = value;
    }

    public VersionSearchPositionKind Kind { get; }

    /// <summary>The number for <see cref="VersionSearchPositionKind.Exact"/>; 0 otherwise.</summary>
    public int Value { get; }

    /// <summary><c>*</c>.</summary>
    public static VersionSearchPosition Any => default;

    /// <summary><c>^</c>.</summary>
    public static VersionSearchPosition Newest { get; } = new(VersionSearchPositionKind.Newest, 0);

    /// <summary><c>-</c>.</summary>
    public static VersionSearchPosition Oldest { get; } = new(VersionSearchPositionKind.Oldest, 0);

    public static VersionSearchPosition Exact(int value) => new(VersionSearchPositionKind.Exact, value);

    /// <summary>
    /// Upstream's text positions: <c>^</c>, <c>*</c> or <c>-</c>; anything else (including digits, which only the
    /// parser turns into numbers) throws <see cref="VersionSearchQueryException"/> with
    /// <see cref="VersionSearchQueryException.InvalidPositionKey"/>, as upstream's <c>__post_init__</c> does.
    /// </summary>
    public static VersionSearchPosition FromSymbol(string symbol) => symbol switch
    {
        "*" => Any,
        "^" => Newest,
        "-" => Oldest,
        _ => throw VersionSearchQueryException.InvalidPosition(symbol),
    };

    public static implicit operator VersionSearchPosition(int value) => Exact(value);

    /// <summary>See <see cref="FromSymbol"/>.</summary>
    public static implicit operator VersionSearchPosition(string symbol) => FromSymbol(symbol);

    /// <summary>The query text: <c>*</c>, <c>^</c>, <c>-</c> or the number.</summary>
    public override string ToString() => Kind switch
    {
        VersionSearchPositionKind.Newest => "^",
        VersionSearchPositionKind.Oldest => "-",
        VersionSearchPositionKind.Exact => Value.ToString(CultureInfo.InvariantCulture),
        _ => "*",
    };

    public bool Equals(VersionSearchPosition other) => Kind == other.Kind && Value == other.Value;

    public override bool Equals(object? obj) => obj is VersionSearchPosition other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, Value);

    public static bool operator ==(VersionSearchPosition left, VersionSearchPosition right) => left.Equals(right);

    public static bool operator !=(VersionSearchPosition left, VersionSearchPosition right) => !left.Equals(right);
}

/// <summary>
/// The commit-time place of a <see cref="VersionSearchQuery"/>: upstream's <c>datetime | str</c>, where the text
/// may only be <c>^</c>, <c>*</c> or <c>-</c>. <c>default</c> is <c>*</c>. An exact value keeps whether it was
/// naive or aware and its offset, as Python's <c>datetime</c> does; a naive value never equals a commit time.
/// </summary>
public readonly struct VersionSearchTime : IEquatable<VersionSearchTime>
{
    private VersionSearchTime(VersionSearchPositionKind kind, IsoTimestamp value)
    {
        Kind = kind;
        Value = value;
    }

    public VersionSearchPositionKind Kind { get; }

    /// <summary>The time for <see cref="VersionSearchPositionKind.Exact"/>; <c>default</c> otherwise.</summary>
    public IsoTimestamp Value { get; }

    /// <summary><c>*</c>.</summary>
    public static VersionSearchTime Any => default;

    /// <summary><c>^</c>: the newest commit time.</summary>
    public static VersionSearchTime Newest { get; } = new(VersionSearchPositionKind.Newest, default);

    /// <summary><c>-</c>: the oldest commit time.</summary>
    public static VersionSearchTime Oldest { get; } = new(VersionSearchPositionKind.Oldest, default);

    public static VersionSearchTime Exact(IsoTimestamp value) => new(VersionSearchPositionKind.Exact, value);

    /// <summary>
    /// <c>^</c>, <c>*</c> or <c>-</c>; anything else throws <see cref="VersionSearchQueryException"/> with
    /// <see cref="VersionSearchQueryException.InvalidPositionKey"/>.
    /// </summary>
    public static VersionSearchTime FromSymbol(string symbol) => symbol switch
    {
        "*" => Any,
        "^" => Newest,
        "-" => Oldest,
        _ => throw VersionSearchQueryException.InvalidPosition(symbol),
    };

    /// <summary>
    /// Upstream's parser step: a symbol, else <c>datetime.fromisoformat</c>, else the text is kept, which the
    /// query's validation then rejects with <see cref="VersionSearchQueryException.InvalidPositionKey"/>.
    /// </summary>
    public static VersionSearchTime Parse(string text) =>
        text is "^" or "*" or "-" ? FromSymbol(text)
        : IsoTimestamp.TryParse(text, out var value) ? Exact(value)
        : FromSymbol(text);

    public static implicit operator VersionSearchTime(string symbol) => FromSymbol(symbol);

    public static implicit operator VersionSearchTime(IsoTimestamp value) => Exact(value);

    public static implicit operator VersionSearchTime(DateTimeOffset value) => Exact(IsoTimestamp.Aware(value));

    /// <summary>
    /// The query text: a symbol, or Python's <c>str(datetime)</c>, which separates the date and time with a space
    /// (<c>2020-05-04 00:00:00+00:00</c>); the query grammar accepts that form.
    /// </summary>
    public override string ToString() => Kind switch
    {
        VersionSearchPositionKind.Newest => "^",
        VersionSearchPositionKind.Oldest => "-",
        VersionSearchPositionKind.Exact => PythonStr(Value),
        _ => "*",
    };

    public bool Equals(VersionSearchTime other) => Kind == other.Kind && Value == other.Value;

    public override bool Equals(object? obj) => obj is VersionSearchTime other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, Value);

    public static bool operator ==(VersionSearchTime left, VersionSearchTime right) => left.Equals(right);

    public static bool operator !=(VersionSearchTime left, VersionSearchTime right) => !left.Equals(right);

    // str(datetime) is isoformat(sep=" "); the separator is always the eleventh character.
    private static string PythonStr(IsoTimestamp value)
    {
        var iso = value.ToString();
        return string.Concat(iso.AsSpan(0, 10), " ", iso.AsSpan(11));
    }
}
