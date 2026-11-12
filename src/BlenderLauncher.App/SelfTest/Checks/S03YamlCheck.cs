using System.Diagnostics;
using BlenderLauncher.Core.Localisation;

namespace BlenderLauncher.App.SelfTest.Checks;

/// <summary>
/// Spike S03: the <see cref="Translator"/> loads every embedded translation file through YamlDotNet's
/// <c>YamlStream</c> in the published exe, for all five languages, and a few lookups give upstream's text
/// (quoted <c>"yes"</c> key, <c>|</c> block scalar, placeholder, plural, English fallback).
/// </summary>
internal sealed class S03YamlCheck : ISelfTestCheck
{
    public string Id => "S03";
    public string Name => "Translator loads all languages (YamlStream)";
    public bool NeedsUiThread => false;
    public bool NeedsNetwork => false;

    public Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var counts = new List<string>();
        foreach (var language in Translator.SupportedLanguages)
        {
            ct.ThrowIfCancellationRequested();
            var translator = new Translator(language);
            var keys = 0;
            var files = 0;
            foreach (var ns in Translator.Namespaces)
            {
                var n = translator.KeysOf(ns, language).Count;
                keys += n;
                files += n > 0 ? 1 : 0;
            }

            if (keys == 0)
            {
                return Task.FromResult(SelfTestResult.Fail($"{language}: no keys loaded"));
            }

            counts.Add($"{language}={keys}/{files}f");
        }

        var en = new Translator("en");
        var es = new Translator("es");
        (string Actual, string Expected)[] expectations =
        [
            (en["act.yes"], "Yes"),
            (en["act.lprev"], " Previous"),
            (en["act.a.check_tooltip"], "Check for new builds online\n(Hold SHIFT to force check stable and automated builds)\n"),
            (en.Get("act.prog.progress", ("title", "T"), ("progress", 1), ("total", 2)), "T: 1 of 2 MB"),
            (en.GetPlural("msg.popup.ask_remove_from_drive", 3), "Are you sure you want to\ndelete 3 builds?\n"),
            (es["msg.popup.ask_remove_from_drive"], "msg.popup.ask_remove_from_drive"),
            (es["act.no.such.key"], "act.no.such.key"),
            (es.GetPlural("msg.popup.ask_send_to_trash", 1), en.GetPlural("msg.popup.ask_send_to_trash", 1)),
        ];
        foreach (var (actual, expected) in expectations)
        {
            if (actual != expected)
            {
                return Task.FromResult(SelfTestResult.Fail($"expected \"{expected}\", got \"{actual}\""));
            }
        }

        var detail = $"keys/files per language: {string.Join(", ", counts)}; lookups correct; {watch.ElapsedMilliseconds} ms";
        ctx.Log(detail);
        return Task.FromResult(SelfTestResult.Pass(detail));
    }
}
