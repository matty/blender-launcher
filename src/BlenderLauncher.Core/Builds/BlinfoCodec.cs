using System.Globalization;
using System.Text.Json;
using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.IO;
using BlenderLauncher.Core.Serialization;

namespace BlenderLauncher.Core.Builds;

/// <summary>
/// Reads and writes <c>.blinfo</c> through the source-generated context only, and converts between its entries
/// and <see cref="BuildInfo"/> as upstream's <c>from_dict</c> and <c>to_dict</c> do (<c>build_info.py:318-382</c>).
/// </summary>
public static class BlinfoCodec
{
    /// <summary>The file name inside a build folder.</summary>
    public const string FileName = ".blinfo";

    /// <summary>Upstream's <c>BuildInfo.file_version</c>. Any other value triggers re-derivation on read.</summary>
    public const string CurrentFileVersion = BuildInfo.FileVersion;

    // Python's %y maps 69-99 to the 1900s and 00-68 to the 2000s.
    private static readonly CultureInfo PythonDates = CreatePythonDates();

    // Stand-ins for dateparser, which upstream tries after the two strict formats. Each is tried as written;
    // those with a zone are converted to local time, the rest are read as local time.
    private static readonly string[] NaiveFallbackFormats =
    [
        "d-MMM-yyyy H:mm",          // download.blender.org index: 26-Mar-2024 10:57
        "d-MMM-yyyy H:mm:ss",
        "yyyy-MM-dd H:mm",
        "yyyy-MM-dd H:mm:ss",
        "yyyy-MM-dd",
        "yyyy/MM/dd H:mm",
        "yyyy/MM/dd H:mm:ss",
        "d MMM yyyy H:mm",
        "d MMM yyyy H:mm:ss",
        "MMM d, yyyy H:mm",
        "MMM d, yyyy H:mm:ss",
    ];

    private static readonly string[] ZonedFallbackFormats =
    [
        "ddd, dd MMM yyyy HH:mm:ss 'GMT'", // HTTP Last-Modified
        "yyyy-MM-dd H:mm:ss zzz",
        "yyyy-MM-dd H:mm zzz",
    ];

    /// <summary>Parses <c>.blinfo</c> JSON. Throws <see cref="JsonException"/> when it is not a JSON object of that shape.</summary>
    public static BlinfoDocument Read(string json) =>
        JsonSerializer.Deserialize(json, LauncherJsonContext.Default.BlinfoDocument)
        ?? throw new JsonException(".blinfo is the JSON literal null");

    /// <summary>Compact JSON, ASCII-only, snake_case keys and nulls kept.</summary>
    public static string Write(BlinfoDocument document) =>
        JsonSerializer.Serialize(document, LauncherJsonContext.Default.BlinfoDocument);

    /// <summary>
    /// Upstream's <c>from_dict</c>: builds a <see cref="BuildInfo"/> from an entry, with <paramref name="link"/>
    /// as its link (the folder path for an installed build). Null strings become empty, except
    /// <see cref="BlinfoEntry.CustomExecutable"/> and <see cref="BlinfoEntry.BuildHash"/>, which keep null.
    /// When <paramref name="lts"/> is given, LTS promotion is applied with it.
    /// </summary>
    public static BuildInfo ToBuildInfo(BlinfoEntry entry, string link, ILtsLookup? lts = null, TimeProvider? time = null)
    {
        var build = new BuildInfo
        {
            Link = link,
            Subversion = entry.Subversion ?? "",
            BuildHash = entry.BuildHash,
            CommitTime = ParseCommitTime(entry.CommitTime, time),
            Branch = entry.Branch ?? "",
            CustomName = entry.CustomName ?? "",
            IsFavorite = entry.IsFavorite,
            CustomExecutable = entry.CustomExecutable,
            IsFrozen = entry.IsFrozen,
        };
        return lts is null ? build : build.PromoteLts(lts);
    }

    /// <summary>Upstream's <c>to_dict</c> entry. The commit time is written by Python's <c>isoformat</c> rules.</summary>
    public static BlinfoEntry ToEntry(BuildInfo build) => new()
    {
        Branch = build.Branch,
        Subversion = build.Subversion,
        BuildHash = build.BuildHash,
        CommitTime = build.CommitTime.ToString(),
        CustomName = build.CustomName,
        IsFavorite = build.IsFavorite,
        CustomExecutable = build.CustomExecutable,
        IsFrozen = build.IsFrozen,
    };

    /// <summary>Upstream's <c>to_dict</c>: one entry and <c>file_version</c> <see cref="CurrentFileVersion"/>.</summary>
    public static BlinfoDocument ToDocument(BuildInfo build) =>
        new() { FileVersion = CurrentFileVersion, Blinfo = [ToEntry(build)] };

    /// <summary>
    /// Upstream's tolerant commit-time reading: ISO 8601 as Python's <c>fromisoformat</c> reads it (naive or
    /// aware, kept as written); then the legacy <c>%d-%b-%y-%H:%M</c> read as local time; then a small fixed
    /// set of formats in place of <c>dateparser</c>, converted to local time; then the current local time.
    /// </summary>
    public static IsoTimestamp ParseCommitTime(string? text, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        var zone = time.LocalTimeZone;
        if (IsoTimestamp.TryParse(text, out var iso))
        {
            return iso;
        }

        if (text is not null)
        {
            var trimmed = text.Trim();
            if (DateTime.TryParseExact(trimmed, "d-MMM-yy-H:mm", PythonDates, DateTimeStyles.None, out var legacy) ||
                DateTime.TryParseExact(trimmed, NaiveFallbackFormats, PythonDates, DateTimeStyles.AllowWhiteSpaces, out legacy))
            {
                return IsoTimestamp.AssumeLocal(legacy, zone);
            }

            if (DateTimeOffset.TryParseExact(trimmed, ZonedFallbackFormats, PythonDates,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var zoned))
            {
                return IsoTimestamp.Aware(TimeZoneInfo.ConvertTime(zoned, zone));
            }
        }

        return IsoTimestamp.Aware(time.GetLocalNow());
    }

    /// <summary>Reads <c>&lt;folder&gt;/.blinfo</c>. Throws <see cref="IOException"/> or <see cref="JsonException"/> on failure.</summary>
    public static async Task<BlinfoDocument> ReadFileAsync(string folder, CancellationToken ct = default) =>
        Read(await File.ReadAllTextAsync(Path.Combine(folder, FileName), ct).ConfigureAwait(false));

    /// <summary>Writes <c>&lt;folder&gt;/.blinfo</c> for <paramref name="build"/> atomically (temp file, then rename).</summary>
    public static Task WriteFileAsync(string folder, BuildInfo build, CancellationToken ct = default) =>
        AtomicFile.WriteAllTextAsync(Path.Combine(folder, FileName), Write(ToDocument(build)), ct);

    private static CultureInfo CreatePythonDates()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.DateTimeFormat.Calendar.TwoDigitYearMax = 2068;
        return CultureInfo.ReadOnly(culture);
    }
}
