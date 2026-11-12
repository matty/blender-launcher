using BlenderLauncher.Core.Settings.Upstream;

namespace BlenderLauncher.Core.Tests.Settings;

public class QSettingsIniReaderTests
{
    private static QSettingsIni Ini(string text) => QSettingsIni.Parse(text);

    [Fact]
    public void Upstream_ci_fixture_reads_every_key()
    {
        var ini = QSettingsIni.Load(Fixtures.PathOf("Settings/upstream-ci.ini"));

        Assert.Equal(19, ini.Count);
        Assert.Equal(".", ini.GetString("library_folder"));
        Assert.Equal("CachingBot", ini.GetString("user_id"));
        Assert.Equal("4.3", ini.GetString("minimum_blender_stable_version"));
        Assert.False(ini.Get("scrape_automated_builds")!.AsBool());
        Assert.True(ini.Get("first_time_setup_seen")!.AsBool());
        Assert.Equal(0, ini.Get("default_tab")!.AsInt());
        Assert.True(ini.Get("Internal/tray_icon_notified")!.AsBool());
        Assert.Equal("library_folder", ini.Keys[0]);
    }

    [Fact]
    public void General_section_has_no_prefix_and_others_do()
    {
        var ini = Ini("[General]\na=1\n[Internal]\nb=2\n[proxy]\ntype=3\n");
        Assert.Equal("1", ini.GetString("a"));
        Assert.Equal("2", ini.GetString("Internal/b"));
        Assert.Equal("3", ini.GetString("proxy/type"));
        Assert.Null(ini.GetString("b"));
    }

    [Fact]
    public void Keys_before_any_section_are_general_and_values_may_contain_equals_signs()
    {
        Assert.Equal("x", Ini("k=x").GetString("k"));
        Assert.Equal("a=b=c", Ini("k=a=b=c").GetString("k"));
    }

    [Fact]
    public void Backslash_in_key_is_a_slash_and_percent_escapes_decode()
    {
        var ini = Ini("[a]\nb\\c=1\n[General]\nkey%20with%20space=3\n%U00e9t%U00e9=4\n[%General]\nd=2\n");
        Assert.Equal("1", ini.GetString("a/b/c"));
        Assert.Equal("2", ini.GetString("General/d"));
        Assert.Equal("3", ini.GetString("key with space"));
        Assert.Equal("4", ini.GetString("été"));
    }

    [Fact]
    public void Lookups_ignore_case()
    {
        Assert.Equal("v", Ini("[Internal]\nWindow_Maximized=v").GetString("internal/window_maximized"));
    }

    [Fact]
    public void Quoted_value_with_escaped_quotes_and_commas_is_one_string()
    {
        var v = QSettingsIniReader.ParseValue("\"{\\\"4.2.0\\\": \\\"4.^.^-daily@^\\\", \\\"2.80.0\\\": \\\"2.^.^@^\\\"}\"");
        Assert.Equal(IniValueKind.String, v.Kind);
        Assert.Equal("{\"4.2.0\": \"4.^.^-daily@^\", \"2.80.0\": \"2.^.^@^\"}", v.Text);

        // Without the surrounding quotes the escaped quotes are still unescaped.
        Assert.Equal("{\"4.2.0\": \"4.^.^@^\"}", QSettingsIniReader.ParseValue("{\\\"4.2.0\\\": \\\"4.^.^@^\\\"}").Text);
    }

    [Theory]
    [InlineData(@"a\\b", @"a\b")]
    [InlineData(@"line\nbreak", "line\nbreak")]
    [InlineData(@"semi\;colon", "semi;colon")]
    [InlineData(@"comma\,kept", "comma,kept")]
    [InlineData(@"\x41\x00e9", "Aé")]
    [InlineData(@"oct\101", "octA")]
    public void Escapes(string raw, string expected) => Assert.Equal(expected, QSettingsIniReader.ParseValue(raw).Text);

