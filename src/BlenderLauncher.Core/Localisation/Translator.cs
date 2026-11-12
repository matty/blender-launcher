using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace BlenderLauncher.Core.Localisation;

/// <summary>
/// Port of upstream's python-i18n set-up (<c>utils/i18n_init.py</c>).
/// Keys are <c>namespace.path.key</c>; the namespace picks the file <c>&lt;namespace&gt;.&lt;lang&gt;.yml</c>.
/// </summary>
/// <remarks>
/// Behaviour, as python-i18n with upstream's settings (<c>fallback=en</c>, <c>plural_few=1</c>,
/// <c>skip_locale_root_data</c>, missing-key and missing-plural errors off):
/// <list type="bullet">
/// <item>Fallback is per key: a key missing from the chosen language is looked up in English.</item>
/// <item>A key missing from both returns the key itself (the settings tooltip logic relies on this).</item>
/// <item>Passing a <c>count</c> argument selects a plural sub-key: <c>zero</c> for 0, <c>one</c> for 1,
/// <c>few</c> for any other count not above 1 (negative counts, given <c>plural_few=1</c>), and <c>many</c>
/// otherwise or when the preferred sub-key is absent. A plain string ignores <c>count</c>. A plural node
/// without the sub-key it needs and without <c>many</c> returns the key.</item>
/// <item>Placeholders follow Python's <c>string.Template.safe_substitute</c> with <c>%</c> as delimiter:
/// <c>%{name}</c> and <c>%name</c> are replaced when an argument of that name is given and left as written
/// otherwise; <c>%%</c> becomes <c>%</c>.</item>
/// </list>
/// Each namespace and language is loaded on first use; all lookups are safe from any thread.
/// </remarks>
public sealed class Translator
{
    public const string FallbackLanguage = "en";

    /// <summary>Upstream's namespaces plus <c>ui</c>, our own namespace for design-only strings.</summary>
    public static IReadOnlyList<string> Namespaces { get; } = ["act", "msg", "repo", "settings", "wizard", "launching", "custom_build", "ui"];

    /// <summary>The languages the <c>language</c> setting offers, besides <c>auto</c>.</summary>
    public static IReadOnlyList<string> SupportedLanguages { get; } = ["en", "es", "fr", "ja", "zh"];

    private static readonly string[] PluralKeys = ["zero", "one", "few", "many"];

    private const int PluralFew = 1;

    private readonly Func<string, string, TextReader?> _open;
    private readonly ConcurrentDictionary<(string Namespace, string Language), Lazy<IReadOnlyDictionary<string, string>>> _tables = new();

    /// <summary>Creates a translator over the translation files embedded in Core.</summary>
    public Translator(string language)
        : this(language, OpenEmbedded)
    {
    }

    /// <summary>
    /// Creates a translator over a custom source. <paramref name="open"/> gets the namespace and language and
    /// returns the YAML text, or null when there is no such file. It may be called from any thread, at most
    /// once per pair.
    /// </summary>
    public Translator(string language, Func<string, string, TextReader?> open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(open);
        Language = language;
        _open = open;
    }

    /// <summary>The language in use, for example <c>fr</c>.</summary>
    public string Language { get; }

    /// <summary>Same as <see cref="Get(string, ReadOnlySpan{ValueTuple{string, object}})"/> with no arguments.</summary>
    public string this[string key] => Get(key);

    /// <summary>
    /// Translates <paramref name="key"/>, filling placeholders from <paramref name="args"/>. An argument named
    /// <c>count</c> also selects the plural form. Returns the key itself when it is not found.
    /// </summary>
    public string Get(string key, params ReadOnlySpan<(string Name, object? Value)> args)
    {
        TryGet(key, out var value, args);
        return value;
    }

    /// <summary>Translates a plural key: <paramref name="count"/> selects the form and fills <c>%{count}</c>.</summary>
    public string GetPlural(string key, long count, params ReadOnlySpan<(string Name, object? Value)> args)
    {
        var all = new (string Name, object? Value)[args.Length + 1];
        all[0] = ("count", count);
        args.CopyTo(all.AsSpan(1));
        return Get(key, all);
    }

