using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace BlenderLauncher.Core.Updates;

/// <summary>
/// Flattens GitHub release notes (Markdown) to plain text for the update dialog. Upstream renders the
/// Markdown to HTML and keeps the text nodes, dropping code blocks (<c>string_utils.py</c>); this is a small
/// line-based subset instead, enough for release notes:
/// <list type="bullet">
/// <item>Headings (ATX and setext) become their text; thematic breaks and link reference definitions go.</item>
/// <item>Bullets (<c>-</c>, <c>*</c>, <c>+</c>) become <c>•</c>, indented two spaces per level; numbered items keep
/// their number. Block quote markers go.</item>
/// <item>Links and images become their text (an autolink, its URL); emphasis, strike-through and inline-code
/// markers go; backslash escapes and HTML entities are decoded; HTML tags go (<c>&lt;br&gt;</c> breaks the line).</item>
/// <item>Fenced code blocks are kept verbatim, without the fences and untouched by the rules above.</item>
/// <item>Runs of blank lines become one, as upstream a blank line after a line ending in <c>:</c> goes, and the
/// result has no leading or trailing blank lines. Line endings become <c>\n</c>.</item>
/// </list>
/// </summary>
public static partial class MarkdownText
{
    /// <summary>The plain-text form of <paramref name="markdown"/>; empty for null or white space.</summary>
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "";
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var output = new List<string>(lines.Length);
        string? fence = null;
        var levels = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var fenceMatch = Fence().Match(line);
            if (fence is not null)
            {
                // Inside a fence: kept as written until a closing fence of the same kind and at least the same length.
                if (fenceMatch.Success && fenceMatch.Groups["info"].Value.Trim().Length == 0 &&
                    fenceMatch.Groups["fence"].Value[0] == fence[0] && fenceMatch.Groups["fence"].Value.Length >= fence.Length)
                {
                    fence = null;
                }
                else
                {
                    output.Add(line.TrimEnd());
                }

                continue;
            }

            if (fenceMatch.Success)
            {
                fence = fenceMatch.Groups["fence"].Value;
                continue;
            }

            // Setext heading underline: the previous line was text, so drop the underline.
            if (SetextUnderline().IsMatch(line) && output.Count > 0 && output[^1].Length > 0 &&
                i > 0 && lines[i - 1].Trim().Length > 0 && !IsListItem(lines[i - 1]))
            {
                continue;
            }

            if (ThematicBreak().IsMatch(line) || ReferenceDefinition().IsMatch(line))
            {
                output.Add("");
                continue;
            }

