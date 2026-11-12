using System.Text.RegularExpressions;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.Core.Localisation;

namespace BlenderLauncher.App.Tests.Shell;

/// <summary>Every translation key the App uses exists in English, so no view shows a raw key.</summary>
public partial class LocalisationKeysTests
{
    [Fact]
    public void Every_key_used_by_the_app_exists_in_English()
    {
        var english = new Translator("en");
        var appSource = Path.Combine(RepositoryRoot(), "src", "BlenderLauncher.App");
        var missing = new List<string>();
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(appSource, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".axaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
                                 && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            // A key ending in "." is a prefix completed at run time ("ui.channel." + key); not checkable here.
            foreach (var key in XamlKey().Matches(text).Concat(CodeKey().Matches(text)).Select(m => m.Groups["key"].Value)
                         .Where(k => !k.EndsWith('.')))
            {
                count++;
                if (!english.HasKey(key, "en"))
                {
                    missing.Add($"{Path.GetRelativePath(appSource, file)}: {key}");
                }
            }
        }

        Assert.True(count > 20, $"only {count} keys found; is the scan broken?");
        Assert.Empty(missing);
    }

    [Fact]
    public void Tr_extension_and_helper_translate()
    {
        Assert.Equal("Check for builds", new TrExtension("ui.list.check_for_builds").ProvideValue(null!));
        Assert.Equal("2 instances running", Loc.Plural("ui.detail.instances_running", 2));
        Assert.Equal("Launch 5.2.2", Loc.Tr("ui.detail.launch_version", ("version", "5.2.2")));
        Assert.Equal("Launch", Loc.Tr("act.launch"));
        Assert.Equal("ui.no.such.key", Loc.Tr("ui.no.such.key"));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BlenderLauncher.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    // {loc:Tr ui.sidebar.library}, {loc:Tip settings.x_tooltip} and {loc:Tr Key=...}
    [GeneratedRegex(@"\{loc:(?:Tr|Tip)\s+(?:Key=)?(?<key>[a-z_]+\.[A-Za-z0-9_.\-]+)\s*\}")]
    private static partial Regex XamlKey();

    // Loc.Tr("ui.x.y" / Loc.Plural("ui.x.y"
    [GeneratedRegex(@"Loc\.(?:Tr|Plural)\(""(?<key>[a-z_]+\.[A-Za-z0-9_.\-]+)""")]
    private static partial Regex CodeKey();
}
