using System.Globalization;
using System.Text;

namespace BlenderLauncher.Core.Settings.Upstream;

/// <summary>What an INI value turned out to be.</summary>
public enum IniValueKind
{
    /// <summary>A single string.</summary>
    String,

    /// <summary>A comma-separated list (commas outside quotes).</summary>
    List,

    /// <summary>A Qt variant this reader does not decode (<c>@ByteArray(…)</c>, <c>@Variant(…)</c>, <c>@Rect(…)</c>…).</summary>
    Skipped,
}

/// <summary>One value from a QSettings INI file.</summary>
public sealed record IniValue(IniValueKind Kind, IReadOnlyList<string> Items, string Raw)
{
    /// <summary>
    /// The value as text: the string, or a list's items joined with <c>", "</c> (a hand-edited JSON value with
    /// unquoted commas reads back intact that way). Null for a skipped variant.
    /// </summary>
    public string? Text => Kind switch
    {
        IniValueKind.String => Items[0],
        IniValueKind.List => string.Join(", ", Items),
        _ => null,
    };

    /// <summary>
    /// The value as Qt's <c>QVariant::toBool</c> reads a string: false for empty, <c>0</c> and <c>false</c> (any
    /// case), true otherwise. Null for a list or a skipped variant.
    /// </summary>
    public bool? AsBool()
    {
        if (Kind != IniValueKind.String)
        {
            return null;
        }

        var s = Items[0].Trim();
        return !(s.Length == 0 || s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The value as a decimal integer (surrounding spaces allowed), or null.</summary>
    public int? AsInt() =>
        Kind == IniValueKind.String && int.TryParse(Items[0].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
}

/// <summary>
/// A QSettings INI file, read only. Keys are QSettings keys: <c>[General]</c> entries have no prefix, an entry
/// <c>b</c> in section <c>[a]</c> is <c>a/b</c>, and a <c>\</c> inside a key is a <c>/</c>. Lookups ignore case,
/// as QSettings does on Windows. A repeated key keeps its last value.
/// </summary>
public sealed class QSettingsIni
{
    private readonly Dictionary<string, IniValue> _values;
    private readonly List<string> _order;

    internal QSettingsIni(Dictionary<string, IniValue> values, List<string> order)
    {
        _values = values;
        _order = order;
    }

    /// <summary>Every key, in the order first seen.</summary>
    public IReadOnlyList<string> Keys => _order;

    public int Count => _values.Count;

    public bool Contains(string key) => _values.ContainsKey(key);

    public IniValue? Get(string key) => _values.GetValueOrDefault(key);

    /// <summary>The text of <paramref name="key"/> (<see cref="IniValue.Text"/>), or null when absent or skipped.</summary>
    public string? GetString(string key) => Get(key)?.Text;

    public static QSettingsIni Parse(string text) => QSettingsIniReader.Parse(text);

    public static QSettingsIni Load(string path) => QSettingsIniReader.Parse(File.ReadAllText(path, Encoding.UTF8));
}

/// <summary>
/// Reads Qt's INI dialect as written by <c>QSettings(…, IniFormat)</c>.
/// <list type="bullet">
/// <item>Lines starting with <c>;</c> or <c>#</c> are comments; a <c>;</c> outside quotes ends a value.</item>
/// <item>A line ending in an odd number of backslashes continues on the next line.</item>
/// <item>Section and key names are percent-decoded (<c>%XX</c>, <c>%UXXXX</c>); <c>[%General]</c> is a real section
/// called General.</item>
/// <item>Values: surrounding spaces trimmed; <c>"…"</c> quotes keep spaces, commas and semicolons; escapes
/// <c>\a \b \f \n \r \t \v \" \' \? \\ \;  \,</c>, <c>\xH…</c> (up to four hex digits) and <c>\ooo</c> (octal);
/// commas outside quotes make a list.</item>
/// <item>Variants: <c>@@</c> at the start is a literal <c>@</c>; <c>@String(…)</c> is the string; every other
/// <c>@Type(…)</c> (<c>@ByteArray</c>, <c>@Variant</c>, <c>@Rect</c>, <c>@Size</c>, <c>@Point</c>, <c>@DateTime</c>,
/// <c>@Invalid</c>) is <see cref="IniValueKind.Skipped"/>.</item>
/// </list>
/// Lines that are neither a section nor <c>key=value</c> are ignored.
/// </summary>
public static class QSettingsIniReader
{
    private static readonly string[] VariantTypes = ["ByteArray", "Variant", "Rect", "Size", "Point", "DateTime", "Invalid", "RectF", "SizeF", "PointF"];

    public static QSettingsIni Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var values = new Dictionary<string, IniValue>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var section = "";
        foreach (var raw in LogicalLines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                var close = line.IndexOf(']', StringComparison.Ordinal);
                var name = close > 0 ? line[1..close].Trim() : line[1..].Trim();
                section = name.Equals("General", StringComparison.OrdinalIgnoreCase) ? ""
                    : name.Equals("%General", StringComparison.OrdinalIgnoreCase) ? "General"
                    : UnescapeKey(name);
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = UnescapeKey(line[..eq].Trim());
            if (key.Length == 0)
            {
                continue;
            }

            var fullKey = section.Length == 0 ? key : section + "/" + key;
            var rawValue = line[(eq + 1)..];
            if (!values.ContainsKey(fullKey))
            {
                order.Add(fullKey);
            }

            values[fullKey] = ParseValue(rawValue);
        }

        return new QSettingsIni(values, order);
    }

    /// <summary>Decodes one value (the text after <c>=</c>).</summary>
    public static IniValue ParseValue(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var items = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var isList = false;

        // Unquoted characters are collected with their positions so trailing spaces of an item can be trimmed
        // without touching quoted ones.
        var protectedLength = 0;
        var i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                i++;
                if (!inQuotes)
                {
                    protectedLength = current.Length;
                }

                continue;
            }

            if (c == '\\')
            {
                i = ReadEscape(raw, i, current);
                protectedLength = current.Length;
                continue;
            }

            if (!inQuotes)
            {
                if (c == ';')
                {
                    break;
                }

                if (c == ',')
                {
                    items.Add(TrimItem(current, protectedLength));
                    current.Clear();
                    protectedLength = 0;
                    isList = true;
                    i++;
                    continue;
                }

                if (char.IsWhiteSpace(c) && current.Length == 0)
                {
                    i++;
                    continue;
                }
            }

            current.Append(c);
            if (inQuotes)
            {
                protectedLength = current.Length;
            }

            i++;
        }

        items.Add(TrimItem(current, protectedLength));
        if (isList)
        {
            return new IniValue(IniValueKind.List, items, raw);
        }

        var single = items[0];
        if (single.StartsWith('@'))
        {
            if (single.StartsWith("@@", StringComparison.Ordinal))
            {
                return new IniValue(IniValueKind.String, [single[1..]], raw);
            }

            if (single.StartsWith("@String(", StringComparison.Ordinal) && single.EndsWith(')'))
            {
                return new IniValue(IniValueKind.String, [single["@String(".Length..^1]], raw);
            }

            foreach (var type in VariantTypes)
            {
                if (single.Length > type.Length + 1 && single.AsSpan(1).StartsWith(type, StringComparison.Ordinal) && single[type.Length + 1] == '(')
                {
                    return new IniValue(IniValueKind.Skipped, [single], raw);
                }
            }
        }

        return new IniValue(IniValueKind.String, [single], raw);
    }

