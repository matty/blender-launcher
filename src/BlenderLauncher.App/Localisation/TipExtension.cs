using Avalonia.Markup.Xaml;

namespace BlenderLauncher.App.Localisation;

/// <summary>
/// <c>{loc:Tip key}</c>: a tooltip text, as <see cref="TrExtension"/> but with the trailing line break of upstream's
/// block-style <c>*_tooltip</c> strings removed, so the tooltip has no empty last line.
/// </summary>
public sealed class TipExtension : MarkupExtension
{
    public TipExtension()
    {
    }

    public TipExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Tr(Key).Trim();
}
