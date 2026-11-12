using BlenderLauncher.App.Cli;

namespace BlenderLauncher.App.Tests.Cli;

public class CommandLineTests
{
    private const string Blend = @"C:\work\scene.blend";

    private static CliOptions Parse(string line) =>
        CommandLine.Parse(line.Length == 0 ? [] : line.Split(' '), path => path == Blend);

    /// <summary>
    /// One row per form: the command line (split on spaces) and the parsed result written as
    /// "command|file|query|flags|blender args", or "error" when it must be refused (exit code 2).
    /// </summary>
    [Theory]
    [InlineData("", "Gui||||")]
    [InlineData("-h", "Help||||")]
    [InlineData("launch --help", "error")]
    [InlineData("--help launch", "Help||||")]
    [InlineData("-debug --offline -instanced", "Gui|||debug,offline,instanced|")]
    [InlineData("-d -force-first-time", "Gui|||debug,first|")]
    [InlineData(@"-set-library-folder D:\Lib", @"Gui|||lib=D:\Lib|")]
    [InlineData("-set-library-folder", "error")]
    [InlineData(Blend, "Launch|" + Blend + "|||")]
    [InlineData("-debug " + Blend, "Launch|" + Blend + "||debug|")]
    [InlineData(Blend + " -debug", "Launch|" + Blend + "||debug|")]
    [InlineData(Blend + " extra", "error")]
    [InlineData(@"C:\missing.blend", "error")]
    [InlineData("--bogus", "error")]
    [InlineData("launch", "Launch||||")]
    [InlineData("launch -f " + Blend + " -v 4.2.^ -c", "Launch|" + Blend + "|4.2.^|cli|")]
    [InlineData("launch --file " + Blend + " --version ^.^.^-lts --cli", "Launch|" + Blend + "|^.^.^-lts|cli|")]
    [InlineData("launch -ol -c", "Launch|||openlast,cli|")]
    [InlineData("launch -ol -f " + Blend, "error")]
    [InlineData("launch -f " + Blend + " -ol", "error")]
    [InlineData("launch -f", "error")]
    [InlineData("launch -v", "error")]
    [InlineData("launch -c -- --factory-startup -- x", "Launch|||cli|--factory-startup -- x")]
    [InlineData("launch -- -b", "Launch||||-b")]
    [InlineData("launch -c --factory-startup", "error")]
    [InlineData("launch scene", "Launch||||scene")]
    [InlineData("launch --window-pos -3000,-3000 -f " + Blend, "Launch|" + Blend + "|||")]
    [InlineData("--window-pos -3000,-3000 --sample-data --accent orange", "Gui||||")]
    [InlineData("register", "Register||||")]
    [InlineData("unregister now", "error")]
    [InlineData("uninstall -q", "Uninstall|||quiet|")]
    [InlineData("uninstall --force", "error")]
    [InlineData("update", "Update||||")]
    [InlineData("update v1.2.0", "Update|||tag=v1.2.0|")]
    [InlineData("update v1 v2", "error")]
    [InlineData("update v0.1.0 --update-source C:\\rel --update-test-fail-swap", "Update|||tag=v0.1.0|")]
    [InlineData("--updated-from 0.0.9 --restarted", "Gui||||")]
    public void Parses_each_form(string line, string expected)
    {
        var o = Parse(line);
        if (expected == "error")
        {
            Assert.False(o.IsValid, $"'{line}' should be refused, got {o.Command}");
            return;
        }

        Assert.True(o.IsValid, o.Error);
        var flags = new List<string>();
        if (o.Debug) flags.Add("debug");
        if (o.Offline) flags.Add("offline");
        if (o.Instanced) flags.Add("instanced");
        if (o.ForceFirstTime) flags.Add("first");
        if (o.SetLibraryFolder is { } lib) flags.Add("lib=" + lib);
        if (o.OpenLast) flags.Add("openlast");
        if (o.Cli) flags.Add("cli");
        if (o.Quiet) flags.Add("quiet");
        if (o.UpdateTag is { } tag) flags.Add("tag=" + tag);
        var actual = $"{o.Command}|{o.File}|{o.VersionQuery}|{string.Join(',', flags)}|{string.Join(' ', o.BlenderArgs)}";
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void A_bare_file_is_marked_and_quotes_are_stripped()
    {
        var o = CommandLine.Parse(["\"" + Blend + "\""], path => path == Blend);
        Assert.Equal(CliCommand.Launch, o.Command);
        Assert.True(o.BareFile);
        Assert.Equal(Blend, o.File);
    }
}
