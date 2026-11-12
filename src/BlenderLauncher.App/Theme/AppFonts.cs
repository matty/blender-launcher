using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace BlenderLauncher.App.Theme;

/// <summary>
/// The embedded IBM Plex families: static TTFs in <c>Assets/Fonts</c> (IBM Plex Sans 1.1.0 and IBM Plex Mono 2.5.0,
/// the official releases from github.com/IBM/plex; SIL OFL 1.1 in <c>Assets/Fonts/OFL.txt</c>). Avalonia does not
/// support variable fonts, so only the static weights the design uses are shipped: Sans 400/500/600/700, Mono 400/500.
/// <para>
/// The TTFs are plain embedded resources loaded by <see cref="AppFontCollection"/>, not <c>avares://</c> assets:
/// Avalonia 12.1.6 cannot resolve an <c>avares://</c> URI for an assembly whose name contains a space
/// (<c>Uri.Authority</c> is <c>Blender%20Launcher</c>, which its resolver compares unescaped against the module name),
/// so <c>avares://Blender Launcher/Assets/Fonts#IBM Plex Sans</c> finds no fonts (spike S7).
/// </para>
/// <para>
/// Japanese and Chinese: Plex has no CJK glyphs. Left to itself, Avalonia's system fallback does find a CJK font (no
/// tofu), but which one depends on what was shaped earlier in the process: on an en-GB system Simplified Chinese came
/// out in Microsoft JhengHei UI (Traditional forms), and after one Chinese string even Japanese kana did. A composite
/// family ("IBM Plex Sans, Yu Gothic UI, Microsoft YaHei UI") changed nothing. What works is
/// <see cref="FontManagerOptions.FontFallbacks"/>, consulted first for any character Plex lacks, ordered by the UI
/// language (<see cref="CreateFontManagerOptions"/>). Measured in S07.
/// </para>
/// </summary>
public static class AppFonts
{
    /// <summary>Key of <see cref="AppFontCollection"/>; families are addressed as <c>fonts:BlenderLauncher#Name</c>.</summary>
    public const string CollectionKey = "fonts:BlenderLauncher";

    public const string PlexSansName = "IBM Plex Sans";
    public const string PlexMonoName = "IBM Plex Mono";

    public const string JapaneseFallback = "Yu Gothic UI";
    public const string ChineseFallback = "Microsoft YaHei UI";
    public const string TraditionalChineseFallback = "Microsoft JhengHei UI";

    public static FontFamily PlexSans { get; } = new($"{CollectionKey}#{PlexSansName}");

    public static FontFamily PlexMono { get; } = new($"{CollectionKey}#{PlexMonoName}");

    /// <summary>
    /// CJK fallback order for a UI language: the language's own font first, so shared Han characters take its glyph
    /// forms. All three ship with Windows 10 and 11. The order is fixed at start-up, so changing the UI language needs
    /// a restart to change it.
    /// </summary>
    public static string[] CjkFallbacks(CultureInfo uiCulture)
    {
        ArgumentNullException.ThrowIfNull(uiCulture);
        var name = uiCulture.Name;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            var traditional = name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
                              name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
                              name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
                              name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase);
            return traditional
                ? [TraditionalChineseFallback, ChineseFallback, JapaneseFallback]
                : [ChineseFallback, TraditionalChineseFallback, JapaneseFallback];
        }

        // Japanese and every other language (Japanese build names are likelier than Chinese ones in a non-CJK UI).
        return [JapaneseFallback, ChineseFallback, TraditionalChineseFallback];
    }

    /// <summary>Plex as the app-wide default family, with the CJK fallbacks for <paramref name="uiCulture"/>.</summary>
    public static FontManagerOptions CreateFontManagerOptions(CultureInfo uiCulture) => new()
    {
        DefaultFamilyName = $"{CollectionKey}#{PlexSansName}",
        FontFallbacks = [.. CjkFallbacks(uiCulture).Select(f => new FontFallback { FontFamily = new FontFamily(f) })],
    };
}

/// <summary>
/// Loads the TTFs embedded under the logical name <c>Fonts/&lt;file&gt;.ttf</c>. Registered in
/// <c>Program.BuildAvaloniaApp</c> through <c>ConfigureFonts</c>.
/// </summary>
public sealed class AppFontCollection : FontCollectionBase
{
    private const string ResourcePrefix = "Fonts/";

    public AppFontCollection()
    {
        var assembly = typeof(AppFontCollection).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The manifest stream points into the loaded image and lives as long as the process; not disposed.
            var stream = assembly.GetManifestResourceStream(name);
            if (stream is not null && TryAddGlyphTypeface(stream, out _))
            {
                LoadedFiles++;
            }
        }
    }

    public override Uri Key { get; } = new(AppFonts.CollectionKey);

    /// <summary>How many TTFs were added (6 expected).</summary>
    public int LoadedFiles { get; }
}
