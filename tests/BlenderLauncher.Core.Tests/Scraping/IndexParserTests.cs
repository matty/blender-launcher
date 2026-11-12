using BlenderLauncher.Core.Scraping;
using BlenderLauncher.Core.Scraping.Stable;

namespace BlenderLauncher.Core.Tests.Scraping;

/// <summary>
/// Real pages saved on 11 Nov 2026 (Fixtures/download-blender-org-release-index.html, bforartists-*.html) and
/// the inline pages from upstream's tests/backend/test_bfa_scraper.py.
/// </summary>
public class IndexParserTests
{
    private static readonly Uri BfaDownloads = new("https://downloads.bforartists.com/");
    private static readonly Uri BfaDevBuilds = new("https://devbuilds.bforartists.com/");

    [Fact]
    public void Saved_blender_release_index_lists_every_folder_with_its_date()
    {
        var folders = StableIndexParser.ParseIndex(Fixtures.ReadText("download-blender-org-release-index.html"));

        Assert.Equal(90, folders.Count);
        Assert.Contains(new StableIndexFolder("Blender4.1/", "4.1", "16-Apr-2024 08:42"), folders);
        Assert.Contains(new StableIndexFolder("Blender1.0/", "1.0", "11-Jul-2020 07:17"), folders);
        Assert.All(folders, f => Assert.NotNull(f.Modified));
    }

    [Fact]
    public void Saved_bforartists_stable_page_has_sections_with_windows_zips()
    {
        var sections = BfaIndexParser.Parse(Fixtures.ReadText("bforartists-downloads.html"), BfaDownloads);

        Assert.True(sections.Count > 40, $"only {sections.Count} sections");
        var latest = Assert.Single(sections, s => s.Title == "Bforartists 5.2.3" || s.Title.StartsWith("Bforartists 5.2.3 ", StringComparison.Ordinal));
        Assert.Contains(new Uri("https://downloads.bforartists.com/Bforartists-5.2.3-Windows.zip"), latest.Links);
        Assert.All(sections.SelectMany(s => s.Links), l => Assert.Equal("downloads.bforartists.com", l.Host));
    }

    [Fact]
    public void Saved_bforartists_dev_page_has_dated_sections()
    {
        var sections = BfaIndexParser.Parse(Fixtures.ReadText("bforartists-devbuilds.html"), BfaDevBuilds);

        var section = Assert.Single(sections, s => s.Title.StartsWith("2026-11-10", StringComparison.Ordinal));
        Assert.Contains(new Uri("https://devbuilds.bforartists.com/builds/windows-devbuild-10-11-2026.zip"), section.Links);
    }

    [Fact]
    public void Upstream_inline_stable_page_keeps_own_https_links_once()
    {
        const string html = """
            <details><summary>Bforartists 5.2.0 (5 files)</summary>
            <a href="Bforartists-5.2.0-Windows.zip">Windows</a>
            <a href="Bforartists-5.2.0-Windows.zip">Duplicate</a>
            <a href="Bforartists-5.2.0-Mac-Intel.dmg">Intel</a>
            <a href="Bforartists-5.2.0-Mac-Silicon.dmg">Silicon</a>
            <a href="Bforartists-5.2.0-Linux.tar.xz">Linux</a>
            <a href="Install.exe">Installer</a>
            <a href="https://example.com/unwanted-Windows.zip">External</a>
            </details>
            """;

        var section = Assert.Single(BfaIndexParser.Parse(html, BfaDownloads));

        Assert.Equal("Bforartists 5.2.0 (5 files)", section.Title);
        Assert.Equal(5, section.Links.Count);
        Assert.Equal(new Uri("https://downloads.bforartists.com/Bforartists-5.2.0-Windows.zip"), section.Links[0]);
    }

    [Fact]
    public void Upstream_inline_dev_page_collapses_summary_text()
    {
        const string html = """
            <details><summary>2026-10-27 <span>2 files</span></summary>
            <a href="builds/windows-devbuild-27-10-2026.zip">Windows</a>
            <a href="builds/linux-devbuild-27-10-2026.tar.xz">Linux</a>
            </details><details><summary>2026-99-99</summary><a href="bad.zip">Bad</a></details>
            """;

        var sections = BfaIndexParser.Parse(html, BfaDevBuilds);

        Assert.Equal(["2026-10-27 2 files", "2026-99-99"], sections.Select(s => s.Title));
        Assert.Equal(2, sections[0].Links.Count);
    }
}
