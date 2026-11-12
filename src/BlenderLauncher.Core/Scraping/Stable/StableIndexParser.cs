using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace BlenderLauncher.Core.Scraping.Stable;

/// <summary>A <c>BlenderX.Y/</c> folder on the download.blender.org release index.</summary>
/// <param name="Href">The anchor's <c>href</c>, for example <c>Blender4.1/</c>.</param>
/// <param name="Version">The <c>X.Y</c> captured from the href.</param>
/// <param name="Modified">The first two whitespace tokens of the text after the anchor, for example <c>26-Mar-2024 10:57</c>; null when there is none.</param>
public sealed record StableIndexFolder(string Href, string Version, string? Modified);

/// <summary>A file in a release folder's listing.</summary>
/// <param name="Href">The anchor's <c>href</c>, for example <c>blender-4.2.3-windows-x64.zip</c>.</param>
/// <param name="Modified">The listing's date and time for the file (<c>dd-MMM-yyyy HH:mm</c>, GMT), or null.</param>
/// <param name="SizeBytes">The listing's size column in bytes, or null when it is missing or not a plain number.</param>
public sealed record StableFolderFile(string Href, DateTime? Modified, long? SizeBytes);

/// <summary>
/// Parses the Apache-style (nginx autoindex) pages at <c>https://download.blender.org/release/</c>:
/// the index of release folders and each folder's file listing. A listing line looks like
/// <c>&lt;a href="blender-5.3.0-windows-x64.zip"&gt;…&lt;/a&gt;   10-Nov-2026 08:58   425299259</c>.
/// </summary>
public static partial class StableIndexParser
{
    private static readonly string[] ListingDateFormats = ["dd-MMM-yyyy HH:mm", "d-MMM-yyyy HH:mm"];

    /// <summary>Every anchor whose href contains <c>Blender&lt;digits&gt;.&lt;digits&gt;</c>, in page order.</summary>
    public static IReadOnlyList<StableIndexFolder> ParseIndex(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        using var document = new HtmlParser().ParseDocument(html);
        var folders = new List<StableIndexFolder>();
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href")!;
            var match = BlenderFolder().Match(href);
            if (!match.Success)
            {
                continue;
            }

            var tokens = Tokens(anchor.NextSibling);
            folders.Add(new StableIndexFolder(href, match.Groups[1].Value, tokens.Length >= 2 ? $"{tokens[0]} {tokens[1]}" : null));
        }

        return folders;
    }

    /// <summary>
    /// The anchors of a release folder page whose href passes <paramref name="fileFilter"/> (upstream's
    /// <c>find_all(href=link_filter)</c>, a regex search), with the date and size from the text after each anchor.
    /// When that text has no date, the date is looked for on the page line holding the anchor, as upstream does.
    /// </summary>
    public static IReadOnlyList<StableFolderFile> ParseFolder(string html, Regex fileFilter)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(fileFilter);
        using var document = new HtmlParser().ParseDocument(html);
        var files = new List<StableFolderFile>();
        string[]? lines = null;
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href")!;
            if (!fileFilter.IsMatch(href))
            {
                continue;
            }

            var tokens = Tokens(anchor.NextSibling);
            DateTime? modified = tokens.Length >= 2 && TryParseListingDate($"{tokens[0]} {tokens[1]}", out var date) ? date : null;
            long? size = tokens.Length >= 3 &&
                         long.TryParse(tokens[2], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ? bytes : null;
            if (modified is null)
            {
                lines ??= html.Split('\n');
                modified = FindDateOnLine(lines, href);
            }

            files.Add(new StableFolderFile(href, modified, size));
        }

        return files;
    }

    /// <summary>
    /// Parses a listing date such as <c>26-Mar-2024 10:57</c> (upstream used <c>dateparser</c>; the listings only
    /// ever use this form). The result has kind <see cref="DateTimeKind.Unspecified"/>.
    /// </summary>
    public static bool TryParseListingDate(string? text, out DateTime value) =>
        DateTime.TryParseExact(text?.Trim(), ListingDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    // Upstream's fallback (stable.py:271-283): the first line containing href="…<href>" and a date. Upstream kept
    // scanning and used the last match; the listing has one line per href, so the first is the same line.
    private static DateTime? FindDateOnLine(string[] lines, string href)
    {
        var quoted = href + "\"";
        foreach (var line in lines)
        {
            var at = line.IndexOf(quoted, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            // The href must sit inside an href="…" attribute with no quote between the attribute start and it.
            var attribute = line.LastIndexOf("href=\"", at, StringComparison.Ordinal);
            if (attribute < 0 || line.IndexOf('"', attribute + 6) < at + href.Length)
            {
                continue;
            }

            var match = ListingDate().Match(line);
            if (match.Success && TryParseListingDate($"{match.Groups[1].Value} {match.Groups[2].Value}", out var date))
            {
                return date;
            }
        }

        return null;
    }

    private static string[] Tokens(INode? sibling) =>
        sibling is IText text ? text.Data.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) : [];

    // Upstream: re.compile(r"Blender(\d+\.\d+)") applied with search to the href.
    [GeneratedRegex(@"Blender(\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex BlenderFolder();

    // Upstream: r"(\d{1,2}-\w{3}-\d{4})\s+(\d{2}:\d{2})".
    [GeneratedRegex(@"(\d{1,2}-\w{3}-\d{4})\s+(\d{2}:\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex ListingDate();
}
