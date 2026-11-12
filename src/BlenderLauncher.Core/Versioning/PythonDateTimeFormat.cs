using System.Globalization;

namespace BlenderLauncher.Core.Versioning;

/// <summary>Formats dates the way Python's <c>str(datetime)</c> does for an aware datetime.</summary>
public static class PythonDateTimeFormat
{
    /// <summary>
    /// <c>str(datetime)</c> of an aware datetime: <c>2024-07-16 00:00:00+00:00</c>. Microseconds appear as
    /// <c>.ffffff</c> only when non-zero; sub-microsecond ticks are dropped, as Python has no finer unit.
    /// The offset is <c>+HH:MM</c> (a <see cref="DateTimeOffset"/> offset is always whole minutes, so Python's
    /// <c>:SS</c> suffix never applies). The value is printed in its own offset, not converted.
    /// </summary>
    public static string Format(DateTimeOffset value)
    {
        var micro = (int)(value.Ticks % TimeSpan.TicksPerSecond / TimeSpan.TicksPerMicrosecond);
        var offset = value.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();

        return micro == 0
            ? string.Create(CultureInfo.InvariantCulture,
                $"{value:yyyy'-'MM'-'dd' 'HH':'mm':'ss}{sign}{offset.Hours:D2}:{offset.Minutes:D2}")
            : string.Create(CultureInfo.InvariantCulture,
                $"{value:yyyy'-'MM'-'dd' 'HH':'mm':'ss}.{micro:D6}{sign}{offset.Hours:D2}:{offset.Minutes:D2}");
    }
}
