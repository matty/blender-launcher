using System.Globalization;
using System.Text;
using BlenderLauncher.Core.Localisation;

namespace BlenderLauncher.Core.Tests.Localisation;

public class TranslatorTests(ITestOutputHelper output)
{
    private static readonly Translator En = new("en");

    // --- lookup over the embedded upstream files ---

    [Fact]
    public void Looks_up_dotted_keys()
    {
        Assert.Equal("Library", En["act.tabs.library"]);
        Assert.Equal("Yes", En["act.yes"]);
        Assert.Equal(" Previous", En["act.lprev"]);
        Assert.Equal(
            "Check for new builds online\n(Hold SHIFT to force check stable and automated builds)\n",
            En["act.a.check_tooltip"]);
    }

    [Fact]
    public void Other_languages_translate()
    {
        var fr = new Translator("fr");
        Assert.True(fr.TryGet("act.tabs.library", out var text));
        Assert.NotEqual(En["act.tabs.library"], text);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void Fills_placeholders()
    {
        Assert.Equal(
            "Blender: 12.5 of 300 MB",
            En.Get("act.prog.progress", ("title", "Blender"), ("progress", 12.5), ("total", 300)));
    }

    [Fact]
    public void Leaves_unknown_placeholders_as_written() =>
        Assert.Equal("Blender: %{progress} of %{total} MB", En.Get("act.prog.progress", ("title", "Blender")));

    // --- plurals ---

    [Fact]
    public void Plural_one_and_many()
    {
        Assert.Equal("Are you sure you want to\ndelete this build?\n", En.GetPlural("msg.popup.ask_remove_from_drive", 1));
        Assert.Equal("Are you sure you want to\ndelete 5 builds?\n", En.GetPlural("msg.popup.ask_remove_from_drive", 5));
        Assert.Equal("Are you sure you want to\nsend 2 builds to the trash?\n", En.Get("msg.popup.ask_send_to_trash", ("count", 2)));
    }

    [Fact]
    public void Plural_without_zero_or_few_uses_many()
    {
        // count 0 wants "zero", a negative count wants "few" (plural_few = 1); neither exists, so "many".
        Assert.Equal("Are you sure you want to\ndelete 0 builds?\n", En.GetPlural("msg.popup.ask_remove_from_drive", 0));
        Assert.Equal("Are you sure you want to\ndelete -1 builds?\n", En.GetPlural("msg.popup.ask_remove_from_drive", -1));
    }

    [Fact]
    public void Plural_rules_follow_python_i18n()
    {
        var t = FromYaml("""
            all:
              zero: none
              one: one %{count}
              few: few %{count}
              many: many %{count}
            onlyone:
              one: just one
              zero: nothing
            """);
        Assert.Equal("none", t.GetPlural("ns.all", 0));
        Assert.Equal("one 1", t.GetPlural("ns.all", 1));
        Assert.Equal("few -3", t.GetPlural("ns.all", -3));
        Assert.Equal("many 2", t.GetPlural("ns.all", 2));
        Assert.Equal("many 1.5", t.Get("ns.all", ("count", 1.5)));

        // No "many": the key comes back.
        Assert.Equal("just one", t.GetPlural("ns.onlyone", 1));
        Assert.Equal("ns.onlyone", t.GetPlural("ns.onlyone", 7));
    }

    [Fact]
    public void Count_on_a_plain_string_only_fills_the_placeholder()
    {
        var t = FromYaml("found: \"Found %{count} builds\"");
        Assert.Equal("Found 3 builds", t.GetPlural("ns.found", 3));
        Assert.Equal("Found 1 builds", t.GetPlural("ns.found", 1));
    }

    [Fact]
    public void Plural_node_without_count_is_missing()
    {
        Assert.False(En.TryGet("msg.popup.ask_remove_from_drive", out var value));
        Assert.Equal("msg.popup.ask_remove_from_drive", value);
    }

    [Fact]
    public void Placeholder_syntax_matches_python_template()
    {
        (string, object?)[] args = [("a", 1), ("b_2", "x"), ("flag", true), ("none", null)];
        Assert.Equal("1 x 1x %b %{c} 100% True None %", Translator.Format("%{a} %{b_2} %a%b_2 %%b %{c} 100% %flag %{none} %", args));
    }

    // --- fallback and missing keys ---

    [Fact]
    public void Spanish_falls_back_to_english_per_key()
    {
        var es = new Translator("es");
        Assert.True(es.HasFile("act", "es"));
        Assert.False(es.HasFile("msg", "es"));

        // Only act.es.yml exists, so every msg key comes from English.
        Assert.True(es.TryGet("msg.popup.ask_send_to_trash", out var trash, ("count", 1)));
        Assert.Equal(En.GetPlural("msg.popup.ask_send_to_trash", 1), trash);

        // A key present in act.en.yml but not act.es.yml also comes from English.
        var missingInEs = new Translator("en").KeysOf("act", "en").First(k => !es.HasKey("act." + k, "es"));
        Assert.Equal(En["act." + missingInEs], es["act." + missingInEs]);
    }

    [Fact]
    public void Missing_key_returns_the_key()
    {
        var fr = new Translator("fr");
        Assert.Equal("settings.nope.label_tooltip", fr["settings.nope.label_tooltip"]);
        Assert.False(fr.TryGet("settings.nope.label_tooltip", out var value));
        Assert.Equal("settings.nope.label_tooltip", value);
        Assert.Equal("nonamespace", fr["nonamespace"]);
        Assert.Equal("unknown_ns.key", fr["unknown_ns.key"]);
        Assert.Equal("act.", fr["act."]);
        Assert.Equal("../act.yes", fr["../act.yes"]);
    }

    [Fact]
    public void Missing_key_is_not_formatted() =>
        Assert.Equal("act.missing %{x}", En.Get("act.missing %{x}", ("x", 1)));

    [Fact]
    public void Ui_namespace_is_embedded_and_other_languages_fall_back()
    {
        Assert.Equal("Blender Launcher", En["ui.window.title"]);
        Assert.Equal("Blender Launcher", new Translator("ja")["ui.window.title"]);
        Assert.Equal("2 builds installed", En.GetPlural("ui.status.builds_installed", 2));
        Assert.Equal("1 build installed", En.GetPlural("ui.status.builds_installed", 1));
    }

    // --- language resolution ---

    [Theory]
    [InlineData("fr", "en-GB", "fr")]
    [InlineData("AUTO", "ja-JP", "ja")]
    [InlineData(null, "es-ES", "es")]
    [InlineData("auto", "zh-Hans-CN", "zh")]
    [InlineData("auto", "de-DE", "en")]
    [InlineData("auto", "", "en")] // invariant culture
    [InlineData("zh_CN", "en-GB", "zh")]
    [InlineData("Chinese_Simplified", "en-GB", "zh")]
    [InlineData("FR", "en-GB", "fr")]
    public void Resolves_language(string? setting, string culture, string expected) =>
        Assert.Equal(expected, Translator.ResolveLanguage(setting, CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void Create_uses_the_resolved_language() =>
        Assert.Equal("fr", Translator.Create("auto", CultureInfo.GetCultureInfo("fr-CA")).Language);

    // --- every file loads ---

    [Fact]
    public void All_five_languages_load_every_namespace_present()
    {
        var expected = new Dictionary<string, string[]>
        {
            ["en"] = ["act", "msg", "repo", "settings", "wizard", "launching", "custom_build", "ui"],
            ["es"] = ["act"],
            ["fr"] = ["act", "msg", "repo", "settings", "wizard", "launching", "custom_build"],
            ["ja"] = ["act", "msg", "repo", "settings", "wizard", "launching", "custom_build"],
            ["zh"] = ["act", "msg", "repo", "settings", "wizard", "launching", "custom_build"],
        };
        Assert.Equal(expected.Keys.Order(), Translator.SupportedLanguages.Order());

        foreach (var (language, namespaces) in expected)
        {
            var t = new Translator(language);
            var present = Translator.Namespaces.Where(ns => t.HasFile(ns, language)).ToArray();
            Assert.Equal(namespaces.Order(), present.Order());
            foreach (var ns in present)
            {
                Assert.NotEmpty(t.KeysOf(ns, language));
            }
        }

        // Upstream's English leaf counts (counted per leaf line, so one/many count twice).
        Assert.Equal(98, En.KeysOf("act", "en").Count);
        Assert.Equal(56, En.KeysOf("msg", "en").Count);
    }

    [Fact]
    public void Lookups_are_thread_safe()
    {
        var t = new Translator("ja");
        var keys = Translator.Namespaces.SelectMany(ns => En.KeysOf(ns, "en").Select(k => ns + "." + k)).ToArray();
        var results = new string[keys.Length];
        Parallel.For(0, keys.Length * 4, i => results[i % keys.Length] = t.Get(keys[i % keys.Length], ("count", 2)));
        Assert.All(results, Assert.NotNull);
    }

    [Fact]
    public void Each_file_is_loaded_once()
    {
        var opened = 0;
        var t = new Translator("fr", (ns, lang) =>
        {
            Interlocked.Increment(ref opened);
            return ns == "ns" ? new StringReader("a: b") : null;
        });
        Parallel.For(0, 200, _ => t.Get("ns.a"));
        Assert.Equal(1, opened); // ns.fr only: the key is found there, so English is never needed
    }

    /// <summary>Informational: per-language coverage against English. Never fails.</summary>
    [Fact]
    public void Key_coverage_report()
    {
        var report = new StringBuilder();
        foreach (var language in Translator.SupportedLanguages.Where(l => l != "en"))
        {
            var t = new Translator(language);
            var total = 0;
            var covered = 0;
            report.Append(CultureInfo.InvariantCulture, $"{language}:");
            foreach (var ns in Translator.Namespaces)
            {
                var english = En.KeysOf(ns, "en");
                var have = english.Count(k => t.HasKey(ns + "." + k, language));
                total += english.Count;
                covered += have;
                report.Append(CultureInfo.InvariantCulture, $" {ns} {have}/{english.Count};");
            }

            report.AppendLine(CultureInfo.InvariantCulture, $" total {covered}/{total} ({100.0 * covered / total:F0}%)");
        }

        output.WriteLine(report.ToString());
        TestContext.Current.AddAttachment("translation-coverage", report.ToString());
    }

    private static Translator FromYaml(string yaml) =>
        new("en", (ns, lang) => ns == "ns" && lang == "en" ? new StringReader(yaml) : null);
}
