using BlenderLauncher.Core.Updates;

namespace BlenderLauncher.Core.Tests.Updates;

public class MarkdownTextTests
{
    private static string Flat(string markdown) => MarkdownText.ToPlainText(markdown.ReplaceLineEndings("\n"));

    private static string Lines(params string[] lines) => string.Join('\n', lines);

    [Theory]
    [InlineData(null)]
    [InlineData("  \n\n ")]
    public void Nothing_gives_empty(string? markdown) => Assert.Equal("", MarkdownText.ToPlainText(markdown));

    [Theory]
    [InlineData("# Title", "Title")]
    [InlineData("## Closed ##", "Closed")]
    [InlineData("#NotAHeading", "#NotAHeading")]
    [InlineData("## 🛠️ Fixes:", "🛠️ Fixes:")]
    public void Atx_headings(string markdown, string expected) => Assert.Equal(expected, Flat(markdown));

    [Fact]
    public void Setext_headings_lose_their_underline()
    {
        Assert.Equal(Lines("Title", "Sub"), Flat("Title\n=====\nSub\n---"));
    }

    [Fact]
    public void Bullets_become_dots_and_nest_by_indentation()
    {
        var markdown = """
            - one
            * two
              + nested
                - deeper
            - three
            """;

        Assert.Equal(Lines("• one", "• two", "  • nested", "    • deeper", "• three"), Flat(markdown));
    }

    [Fact]
    public void A_list_indented_throughout_stays_at_the_top_level()
    {
        // As in upstream's v2.6.1 notes.
        Assert.Equal(Lines("• a", "• b", "  • c"), Flat("   - a\n   - b\n      - c"));
    }

    [Fact]
    public void Numbered_items_keep_their_number()
    {
        Assert.Equal(Lines("1. first", "2. second", "  1. inner"), Flat("1. first\n2) second\n   1. inner"));
    }

    [Theory]
    [InlineData("See [the docs](https://example.org/docs \"t\").", "See the docs.")]
    [InlineData("Ref [link][1] here", "Ref link here")]
    [InlineData("Go to <https://example.org/x>", "Go to https://example.org/x")]
    [InlineData("**bold** and __bold__ and *em* and _em_ and ~~gone~~", "bold and bold and em and em and gone")]
    [InlineData("snake_case_name stays", "snake_case_name stays")]
    [InlineData("2 * 3 * 4", "2 * 3 * 4")]
    [InlineData(@"\*not em\* and \[x\]", "*not em* and [x]")]
    [InlineData("Fish &amp; chips &lt;3", "Fish & chips <3")]
    [InlineData("a<br>b", "a\nb")]
    [InlineData("<details><summary>More</summary>text</details>", "Moretext")]
    [InlineData("> quoted **text**", "quoted text")]
    public void Inline_markup_becomes_text(string markdown, string expected) => Assert.Equal(expected, Flat(markdown));

    [Fact]
    public void Inline_code_keeps_its_content_untouched()
    {
        Assert.Equal("Run --dev-scrape with *stars* and [x](y)", Flat("Run `--dev-scrape` with ``*stars* and [x](y)``"));
    }

    [Fact]
    public void Code_fences_are_kept_verbatim()
    {
        var markdown = """
            Before:
            ```powershell
            # not a heading
            - not a bullet
                **kept**
            ```
            After
            """;

        Assert.Equal(Lines("Before:", "# not a heading", "- not a bullet", "    **kept**", "After"), Flat(markdown));
    }

    [Fact]
    public void An_unclosed_fence_runs_to_the_end()
    {
        Assert.Equal(Lines("x", "# y"), Flat("x\n~~~\n# y"));
    }

    [Fact]
    public void Blank_lines_collapse_and_go_after_a_colon()
    {
        var markdown = "\n\n# What's Changed\n\n\n## Fixes:\n\n- a\n\n\n---\n\n[1]: https://example.org\nEnd\n\n";

        Assert.Equal(Lines("What's Changed", "", "Fixes:", "• a", "", "End"), Flat(markdown));
    }

    [Fact]
    public void Windows_line_endings_are_handled()
    {
        Assert.Equal(Lines("Title", "", "• a", "x"), MarkdownText.ToPlainText("# Title\r\n\r\n- a\r\nx\r"));
    }

    [Fact]
    public void Upstreams_v270_notes_flatten_cleanly()
    {
        var markdown = """
            # What's Changed

            ## 💡 Features:

            - Search bar for builds with version/date range filters and fuzzy matching
            - Chinese localization support

            ## ⚙️ Others:

            - Dependency updates
            """;

        Assert.Equal(
            Lines(
                "What's Changed",
                "",
                "💡 Features:",
                "• Search bar for builds with version/date range filters and fuzzy matching",
                "• Chinese localization support",
                "",
                "⚙️ Others:",
                "• Dependency updates"),
            Flat(markdown));
    }
}
