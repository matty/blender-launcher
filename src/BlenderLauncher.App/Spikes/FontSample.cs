using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using BlenderLauncher.App.Theme;

namespace BlenderLauncher.App.Spikes;

/// <summary>
/// Spike S7: the embedded IBM Plex fonts and what happens to Japanese and Chinese text, which Plex does not cover.
/// Shared by the S07 self-test check and the manual <c>--spike fonts</c> window.
/// </summary>
internal static class FontSample
{
    public const string Latin = "Blender Launcher - Check for builds, 4.2.3 LTS (0123456789)";
    public const string Japanese = "ビルドを確認 ダウンロード中 起動する 設定";
    public const string Chinese = "检查构建版本 正在下载 启动 设置";
    public const string Mono = "a1b2c3d  main  312.4 MB  2026-11-11";

    /// <summary>An unassigned code point: no font has it, so it must come out as .notdef (glyph 0). Proves the detector works.</summary>
    public const string Missing = "͸";

    /// <summary>The composite family (Plex followed by explicit CJK families), kept for comparison.</summary>
    public static FontFamily PlexWithFallbacks { get; } =
        new($"{AppFonts.CollectionKey}#{AppFonts.PlexSansName}, {AppFonts.JapaneseFallback}, {AppFonts.ChineseFallback}");

    public readonly record struct RunInfo(string Family, int Glyphs, int NotDef);

    /// <summary>
    /// Shapes <paramref name="text"/> and reports, per run, the font actually used and how many glyphs are .notdef (tofu).
    /// <paramref name="culture"/> is the text run's culture, which steers Avalonia's Han fallback (null is what a
    /// TextBlock passes).
    /// </summary>
    public static List<RunInfo> Analyse(FontFamily family, string text, CultureInfo? culture = null, FontWeight weight = FontWeight.Normal)
    {
        var properties = new GenericTextRunProperties(
            new Typeface(family, FontStyle.Normal, weight), 16, foregroundBrush: Brushes.White, cultureInfo: culture);
        var paragraph = new GenericTextParagraphProperties(properties, TextAlignment.Left, TextWrapping.NoWrap, 0, 0);
        using var layout = new TextLayout(new SingleRunSource(text, properties), paragraph);
        var runs = new List<RunInfo>();
        foreach (var line in layout.TextLines)
        {
            foreach (var run in line.TextRuns)
            {
                if (run is ShapedTextRun shaped)
                {
                    var glyphs = shaped.GlyphRun.GlyphInfos;
                    var notDef = 0;
                    foreach (var g in glyphs)
                    {
                        if (g.GlyphIndex == 0)
                        {
                            notDef++;
                        }
                    }

                    runs.Add(new RunInfo(shaped.GlyphRun.GlyphTypeface.FamilyName, glyphs.Count, notDef));
                }
            }
        }

        return runs;
    }

    private sealed class SingleRunSource(string text, TextRunProperties properties) : ITextSource
    {
        public TextRun? GetTextRun(int textSourceIndex) =>
            textSourceIndex < text.Length ? new TextCharacters(text[textSourceIndex..], properties) : null;
    }

    public static string Describe(List<RunInfo> runs) =>
        string.Join(" + ", runs.Select(r => r.NotDef > 0 ? $"{r.Family}[{r.Glyphs}, notdef {r.NotDef}]" : $"{r.Family}[{r.Glyphs}]"));

    /// <summary>The sample laid out as the S07 PNG and the manual window show it.</summary>
    public static Control BuildVisual()
    {
        var panel = new StackPanel { Spacing = 6 };

        void Header(string text) => panel.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = AppFonts.PlexMono,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#8a8a8a")),
            Margin = new Thickness(0, 8, 0, 0),
        });

        void Line(string text, FontFamily family, FontWeight weight, double size = 16) => panel.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = family,
            FontWeight = weight,
            FontSize = size,
            Foreground = new SolidColorBrush(Color.Parse("#e4e4e4")),
        });

        Header("IBM Plex Sans 400 / 500 / 600 / 700");
        Line(Latin, AppFonts.PlexSans, FontWeight.Normal);
        Line(Latin, AppFonts.PlexSans, FontWeight.Medium);
        Line(Latin, AppFonts.PlexSans, FontWeight.SemiBold);
        Line(Latin, AppFonts.PlexSans, FontWeight.Bold);
        Header("IBM Plex Mono 400 / 500");
        Line(Mono, AppFonts.PlexMono, FontWeight.Normal, 13);
        Line(Mono, AppFonts.PlexMono, FontWeight.Medium, 13);
        Header("Japanese, Chinese: Plex with the configured CJK FontFallbacks");
        Line(Japanese, AppFonts.PlexSans, FontWeight.Normal);
        Line(Chinese, AppFonts.PlexSans, FontWeight.Normal);
        Line(Japanese, AppFonts.PlexSans, FontWeight.SemiBold);
        Header($"Composite family \"Plex, {AppFonts.JapaneseFallback}, {AppFonts.ChineseFallback}\" (comparison)");
        Line(Japanese, PlexWithFallbacks, FontWeight.Normal);
        Line(Chinese, PlexWithFallbacks, FontWeight.Normal);
        Header("Mixed line");
        Line($"Blender 4.2 LTS - {Japanese} - {Chinese}", AppFonts.PlexSans, FontWeight.Normal, 14);
        Header("Control: unassigned code point U+0378 (expected tofu)");
        Line($"[{Missing}]", AppFonts.PlexSans, FontWeight.Normal);

        return new Border { Child = panel, Padding = new Thickness(16), Background = new SolidColorBrush(Color.Parse("#2a2a2a")) };
    }

    /// <summary>Lays out and renders a control off-screen at the given scale.</summary>
    public static RenderTargetBitmap Render(Control control, double width, double scale = 2)
    {
        control.Measure(new Size(width, double.PositiveInfinity));
        control.Arrange(new Rect(0, 0, width, control.DesiredSize.Height));
        var pixels = new PixelSize((int)Math.Ceiling(width * scale), (int)Math.Ceiling(control.DesiredSize.Height * scale));
        var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        bitmap.Render(control);
        return bitmap;
    }

    /// <summary>Renders one line of text white on transparent and counts pixels with any alpha: the ink.</summary>
    public static int Ink(string text, FontFamily family)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = family,
            FontSize = 24,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        using var bitmap = Render(block, 900, 1);
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        unsafe
        {
            fixed (byte* p = buffer)
            {
                bitmap.CopyPixels(new PixelRect(size), (nint)p, buffer.Length, stride);
            }
        }

        var ink = 0;
        for (var i = 3; i < buffer.Length; i += 4)
        {
            if (buffer[i] > 64)
            {
                ink++;
            }
        }

        return ink;
    }

    public static string Culture => CultureInfo.CurrentUICulture.Name;
}
