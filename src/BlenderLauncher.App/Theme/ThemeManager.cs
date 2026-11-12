using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.Theme;

/// <summary>
/// Applies the runtime-switchable tokens (settings <c>appearance.accent</c> and <c>appearance.density</c>) by
/// overriding their keys in a resource dictionary, normally <see cref="Application.Resources"/>. Everything that uses
/// them through <c>DynamicResource</c> updates at once; no restart.
/// </summary>
public static class ThemeManager
{
    /// <summary>The design's accent palettes: main colour, hover (≈ the design's brightness(1.12)), row, row hover.</summary>
    private static readonly IReadOnlyDictionary<Accent, (Color Main, Color Hover, Color Row, Color RowHover)> Accents =
        new Dictionary<Accent, (Color, Color, Color, Color)>
        {
            [Accent.Blue] = (Color.Parse("#4772b3"), Color.Parse("#5080c8"), Color.Parse("#34445e"), Color.Parse("#3a4c69")),
            [Accent.Orange] = (Color.Parse("#b8620f"), Color.Parse("#ce6e11"), Color.Parse("#4a3a2a"), Color.Parse("#54412e")),
        };

    public const double ComfortableRowHeight = 48;
    public const double CompactRowHeight = 34;

    /// <summary>The accent currently applied (blue until <see cref="ApplyAccent"/> is called).</summary>
    public static Accent CurrentAccent { get; private set; } = Accent.Blue;

    /// <summary>The density currently applied.</summary>
    public static Density CurrentDensity { get; private set; } = Density.Comfortable;

    public static void Apply(AppearanceSettings appearance, IResourceDictionary? target = null)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        ApplyAccent(appearance.Accent, target);
        ApplyDensity(appearance.Density, target);
    }

    public static void ApplyAccent(Accent accent, IResourceDictionary? target = null)
    {
        var resources = target ?? Resources();
        if (resources is null)
        {
            return;
        }

        if (!Accents.TryGetValue(accent, out var palette))
        {
            palette = Accents[Accent.Blue];
        }

        resources["BlAccentColor"] = palette.Main;
        resources["BlAccentBrush"] = new SolidColorBrush(palette.Main);
        resources["BlAccentHoverBrush"] = new SolidColorBrush(palette.Hover);
        resources["BlAccentRowBrush"] = new SolidColorBrush(palette.Row);
        resources["BlAccentRowHoverBrush"] = new SolidColorBrush(palette.RowHover);

        // Fluent's own keys for the form controls (Theme/Forms.axaml): focused border, selection, selected item.
        resources["TextControlBorderBrushFocused"] = new SolidColorBrush(palette.Main);
        resources["TextControlSelectionHighlightColor"] = new SolidColorBrush(palette.Main);
        resources["ComboBoxBackgroundBorderBrushFocused"] = new SolidColorBrush(palette.Main);
        resources["ComboBoxItemBackgroundSelected"] = new SolidColorBrush(palette.Row);
        resources["ComboBoxItemBackgroundSelectedPointerOver"] = new SolidColorBrush(palette.RowHover);
        resources["ComboBoxItemBackgroundSelectedPressed"] = new SolidColorBrush(palette.RowHover);
        CurrentAccent = accent;
    }

    public static void ApplyDensity(Density density, IResourceDictionary? target = null)
    {
        var resources = target ?? Resources();
        if (resources is null)
        {
            return;
        }

        var compact = density == Density.Compact;
        resources["BlRowHeight"] = compact ? CompactRowHeight : ComfortableRowHeight;
        resources["BlRowSecondLineVisible"] = !compact;
        CurrentDensity = density;
    }

    private static IResourceDictionary? Resources() => Application.Current?.Resources;
}
