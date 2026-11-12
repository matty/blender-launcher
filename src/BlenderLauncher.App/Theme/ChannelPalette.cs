using Avalonia.Media;
using Avalonia.Media.Immutable;
using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.App.Theme;

/// <summary>The two brushes of a channel pill: text and its 14% tint.</summary>
public sealed record ChannelColours(IImmutableSolidColorBrush Foreground, IImmutableSolidColorBrush Background, string StyleClass);

/// <summary>
/// Channel colours. Stable, LTS, Daily and Experimental are the design's; Bforartists, UPBGE and
/// Custom are proposed. Patch builds share Experimental's colours (they live in the same library folder); both UPBGE
/// channels share one. Mirrored as <c>BlChannel*Brush</c> resources in <c>Tokens.axaml</c> and as the
/// <c>Border.pill.&lt;class&gt;</c> styles in <c>Styles.axaml</c>.
/// </summary>
public static class ChannelPalette
{
    public static ChannelColours Stable { get; } = Make(0x7aa6e6, 0.14, "stable");
    public static ChannelColours Lts { get; } = Make(0x82c784, 0.14, "lts");
    public static ChannelColours Daily { get; } = Make(0xeda15a, 0.14, "daily");
    public static ChannelColours Experimental { get; } = Make(0xc196e6, 0.15, "experimental");
    public static ChannelColours Bforartists { get; } = Make(0xe68a7a, 0.14, "bforartists");
    public static ChannelColours Upbge { get; } = Make(0x6fc7c0, 0.14, "upbge");
    public static ChannelColours Custom { get; } = Make(0xb0b0b0, 0.14, "custom");

    /// <summary>The sidebar's dot for "All builds" and "Favorites" (not channels).</summary>
    public static IImmutableSolidColorBrush AllDot { get; } = new ImmutableSolidColorBrush(Color.FromRgb(0x8a, 0x8a, 0x8a));

    public static IImmutableSolidColorBrush FavoritesDot { get; } = new ImmutableSolidColorBrush(Color.FromRgb(0xd9, 0xb5, 0x4a));

    public static ChannelColours For(Channel channel) => channel switch
    {
        Channel.Stable => Stable,
        Channel.Lts => Lts,
        Channel.Daily => Daily,
        Channel.Experimental or Channel.Patch => Experimental,
        Channel.Bforartists => Bforartists,
        Channel.UpbgeStable or Channel.UpbgeWeekly => Upbge,
        _ => Custom,
    };

    private static ChannelColours Make(uint rgb, double alpha, string styleClass)
    {
        var colour = Color.FromUInt32(0xff000000 | rgb);
        var tint = Color.FromArgb((byte)Math.Round(alpha * 255), colour.R, colour.G, colour.B);
        return new ChannelColours(new ImmutableSolidColorBrush(colour), new ImmutableSolidColorBrush(tint), styleClass);
    }
}