    private static string TrimItem(StringBuilder item, int protectedLength)
    {
        var end = item.Length;
        while (end > protectedLength && char.IsWhiteSpace(item[end - 1]))
        {
            end--;
        }

        return item.ToString(0, end);
    }

    /// <summary>Reads the escape at <paramref name="i"/> (a backslash) into <paramref name="sb"/>; returns the next index.</summary>
    private static int ReadEscape(string s, int i, StringBuilder sb)
    {
        if (i + 1 >= s.Length)
        {
            return i + 1;
        }

        var c = s[i + 1];
        switch (c)
        {
            case 'a': sb.Append('\a'); return i + 2;
            case 'b': sb.Append('\b'); return i + 2;
            case 'f': sb.Append('\f'); return i + 2;
            case 'n': sb.Append('\n'); return i + 2;
            case 'r': sb.Append('\r'); return i + 2;
            case 't': sb.Append('\t'); return i + 2;
            case 'v': sb.Append('\v'); return i + 2;
            case 'x':
            {
                var j = i + 2;
                var value = 0;
                while (j < s.Length && j < i + 6 && Uri.IsHexDigit(s[j]))
                {
                    value = (value * 16) + Convert.ToInt32(s[j].ToString(), 16);
                    j++;
                }

                if (j > i + 2)
                {
                    sb.Append((char)value);
                }

                return j;
            }

            case >= '0' and <= '7':
            {
                var j = i + 1;
                var value = 0;
                while (j < s.Length && j < i + 4 && s[j] is >= '0' and <= '7')
                {
                    value = (value * 8) + (s[j] - '0');
                    j++;
                }

                sb.Append((char)value);
                return j;
            }

            default:
                // \" \' \? \\ \; \, and anything else: the character itself.
                sb.Append(c);
                return i + 2;
        }
    }

    /// <summary>Percent-decodes a key or section name and turns <c>\</c> into <c>/</c>.</summary>
    private static string UnescapeKey(string key)
    {
        var sb = new StringBuilder(key.Length);
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (c == '%')
            {
                if (i + 5 < key.Length &&key[i + 1] == 'U' && TryHex(key.AsSpan(i + 2, 4), out var u))
                {
                    sb.Append((char)u);
                    i += 5;
                    continue;
                }

                if (i + 2 < key.Length && TryHex(key.AsSpan(i + 1, 2), out var b))
                {
                    sb.Append((char)b);
                    i += 2;
                    continue;
                }
            }

            sb.Append(c == '\\' ? '/' : c);
        }

        return sb.ToString();
    }

    private static bool TryHex(ReadOnlySpan<char> s, out int value) =>
        int.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);

    /// <summary>Splits into lines, joining a line that ends in an odd number of backslashes to the next.</summary>
    private static IEnumerable<string> LogicalLines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var pending = new StringBuilder();
        foreach (var line in lines)
        {
            var trailing = 0;
            for (var k = line.Length - 1; k >= 0 && line[k] == '\\'; k--)
            {
                trailing++;
            }

            if (trailing % 2 == 1)
            {
                pending.Append(line, 0, line.Length - 1);
                continue;
            }

            pending.Append(line);
            yield return pending.ToString();
            pending.Clear();
        }

        if (pending.Length > 0)
        {
            yield return pending.ToString();
        }
    }
}
