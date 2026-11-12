using System.Globalization;
using BlenderLauncher.Core.Localisation;

namespace BlenderLauncher.App.Localisation;

/// <summary>
/// The app-wide translator for views and view models. Set once at start-up, before any window is built, from
/// the <c>language</c> setting; changing the language needs a restart, so nothing here is observable.
/// </summary>
/// <remarks>
/// In code: <c>Loc.Tr("ui.detail.remove")</c>, <c>Loc.Tr("ui.detail.launch_version", ("version", "5.2.2"))</c>,
/// <c>Loc.Plural("ui.status.instances_running", count)</c>. In XAML: <c>Text="{loc:Tr ui.detail.remove}"</c>.
/// Until <see cref="Initialise"/> is called (design previewer, unit tests) the OS language is used.
/// </remarks>
public static class Loc
{
    private static Translator? _translator;

    /// <summary>The translator in use.</summary>
    public static Translator Translator =>
        Volatile.Read(ref _translator) ?? Interlocked.CompareExchange(ref _translator, Translator.Create("auto", CultureInfo.CurrentUICulture), null) ?? _translator!;

    /// <summary>Sets the translator for the process. Called by the start-up code before Avalonia builds any view.</summary>
    public static void Initialise(Translator translator)
    {
        ArgumentNullException.ThrowIfNull(translator);
        Volatile.Write(ref _translator, translator);
    }

    /// <summary>Translates <paramref name="key"/>; returns the key itself when it does not exist.</summary>
    public static string Tr(string key) => Translator.Get(key);

    /// <summary>Translates <paramref name="key"/>, filling <c>%{name}</c> placeholders.</summary>
    public static string Tr(string key, params ReadOnlySpan<(string Name, object? Value)> args) => Translator.Get(key, args);

    /// <summary>Translates a plural key (<c>one</c>/<c>many</c>); <paramref name="count"/> also fills <c>%{count}</c>.</summary>
    public static string Plural(string key, long count, params ReadOnlySpan<(string Name, object? Value)> args) =>
        Translator.GetPlural(key, count, args);
}