    /// <summary>
    /// Like <see cref="Get(string, ReadOnlySpan{ValueTuple{string, object}})"/>, but says whether the key was
    /// found in the chosen language or in English. When it was not, <paramref name="value"/> is the key.
    /// </summary>
    public bool TryGet(string key, out string value, params ReadOnlySpan<(string Name, object? Value)> args)
    {
        ArgumentNullException.ThrowIfNull(key);
        var found = TryLookup(key, Language, args, out var raw);
        if (!found && Language != FallbackLanguage)
        {
            found = TryLookup(key, FallbackLanguage, args, out raw);
        }

        value = found ? Format(raw!, args) : key;
        return found;
    }

    /// <summary>
    /// Whether <paramref name="key"/> exists in <paramref name="language"/> itself (no fallback), as a string or
    /// as a plural node.
    /// </summary>
    public bool HasKey(string key, string language)
    {
        if (!SplitKey(key, out var ns, out var path))
        {
            return false;
        }

        var table = Table(ns, language);
        return table.ContainsKey(path) || IsPluralNode(table, path);
    }

    /// <summary>The flattened keys of one file (plural forms appear as <c>key.one</c>, <c>key.many</c>); empty when the file does not exist.</summary>
    public IReadOnlyCollection<string> KeysOf(string ns, string language) => Table(ns, language).Keys.ToArray();

    /// <summary>Whether a translation file exists for this namespace and language.</summary>
    public bool HasFile(string ns, string language) => Table(ns, language).Count > 0;

    /// <summary>
    /// Upstream's language choice: the setting wins unless it is empty or <c>auto</c>; otherwise the OS UI
    /// language. Only the part before the first <c>-</c> or <c>_</c> is kept, lower-cased, and "chinese" becomes
    /// <c>zh</c>. Anything outside <see cref="SupportedLanguages"/> becomes <c>en</c> (upstream would set the
    /// unknown locale and fall back to English for every key, which is the same result).
    /// </summary>
    public static string ResolveLanguage(string? setting, CultureInfo osUiCulture)
    {
        ArgumentNullException.ThrowIfNull(osUiCulture);
        var source = string.IsNullOrWhiteSpace(setting) || setting.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? osUiCulture.Name
            : setting.Trim();
        return Normalise(source);
    }

    /// <summary>Creates a translator for the <c>language</c> setting and the OS UI culture.</summary>
    public static Translator Create(string? languageSetting, CultureInfo osUiCulture) =>
        new(ResolveLanguage(languageSetting, osUiCulture));

    private static string Normalise(string locale)
    {
        var cut = locale.IndexOfAny(['-', '_']);
        var head = (cut >= 0 ? locale[..cut] : locale).ToLowerInvariant();
        if (head == "chinese")
        {
            head = "zh";
        }

        foreach (var supported in SupportedLanguages)
        {
            if (supported == head)
            {
                return supported;
            }
        }

        return FallbackLanguage;
    }

    private bool TryLookup(string key, string language, ReadOnlySpan<(string Name, object? Value)> args, out string? raw)
    {
        raw = null;
        if (!SplitKey(key, out var ns, out var path))
        {
            return false;
        }

        var table = Table(ns, language);
        if (table.TryGetValue(path, out var text))
        {
            // A plain string: count, if given, only fills the placeholder (python-i18n returns it as is).
            raw = text;
            return true;
        }

        if (!IsPluralNode(table, path))
        {
            return false;
        }

        // The node exists in this language, so there is no fallback to English from here on, as in python-i18n.
        if (!TryGetCount(args, out var count))
        {
            return false;
        }

        var preferred = count switch
        {
            0d => "zero",
            1d => "one",
            <= PluralFew => "few",
            _ => null,
        };
        if (preferred is not null && table.TryGetValue(path + "." + preferred, out text))
        {
            raw = text;
            return true;
        }

        if (table.TryGetValue(path + ".many", out text))
        {
            raw = text;
            return true;
        }

        // python-i18n returns the key when "many" is missing, without trying the fallback language.
        raw = key;
        return true;
    }

