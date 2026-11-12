using Avalonia.Markup.Xaml;

namespace BlenderLauncher.App.Localisation;

/// <summary>
/// <c>{loc:Tr key}</c>: the translated string for <paramref name="key"/>, looked up once when the view is built
///. AOT-safe: the XAML compiler emits a direct call to <see cref="ProvideValue"/>; there is no binding and no
/// reflection. A language change needs a restart, so the value never has to update.
/// </summary>
/// <example><c>xmlns:loc="using:BlenderLauncher.App.Localisation"</c> … <c>&lt;TextBlock Text="{loc:Tr ui.detail.remove}" /&gt;</c></example>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key)
    {
        Key = key;
    }

    /// <summary>A translation key, <c>namespace.path</c>, for example <c>ui.sidebar.all_builds</c> or <c>act.launch</c>.</summary>
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Tr(Key);
}
