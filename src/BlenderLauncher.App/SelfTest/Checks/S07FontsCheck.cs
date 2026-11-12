using System.Globalization;
using Avalonia.Media;
using BlenderLauncher.App.Spikes;
using BlenderLauncher.App.Theme;

namespace BlenderLauncher.App.SelfTest.Checks;

/// <summary>
/// Spike S07: the embedded IBM Plex static TTFs load at every shipped weight (and are not silently replaced by a
/// system font); Latin text stays in Plex; Japanese and Chinese text shape to real glyphs (no .notdef) in the CJK
/// fallback fonts configured by <see cref="AppFonts.CreateFontManagerOptions"/>, whatever the run's culture, and leave
/// ink when rendered. Writes <c>fonts.png</c> to the work directory (kept with <c>--keep-temp</c>) for a visual check.
/// </summary>
internal sealed class S07FontsCheck : ISelfTestCheck
{
    public string Id => "S07";
    public string Name => "Embedded fonts and CJK fallback";
    public bool NeedsUiThread => true;
    public bool NeedsNetwork => false;

    public Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct)
    {
        var failures = new List<string>();

        // 1. Every shipped weight resolves to the embedded font itself. The legacy family names of the middle weights
        //    are "IBM Plex Sans Medm" and "IBM Plex Sans SmBld"; the typographic family is "IBM Plex Sans".
        (FontFamily Family, string Name, FontWeight Weight)[] faces =
        [
            (AppFonts.PlexSans, AppFonts.PlexSansName, FontWeight.Normal),
            (AppFonts.PlexSans, AppFonts.PlexSansName, FontWeight.Medium),
            (AppFonts.PlexSans, AppFonts.PlexSansName, FontWeight.SemiBold),
            (AppFonts.PlexSans, AppFonts.PlexSansName, FontWeight.Bold),
            (AppFonts.PlexMono, AppFonts.PlexMonoName, FontWeight.Normal),
            (AppFonts.PlexMono, AppFonts.PlexMonoName, FontWeight.Medium),
        ];
        foreach (var (family, name, weight) in faces)
        {
            if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(family, FontStyle.Normal, weight), out var face))
            {
                failures.Add($"{name} {(int)weight}: not found");
                continue;
            }

            ctx.Log($"{name} {(int)weight} -> {face.FamilyName} weight {(int)face.Weight}, {face.GlyphCount} glyphs");
            if (!face.FamilyName.StartsWith(name, StringComparison.Ordinal) || face.Weight != weight)
            {
                failures.Add($"{name} {(int)weight}: got {face.FamilyName} {(int)face.Weight}");
            }
        }

        if (FontManager.Current.DefaultFontFamily.Name != AppFonts.PlexSansName)
        {
            failures.Add($"default family is {FontManager.Current.DefaultFontFamily.Name}");
        }

        // 2. Shaping: which font each run uses, and whether any glyph is .notdef. The fallback order is the one for the
        //    process's UI culture; the run culture must not change it (it did without FontFallbacks).
        var uiCulture = CultureInfo.CurrentUICulture;
        var fallbacks = AppFonts.CjkFallbacks(uiCulture);
        ctx.Log($"UI culture '{uiCulture.Name}', CJK fallbacks: {string.Join(", ", fallbacks)}");

        var latin = FontSample.Analyse(AppFonts.PlexSans, FontSample.Latin);
        var missing = FontSample.Analyse(AppFonts.PlexSans, FontSample.Missing);
        ctx.Log($"latin: {FontSample.Describe(latin)}");
        ctx.Log($"control U+0378: {FontSample.Describe(missing)}");

        if (latin.Any(r => r.Family != AppFonts.PlexSansName || r.NotDef > 0))
        {
            failures.Add($"latin not all Plex: {FontSample.Describe(latin)}");
        }

        if (missing.Sum(r => r.NotDef) == 0)
        {
            failures.Add($"detector broken: U+0378 did not give .notdef ({FontSample.Describe(missing)})");
        }

        string? jaFamilies = null;
        string? zhFamilies = null;
        foreach (var runCulture in new CultureInfo?[] { null, CultureInfo.GetCultureInfo("zh-CN"), CultureInfo.GetCultureInfo("ja-JP") })
        {
            var tag = runCulture?.Name ?? "none";
            var ja = FontSample.Analyse(AppFonts.PlexSans, FontSample.Japanese, runCulture);
            var zh = FontSample.Analyse(AppFonts.PlexSans, FontSample.Chinese, runCulture);
            ctx.Log($"run culture {tag}: ja {FontSample.Describe(ja)}");
            ctx.Log($"run culture {tag}: zh {FontSample.Describe(zh)}");

            CheckCjk("ja", tag, ja, fallbacks, failures);
            CheckCjk("zh", tag, zh, fallbacks, failures);

            var jaNow = string.Join('/', ja.Select(r => r.Family).Where(f => f != AppFonts.PlexSansName).Distinct());
            var zhNow = string.Join('/', zh.Select(r => r.Family).Where(f => f != AppFonts.PlexSansName).Distinct());
            if ((jaFamilies ??= jaNow) != jaNow || (zhFamilies ??= zhNow) != zhNow)
            {
                failures.Add($"fallback changed with run culture {tag}: ja {jaNow}, zh {zhNow}");
            }
        }

        // The configured fallback for a Japanese UI must keep Japanese text in Yu Gothic UI (all its characters exist there).
        if (fallbacks[0] == AppFonts.JapaneseFallback && jaFamilies != AppFonts.JapaneseFallback)
        {
            failures.Add($"Japanese not in {AppFonts.JapaneseFallback}: {jaFamilies}");
        }

        // The language-specific order (pure function, so other UI languages are covered without changing the process culture).
        if (AppFonts.CjkFallbacks(CultureInfo.GetCultureInfo("zh-CN"))[0] != AppFonts.ChineseFallback ||
            AppFonts.CjkFallbacks(CultureInfo.GetCultureInfo("zh-TW"))[0] != AppFonts.TraditionalChineseFallback ||
            AppFonts.CjkFallbacks(CultureInfo.GetCultureInfo("ja-JP"))[0] != AppFonts.JapaneseFallback)
        {
            failures.Add("CJK fallback order per UI language is wrong");
        }

        // The composite family, for the record (it does not steer the fallback; FontFallbacks does).
        ctx.Log($"composite family, zh: {FontSample.Describe(FontSample.Analyse(FontSample.PlexWithFallbacks, FontSample.Chinese))}");

        // 3. Ink: CJK lines must actually draw something comparable to a Latin line.
        var inkLatin = FontSample.Ink("Check for builds", AppFonts.PlexSans);
        var inkJa = FontSample.Ink(FontSample.Japanese, AppFonts.PlexSans);
        var inkZh = FontSample.Ink(FontSample.Chinese, AppFonts.PlexSans);
        ctx.Log($"ink (alpha>64 px at 24 px): latin {inkLatin}, ja {inkJa}, zh {inkZh}");
        if (inkLatin == 0 || inkJa < inkLatin / 2 || inkZh < inkLatin / 2)
        {
            failures.Add($"too little ink: latin {inkLatin}, ja {inkJa}, zh {inkZh}");
        }

        // 4. The whole sample as a PNG for a human to look at.
        var png = Path.Combine(ctx.WorkDirectory, "fonts.png");
        using (var bitmap = FontSample.Render(FontSample.BuildVisual(), 760))
        {
            bitmap.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }

        ctx.Log($"sample written to {png}");

        var detail = $"Plex 6 faces; FontFallbacks [{string.Join(", ", fallbacks)}]; ja->{jaFamilies}, zh->{zhFamilies}; " +
                     $"ink latin {inkLatin} ja {inkJa} zh {inkZh}";
        return Task.FromResult(failures.Count == 0
            ? SelfTestResult.Pass(detail)
            : SelfTestResult.Fail(string.Join("; ", failures) + " | " + detail));
    }

    private static void CheckCjk(string label, string tag, List<FontSample.RunInfo> runs, string[] fallbacks, List<string> failures)
    {
        if (runs.Sum(r => r.NotDef) > 0)
        {
            failures.Add($"{label} tofu (run culture {tag}): {FontSample.Describe(runs)}");
        }

        var stray = runs.Select(r => r.Family).Where(f => f != AppFonts.PlexSansName && !fallbacks.Contains(f)).Distinct().ToList();
        if (stray.Count > 0)
        {
            failures.Add($"{label} used unconfigured fonts (run culture {tag}): {string.Join(", ", stray)}");
        }
    }
}
