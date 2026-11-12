using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BlenderLauncher.Core.Builds;

/// <summary>
/// A date and time as Python's <c>datetime</c> holds it: a wall-clock value that is either naive (no offset) or
/// aware (with a UTC offset). Upstream's files mix both (stable cache folder dates are naive,
/// commit times carry an offset), and a round trip must keep each as written.
/// </summary>
/// <remarks>
/// Equality follows Python: two aware values are equal when they are the same instant, two naive values when
/// their clocks match, and a naive value never equals an aware one. Ordering a naive value against an aware one
/// throws, as Python's <c>TypeError</c> does; use <see cref="TryCompareTo"/> to test for that.
/// Precision is one microsecond, as in Python.
/// </remarks>
public readonly partial struct IsoTimestamp : IEquatable<IsoTimestamp>
{
    private IsoTimestamp(DateTime clock, TimeSpan? offset)
    {
        Clock = DateTime.SpecifyKind(new DateTime(clock.Ticks - clock.Ticks % 10), DateTimeKind.Unspecified);
        Offset = offset;
    }

    /// <summary>The wall-clock date and time (kind <see cref="DateTimeKind.Unspecified"/>).</summary>
    public DateTime Clock { get; }

    /// <summary>The UTC offset, or null for a naive value.</summary>
    public TimeSpan? Offset { get; }

    /// <summary>True when there is no offset.</summary>
    public bool IsNaive => Offset is null;

    /// <summary>Upstream's <c>EPOCH</c>: 1970-01-01T00:00:00+00:00.</summary>
    public static IsoTimestamp Epoch { get; } = new(DateTime.UnixEpoch, TimeSpan.Zero);

    /// <summary>A naive value with this wall clock (the <see cref="DateTime.Kind"/> is ignored).</summary>
    public static IsoTimestamp Naive(DateTime clock) => new(clock, null);

    /// <summary>An aware value keeping the offset of <paramref name="value"/>.</summary>
    public static IsoTimestamp Aware(DateTimeOffset value) => new(value.DateTime, value.Offset);

    /// <summary>An aware value with this wall clock and offset. Offsets must be under a day, as in Python.</summary>
    public static IsoTimestamp Aware(DateTime clock, TimeSpan offset)
    {
        if (offset.Duration() >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "UTC offset must be strictly between -24h and 24h.");
        }

        return new IsoTimestamp(clock, offset);
    }

    public static implicit operator IsoTimestamp(DateTimeOffset value) => Aware(value);

    /// <summary>
    /// Python's <c>astimezone()</c> on a naive value: the clock is read as local time in <paramref name="zone"/>
    /// and the result carries that zone's offset at that time.
    /// </summary>
    public static IsoTimestamp AssumeLocal(DateTime clock, TimeZoneInfo zone) =>
        new(clock, zone.GetUtcOffset(DateTime.SpecifyKind(clock, DateTimeKind.Unspecified)));

    /// <summary>
    /// Python's <c>astimezone(zone)</c>: an aware value converted to the offset of <paramref name="zone"/> at that
    /// instant; a naive value is read as local time in <paramref name="zone"/>.
    /// </summary>
    public IsoTimestamp ToZone(TimeZoneInfo zone) =>
        Offset is null ? AssumeLocal(Clock, zone) : Aware(TimeZoneInfo.ConvertTime(ToDateTimeOffset(zone), zone));

    /// <summary>
    /// As a <see cref="DateTimeOffset"/>. A naive value is read as local time in <paramref name="zoneForNaive"/>
    /// (the machine's zone when null), as Python's <c>astimezone()</c> does. An offset that
    /// <see cref="DateTimeOffset"/> cannot hold (seconds, or beyond 14 hours) is converted to UTC.
    /// </summary>
    public DateTimeOffset ToDateTimeOffset(TimeZoneInfo? zoneForNaive = null)
    {
        if (Offset is not { } offset)
        {
            return new DateTimeOffset(Clock, (zoneForNaive ?? TimeZoneInfo.Local).GetUtcOffset(Clock));
        }

        if (offset.Ticks % TimeSpan.TicksPerMinute == 0 && offset.Duration() <= TimeSpan.FromHours(14))
        {
            return new DateTimeOffset(Clock, offset);
        }

        return new DateTimeOffset(DateTime.SpecifyKind(Clock - offset, DateTimeKind.Utc));
    }

    /// <summary>
    /// Python's <c>datetime.fromisoformat</c> for the forms upstream's files use: <c>YYYY-MM-DD</c>, optionally
    /// followed by <c>T</c> or a space and <c>HH[:MM[:SS[.f…]]]</c>, then optionally <c>Z</c>,
    /// <c>±HH:MM[:SS[.ffffff]]</c> or <c>±HHMM</c>. Fractions longer than six digits are truncated, as in Python.
    /// Week dates and the basic (no separator) date form are not accepted.
    /// </summary>
    public static bool TryParse(string? text, out IsoTimestamp value)
    {
        value = default;
        if (text is null)
        {
            return false;
        }

        var m = IsoRegex().Match(text);
        if (!m.Success)
        {
            return false;
        }

        try
        {
            var clock = new DateTime(Int(m, "y"), Int(m, "mo"), Int(m, "d"), Int(m, "h"), Int(m, "mi"), Int(m, "s"))
                .AddTicks(FractionTicks(m.Groups["f"]));
            TimeSpan? offset = null;
            if (m.Groups["z"].Success)
            {
                offset = TimeSpan.Zero;
            }
            else if (m.Groups["oh"].Success)
            {
                var o = new TimeSpan(Int(m, "oh"), Int(m, "om"), Int(m, "os")) + TimeSpan.FromTicks(FractionTicks(m.Groups["of"]));
                if (Int(m, "om") > 59 || Int(m, "os") > 59 || o >= TimeSpan.FromDays(1))
                {
                    return false;
                }

                offset = m.Groups["sign"].Value == "-" ? -o : o;
            }

            value = new IsoTimestamp(clock, offset);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Throwing <see cref="TryParse"/>.</summary>
    public static IsoTimestamp Parse(string text) =>
        TryParse(text, out var value) ? value : throw new FormatException($"Invalid isoformat string: '{text}'");

    /// <summary>
    /// Python's <c>isoformat()</c>: <c>YYYY-MM-DDTHH:MM:SS</c>, <c>.ffffff</c> when there are microseconds, then
    /// the offset as <c>±HH:MM</c> (with <c>:SS</c> and <c>.ffffff</c> when needed) for an aware value.
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder(32);
        sb.Append(Clock.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture));
        var micro = Clock.Ticks % TimeSpan.TicksPerSecond / 10;
        if (micro != 0)
        {
            sb.Append('.').Append(micro.ToString("D6", CultureInfo.InvariantCulture));
        }

        if (Offset is { } offset)
        {
            var abs = offset.Duration();
            sb.Append(offset < TimeSpan.Zero ? '-' : '+')
              .Append(((int)abs.TotalHours).ToString("D2", CultureInfo.InvariantCulture))
              .Append(':')
              .Append(abs.Minutes.ToString("D2", CultureInfo.InvariantCulture));
            var offsetMicro = abs.Ticks % TimeSpan.TicksPerSecond / 10;
            if (abs.Seconds != 0 || offsetMicro != 0)
            {
                sb.Append(':').Append(abs.Seconds.ToString("D2", CultureInfo.InvariantCulture));
                if (offsetMicro != 0)
                {
                    sb.Append('.').Append(offsetMicro.ToString("D6", CultureInfo.InvariantCulture));
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Compares as Python does. Returns false (and 0) when one value is naive and the other aware, where Python
    /// raises <c>TypeError</c>.
    /// </summary>
    public bool TryCompareTo(IsoTimestamp other, out int result)
    {
        result = 0;
        if (IsNaive != other.IsNaive)
        {
            return false;
        }

        result = Math.Sign(ComparableTicks.CompareTo(other.ComparableTicks));
        return true;
    }

    /// <summary>Compares as Python does; throws <see cref="InvalidOperationException"/> for naive against aware.</summary>
    public int CompareTo(IsoTimestamp other) =>
        TryCompareTo(other, out var result)
            ? result
            : throw new InvalidOperationException("can't compare offset-naive and offset-aware datetimes");

    public bool Equals(IsoTimestamp other) => IsNaive == other.IsNaive && ComparableTicks == other.ComparableTicks;

    public override bool Equals(object? obj) => obj is IsoTimestamp other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(IsNaive, ComparableTicks);

    public static bool operator ==(IsoTimestamp left, IsoTimestamp right) => left.Equals(right);

    public static bool operator !=(IsoTimestamp left, IsoTimestamp right) => !left.Equals(right);

    public static bool operator <(IsoTimestamp left, IsoTimestamp right) => left.CompareTo(right) < 0;

    public static bool operator >(IsoTimestamp left, IsoTimestamp right) => left.CompareTo(right) > 0;

    public static bool operator <=(IsoTimestamp left, IsoTimestamp right) => left.CompareTo(right) <= 0;

    public static bool operator >=(IsoTimestamp left, IsoTimestamp right) => left.CompareTo(right) >= 0;

    // The clock for naive values, the UTC instant for aware ones.
    private long ComparableTicks => Clock.Ticks - (Offset?.Ticks ?? 0);

    private static int Int(Match m, string group) =>
        m.Groups[group].Success ? int.Parse(m.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) : 0;

    // Microseconds from a fraction of any length: the first six digits, right-padded.
    private static long FractionTicks(Group g)
    {
        if (!g.Success)
        {
            return 0;
        }

        var digits = g.Value.Length > 6 ? g.Value[..6] : g.Value.PadRight(6, '0');
        return long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture) * 10;
    }

    [GeneratedRegex(
        @"^(?<y>[0-9]{4})-(?<mo>[0-9]{2})-(?<d>[0-9]{2})" +
        @"(?:[Tt ](?<h>[0-9]{2})(?::(?<mi>[0-9]{2})(?::(?<s>[0-9]{2})(?:[.,](?<f>[0-9]+))?)?)?" +
        @"(?:(?<z>Z)|(?<sign>[+-])(?<oh>[0-9]{2})(?::(?<om>[0-9]{2})(?::(?<os>[0-9]{2})(?:\.(?<of>[0-9]{1,6}))?)?|(?<om>[0-9]{2})))?)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex IsoRegex();
}
