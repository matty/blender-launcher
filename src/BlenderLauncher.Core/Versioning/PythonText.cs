using System.Text;

namespace BlenderLauncher.Core.Versioning;

/// <summary>The few Python string methods whose exact behaviour upstream's display text depends on.</summary>
internal static class PythonText
{
    /// <summary>
    /// <c>str.title()</c>: a cased letter is upper-cased when it follows an uncased character (or starts the
    /// string) and lower-cased otherwise, so <c>"npr-prototype"</c> gives <c>"Npr-Prototype"</c> and
    /// <c>"rc2b"</c> gives <c>"Rc2B"</c>.
    /// </summary>
    public static string Title(string s)
    {
        var sb = new StringBuilder(s.Length);
        var previousCased = false;
        foreach (var c in s)
        {
            var cased = char.IsUpper(c) || char.IsLower(c);
            sb.Append(cased ? (previousCased ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)) : c);
            previousCased = cased;
        }

        return sb.ToString();
    }

    /// <summary>
    /// <c>str.casefold()</c>, approximated by invariant lower-casing. The differences (for example German
    /// sharp s) do not occur in version text.
    /// </summary>
    public static string CaseFold(string s) => s.ToLowerInvariant();
}
