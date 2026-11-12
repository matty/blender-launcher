using AngleSharp.Html.Parser;

namespace BlenderLauncher.Core.Scraping;

/// <summary>One <c>&lt;details&gt;</c> section of a Bforartists index page.</summary>
/// <param name="Title">The summary text with whitespace collapsed (upstream <c>get_text(" ", strip=True)</c>).</param>
/// <param name="Links">Absolute https links on the index's own host, de-duplicated across the whole page.</param>
public sealed record BfaSection(string Title, IReadOnlyList<Uri> Links);

/// <summary>
/// Parses <c>https://downloads.bforartists.com/</c> and <c>https://devbuilds.bforartists.com/</c>.
/// Version and date extraction from the title is left to the scraper.
/// </summary>
public static class BfaIndexParser
{
    public static IReadOnlyList<BfaSection> Parse(string html, Uri indexUrl)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var seen = new HashSet<Uri>();
        var sections = new List<BfaSection>();
        foreach (var details in document.QuerySelectorAll("details"))
        {
            var summary = details.QuerySelector("summary");
            var title = summary is null ? "" : CollapseWhitespace(summary.TextContent);
            var links = new List<Uri>();
            foreach (var anchor in details.QuerySelectorAll("a[href]"))
            {
                if (!Uri.TryCreate(indexUrl, anchor.GetAttribute("href"), out var link) ||
                    link.Scheme != Uri.UriSchemeHttps ||
                    !string.Equals(link.Authority, indexUrl.Authority, StringComparison.OrdinalIgnoreCase) ||
                    !seen.Add(link))
                {
                    continue;
                }

                links.Add(link);
            }

            sections.Add(new BfaSection(title, links));
        }

        return sections;
    }

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