    /// <summary>
    /// python-i18n stores a mapping with at least two of zero/one/few/many as one plural value rather than
    /// recursing into it.
    /// </summary>
    private static bool IsPluralNode(IReadOnlyDictionary<string, string> table, string path)
    {
        var hits = 0;
        foreach (var plural in PluralKeys)
        {
            if (table.ContainsKey(path + "." + plural) && ++hits >= 2)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetCount(ReadOnlySpan<(string Name, object? Value)> args, out double count)
    {
        foreach (var (name, value) in args)
        {
            if (name != "count")
            {
                continue;
            }

            switch (value)
            {
                case null:
                    break;
                case string s:
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out count);
                case IConvertible convertible:
                    try
                    {
                        count = convertible.ToDouble(CultureInfo.InvariantCulture);
                        return true;
                    }
                    catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
                    {
                    }

                    break;
            }
        }

        count = 0;
        return false;
    }

    private static bool SplitKey(string key, out string ns, out string path)
    {
        var dot = key.IndexOf('.');
        if (dot <= 0 || dot == key.Length - 1)
        {
            ns = path = "";
            return false;
        }

        ns = key[..dot];
        path = key[(dot + 1)..];
        return true;
    }

    private IReadOnlyDictionary<string, string> Table(string ns, string language) =>
        _tables.GetOrAdd(
                (ns, language),
                static (k, open) => new Lazy<IReadOnlyDictionary<string, string>>(
                    () => LoadTable(open, k.Namespace, k.Language),
                    LazyThreadSafetyMode.ExecutionAndPublication),
                _open)
            .Value;

    private static IReadOnlyDictionary<string, string> LoadTable(Func<string, string, TextReader?> open, string ns, string language)
    {
        using var reader = open(ns, language);
        return reader is null ? Empty : TranslationFileLoader.Load(reader);
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>(StringComparer.Ordinal);

    private static TextReader? OpenEmbedded(string ns, string language)
    {
        // Namespaces are plain identifiers; anything else cannot name an embedded file.
        if (!IsIdentifier(ns) || !IsIdentifier(language))
        {
            return null;
        }

        var stream = typeof(Translator).Assembly.GetManifestResourceStream($"Strings/{ns}.{language}.yml");
        return stream is null ? null : new StreamReader(stream, Encoding.UTF8);
    }

    private static bool IsIdentifier(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return s.Length > 0;
    }

    /// <summary>Python <c>string.Template.safe_substitute</c> with <c>%</c> as the delimiter.</summary>
    internal static string Format(string template, ReadOnlySpan<(string Name, object? Value)> args)
    {
        var at = template.IndexOf('%');
        if (at < 0)
        {
            return template;
        }

        var sb = new StringBuilder(template.Length + 16);
        sb.Append(template, 0, at);
        var i = at;
        while (i < template.Length)
        {
            var c = template[i];
            if (c != '%' || i + 1 >= template.Length)
            {
                sb.Append(c);
                i++;
                continue;
            }

            var next = template[i + 1];
            if (next == '%')
            {
                sb.Append('%');
                i += 2;
                continue;
            }

            if (next == '{')
            {
                var close = template.IndexOf('}', i + 2);
                if (close > i + 2 && IsTemplateId(template.AsSpan(i + 2, close - i - 2))
                    && TryFind(args, template.AsSpan(i + 2, close - i - 2), out var value))
                {
                    sb.Append(value);
                    i = close + 1;
                    continue;
                }

                sb.Append(c);
                i++;
                continue;
            }

            var end = i + 1;
            if (end < template.Length && IsIdStart(template[end]))
            {
                end++;
                while (end < template.Length && IsIdPart(template[end]))
                {
                    end++;
                }

                if (TryFind(args, template.AsSpan(i + 1, end - i - 1), out var value))
                {
                    sb.Append(value);
                    i = end;
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static bool TryFind(ReadOnlySpan<(string Name, object? Value)> args, ReadOnlySpan<char> name, out string value)
    {
        foreach (var arg in args)
        {
            if (name.SequenceEqual(arg.Name))
            {
                value = Stringify(arg.Value);
                return true;
            }
        }

        value = "";
        return false;
    }

    private static string Stringify(object? value) => value switch
    {
        null => "None",
        bool b => b ? "True" : "False",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static bool IsTemplateId(ReadOnlySpan<char> id)
    {
        if (id.IsEmpty || !IsIdStart(id[0]))
        {
            return false;
        }

        foreach (var c in id[1..])
        {
            if (!IsIdPart(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsIdPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