    [Fact]
    public void Surrounding_spaces_are_trimmed_unless_quoted()
    {
        Assert.Equal("a b", QSettingsIniReader.ParseValue("   a b   ").Text);
        Assert.Equal("  a b  ", QSettingsIniReader.ParseValue("\"  a b  \"").Text);
        Assert.Equal("  x", QSettingsIniReader.ParseValue("  \"  x\"  ").Text);
    }

    [Fact]
    public void Unquoted_commas_make_a_list()
    {
        var v = QSettingsIniReader.ParseValue("a, b ,\"c, d\"");
        Assert.Equal(IniValueKind.List, v.Kind);
        Assert.Equal(["a", "b", "c, d"], v.Items);
        Assert.Equal("a, b, c, d", v.Text);
        Assert.Null(v.AsBool());
        Assert.Null(v.AsInt());
    }

    [Fact]
    public void Semicolon_outside_quotes_starts_a_comment()
    {
        Assert.Equal("value", QSettingsIniReader.ParseValue("value ; comment").Text);
        Assert.Equal("a;b", QSettingsIniReader.ParseValue("\"a;b\"").Text);
    }

    [Fact]
    public void Comment_lines_blank_lines_and_junk_are_ignored()
    {
        var ini = Ini("; c\n# c\n\n   \nnot a pair\n=novalue\n[General]\nk=v\n");
        Assert.Equal(["k"], ini.Keys);
    }

    [Theory]
    [InlineData("@ByteArray(\\x1\\xd9\\xd0\\xcb\\0\\x3)")]
    [InlineData("@Variant(\\0\\0\\0\\x7f\\0\\0\\0\\x18)")]
    public void Qt_variants_are_skipped(string raw)
    {
        var v = QSettingsIniReader.ParseValue(raw);
        Assert.Equal(IniValueKind.Skipped, v.Kind);
        Assert.Null(v.Text);
        Assert.Null(v.AsBool());
    }

    [Fact]
    public void Double_at_is_a_literal_at_and_String_variant_is_text()
    {
        Assert.Equal("@home", QSettingsIniReader.ParseValue("@@home").Text);
        Assert.Equal("hello", QSettingsIniReader.ParseValue("@String(hello)").Text);
        Assert.Equal("@unknown", QSettingsIniReader.ParseValue("@unknown").Text);
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("yes", true)]
    public void Booleans_follow_QVariant_toBool(string raw, bool expected) =>
        Assert.Equal(expected, QSettingsIniReader.ParseValue(raw).AsBool());

    [Theory]
    [InlineData("12", 12)]
    [InlineData(" -1 ", -1)]
    [InlineData("3.0", null)]
    public void Integers(string raw, int? expected) => Assert.Equal(expected, QSettingsIniReader.ParseValue(raw).AsInt());

    [Fact]
    public void Line_continuation_joins_lines()
    {
        var ini = Ini("k=abc\\\ndef\nother=C:\\\\\n");
        Assert.Equal("abcdef", ini.GetString("k"));
        Assert.Equal(@"C:\", ini.GetString("other"));
    }

    [Fact]
    public void Crlf_bom_and_repeated_keys()
    {
        var ini = Ini("\uFEFF[General]\r\nk=1\r\nk=2\r\n");
        Assert.Equal("2", ini.GetString("k"));
        Assert.Single(ini.Keys);
    }

    [Fact]
    public void Full_fixture_reads_its_quirks()
    {
        var ini = QSettingsIni.Load(Fixtures.PathOf("Settings/upstream-full.ini"));
        Assert.Equal("  --python-use-system-env  ", ini.GetString("blender_startup_arguments"));
        Assert.Equal("CTRL+Alt+B", ini.GetString("quick_launch_key_seq"));
        Assert.Equal("whatever", ini.GetString("some_future_key"));
        Assert.Equal(IniValueKind.Skipped, ini.Get("Internal/window_geometry")!.Kind);
        Assert.Equal("[73, 434, 124]", ini.GetString("Internal/global_column_widths"));
        Assert.Equal("10.0.0.1", ini.GetString("proxy/host"));
        Assert.StartsWith("[\"C:/Users/test/", ini.GetString("Internal/quick_launch_paths"), StringComparison.Ordinal);
    }
}