            output.Add(FlattenBlockLine(line, levels));
        }

        return Tidy(output);
    }

    private static bool IsListItem(string line) => Bullet().IsMatch(line) || Numbered().IsMatch(line);

    private static string FlattenBlockLine(string line, List<int> levels)
    {
        // Block quotes, possibly nested.
        var quote = BlockQuote().Match(line);
        if (quote.Success)
        {
            line = quote.Groups["rest"].Value;
        }

        var heading = AtxHeading().Match(line);
        if (heading.Success)
        {
            levels.Clear();
            return Inline(heading.Groups["text"].Value).Trim();
        }

        var bullet = Bullet().Match(line);
        if (bullet.Success)
        {
            return Indent(bullet.Groups["indent"].Value, levels) + "• " + Inline(bullet.Groups["text"].Value).Trim();
        }

        var numbered = Numbered().Match(line);
        if (numbered.Success)
        {
            return Indent(numbered.Groups["indent"].Value, levels) + numbered.Groups["number"].Value + ". " + Inline(numbered.Groups["text"].Value).Trim();
        }

        // A paragraph line that is not indented ends the list; an indented one continues an item.
        if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
        {
            levels.Clear();
        }

        return Inline(line).Trim();
    }

    /// <summary>
    /// Two spaces per nesting level. A list item deeper than the one before it opens a level; one at or left of an
    /// open level closes the deeper ones. So a list indented by three spaces throughout stays at the top level.
    /// </summary>
    private static string Indent(string whitespace, List<int> levels)
    {
        var columns = 0;
        foreach (var c in whitespace)
        {
            columns += c == '	' ? 4 - (columns % 4) : 1;
        }

        while (levels.Count > 0 && levels[^1] > columns)
        {
            levels.RemoveAt(levels.Count - 1);
        }

        if (levels.Count == 0 || levels[^1] < columns)
        {
            levels.Add(columns);
        }

        return new string(' ', (levels.Count - 1) * 2);
    }

    /// <summary>Inline rules on text outside code spans; code spans keep their content as written.</summary>
    private static string Inline(string text)
    {
        var sb = new StringBuilder(text.Length);
        var last = 0;
        foreach (Match code in CodeSpan().Matches(text))
        {
            sb.Append(InlineOutsideCode(text[last..code.Index]));
            sb.Append(code.Groups["code"].Value.Trim());
            last = code.Index + code.Length;
        }

        sb.Append(InlineOutsideCode(text[last..]));
        return sb.ToString();
    }

    private static string InlineOutsideCode(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        // Protect escaped characters from the rules below, then restore them as literals.
        text = Escaped().Replace(text, m => $"\u0001{(int)m.Groups[1].Value[0]}\u0002");
        text = Image().Replace(text, "${text}");
        text = Link().Replace(text, "${text}");
        text = Autolink().Replace(text, "${url}");
        text = LineBreakTag().Replace(text, "\n");
        text = HtmlTag().Replace(text, "");
        text = StrongOrStrike().Replace(text, "${text}");
        text = EmphasisStar().Replace(text, "${text}");
        text = EmphasisUnderscore().Replace(text, "${text}");
        text = WebUtility.HtmlDecode(text);
        return EscapeToken().Replace(text, m => ((char)int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToString());
    }

    private static string Tidy(List<string> lines)
    {
        var flat = string.Join('\n', lines).Split('\n');
        var result = new List<string>(flat.Length);
        foreach (var raw in flat)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 && (result.Count == 0 || result[^1].Length == 0 || result[^1].EndsWith(':')))
            {
                continue;
            }

            result.Add(line);
        }

        while (result.Count > 0 && result[^1].Length == 0)
        {
            result.RemoveAt(result.Count - 1);
        }

        return string.Join('\n', result);
    }

    [GeneratedRegex(@"^ {0,3}(?<fence>`{3,}|~{3,})(?<info>.*)$")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^ {0,3}(?:=+|-+)\s*$")]
    private static partial Regex SetextUnderline();

    [GeneratedRegex(@"^ {0,3}(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})$")]
    private static partial Regex ThematicBreak();

    [GeneratedRegex(@"^ {0,3}\[[^\]]+\]:\s*\S+.*$")]
    private static partial Regex ReferenceDefinition();

    [GeneratedRegex(@"^ {0,3}(?:>\s?)+(?<rest>.*)$")]
    private static partial Regex BlockQuote();

    [GeneratedRegex(@"^ {0,3}#{1,6}(?:[ \t]+(?<text>.*?))?(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex AtxHeading();

    [GeneratedRegex(@"^(?<indent>[ \t]*)[-*+][ \t]+(?<text>.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<number>\d{1,9})[.)][ \t]+(?<text>.*)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"(?<!`)(?<ticks>`+)(?!`)(?<code>.+?)(?<!`)\k<ticks>(?!`)")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"\\([!-/:-@\[-`{-~])")]
    private static partial Regex Escaped();

    [GeneratedRegex(@"\u0001(\d+)\u0002")]
    private static partial Regex EscapeToken();

    [GeneratedRegex(@"!\[(?<text>[^\]]*)\](?:\([^)]*\)|\[[^\]]*\])")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[(?<text>[^\]]+)\](?:\([^)]*\)|\[[^\]]*\])")]
    private static partial Regex Link();

    [GeneratedRegex(@"<(?<url>(?:https?|mailto):[^>\s]+)>")]
    private static partial Regex Autolink();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex(@"</?[A-Za-z][A-Za-z0-9-]*(?:\s[^<>]*)?/?>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"(?<mark>\*\*|__|~~)(?=\S)(?<text>.+?)(?<=\S)\k<mark>")]
    private static partial Regex StrongOrStrike();

    [GeneratedRegex(@"\*(?=\S)(?<text>.+?)(?<=\S)\*")]
    private static partial Regex EmphasisStar();

    [GeneratedRegex(@"(?<![A-Za-z0-9])_(?=\S)(?<text>.+?)(?<=\S)_(?![A-Za-z0-9])")]
    private static partial Regex EmphasisUnderscore();
}
