using BlenderLauncher.Core.Localisation;

namespace BlenderLauncher.Core.Scraping;

/// <summary>
/// Turns a <see cref="ScrapeError"/> into the text the UI shows, through the <see cref="Translator"/>. Source ids
/// (<see cref="BuildSourceIds"/>, <c>upstream-api</c>, <c>launcher-update</c>) are shown by their display names,
/// the <c>ui.source.&lt;id&gt;</c> keys.
/// </summary>
public static class ScrapeErrorText
{
    /// <summary>The key prefix of source display names.</summary>
    public const string SourceKeyPrefix = "ui.source.";

    /// <summary><c>"%{source}: %{message}"</c>: a message that does not name its source gets this prefix.</summary>
    public const string WithSourceKey = "ui.err.scrape.with_source";

    /// <summary>The display-name key of <paramref name="sourceId"/>.</summary>
    public static string SourceKey(string sourceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        return SourceKeyPrefix + sourceId;
    }

    /// <summary>The display name of <paramref name="sourceId"/>, or the id itself when it has none.</summary>
    public static string SourceName(Translator translator, string sourceId)
    {
        ArgumentNullException.ThrowIfNull(translator);
        return translator.TryGet(SourceKey(sourceId), out var name) ? name : sourceId;
    }

    /// <summary>
    /// The translated message: the error's key with its placeholder values, where a <c>source</c> value is replaced
    /// by its display name. A message whose key has no <c>%{source}</c> placeholder is prefixed with the source's
    /// name (<see cref="WithSourceKey"/>), so the user always sees which source failed.
    /// </summary>
    public static string Format(Translator translator, ScrapeError error)
    {
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(error);
        var sourceName = SourceName(translator, error.SourceId);
        var args = new (string Name, object? Value)[error.MessageArgs.Count];
        var namesSource = false;
        for (var i = 0; i < args.Length; i++)
        {
            var (name, value) = error.MessageArgs[i];
            if (name == "source")
            {
                namesSource = true;
                args[i] = (name, SourceName(translator, value));
            }
            else
            {
                args[i] = (name, value);
            }
        }

        var message = translator.Get(error.MessageKey, args).TrimEnd();
        return namesSource ? message : translator.Get(WithSourceKey, ("source", sourceName), ("message", message));
    }
}
