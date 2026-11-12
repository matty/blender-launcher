using System.Globalization;
using System.Text.RegularExpressions;
using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.Core.Scraping.PullRequests;

/// <summary>
/// Pull-request numbers in build names, and the label shown for a titled pull request.
/// </summary>
/// <remarks>
/// Matching is case-insensitive everywhere. Upstream matched <c>PR(\d+)</c> case-sensitively during the scrape
/// but <c>pr(\d+)</c> case-insensitively for "Fetch PR name", and its update detection tested <c>Pr</c>/<c>Npr</c>
/// case-sensitively; the port treats <c>PR123</c>, <c>pr123</c> and <c>Pr123</c> alike.
/// </remarks>
public static partial class PullRequestLabels
{
    /// <summary>
    /// The scrape-time rule: the prerelease of a patch build (for example <c>PR162828</c>, or
    /// <c>PR162828 | x86_64</c> from the any-architecture fallback) starts with <c>PR</c> and digits.
    /// Upstream: <c>PR_MATCH.match(prerelease)</c>.
    /// </summary>
    public static bool TryParseFromPrerelease(string? prerelease, out int number) =>
        TryNumber(prerelease is null ? null : PrAtStart().Match(prerelease), out number);

    /// <summary>
    /// The "Fetch PR name" rule: <c>pr</c> and digits anywhere in a branch, such as an installed patch build's
    /// <c>PR162828</c> or a builder branch <c>main-PR162828</c>. Upstream: <c>re.search(r"pr(\d+)", branch, re.I)</c>.
    /// </summary>
    public static bool TryParseFromBranch(string? branch, out int number) =>
        TryNumber(branch is null ? null : PrAnywhere().Match(branch), out number);

    /// <summary>
    /// The pull-request number of <paramref name="build"/>: from its prerelease (a scraped patch build), otherwise
    /// from its branch (an installed one, whose branch is <c>PRnnnnn</c>).
    /// </summary>
    public static bool TryGetNumber(BuildInfo build, out int number)
    {
        ArgumentNullException.ThrowIfNull(build);
        return TryParseFromPrerelease(build.SemVersion?.Prerelease, out number) || TryParseFromBranch(build.Branch, out number);
    }

    /// <summary>
    /// The text shown for a pull request: <c>"{number}: {title}"</c> when <paramref name="prependNumber"/> (setting
    /// <c>prepend_pr_number_on_label</c>, default true), otherwise the title alone.
    /// </summary>
    public static string Format(int number, string title, bool prependNumber)
    {
        ArgumentNullException.ThrowIfNull(title);
        return prependNumber ? string.Create(CultureInfo.InvariantCulture, $"{number}: {title}") : title;
    }

    private static bool TryNumber(Match? match, out int number)
    {
        number = 0;
        return match is { Success: true } &&
               int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number) &&
               number > 0;
    }

    [GeneratedRegex("^PR([0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrAtStart();

    [GeneratedRegex("PR([0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrAnywhere();
}
