using System.Text.RegularExpressions;

namespace BlenderLauncher.Core.Logging;

/// <summary>
/// Removes secrets from log text, as upstream's <c>SensitiveDataFilter</c> (<c>utils/logger.py</c>) does, and a
/// little more: fine-grained <c>github_pat_</c> tokens and the other <c>gh?_</c> prefixes, any
/// <c>Authorization</c> value (with or without quotes), and <c>token …</c> scheme values.
/// The scheme word before a value (<c>token</c>, <c>Bearer</c>, <c>Basic</c>) is kept, so the log still shows
/// which kind of credential was sent.
/// </summary>
public static partial class LogRedactor
{
    public const string Mask = "***REDACTED***";

    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        // Order matters: once a value is masked, the later patterns no longer match it.
        text = AuthorizationValue().Replace(text, "${prefix}" + Mask);
        text = BearerValue().Replace(text, "${prefix}" + Mask);
        text = TokenSchemeValue().Replace(text, "${prefix}" + Mask);
        text = FineGrainedToken().Replace(text, "github_pat_" + Mask);
        text = ClassicToken().Replace(text, "${kind}_" + Mask);
        return text;
    }

    // "Authorization: token abc", "'Authorization': 'Bearer abc'", "\"authorization\":\"abc\"",
    // "Authorization=Basic abc". The value runs to the closing quote, comma, brace or end of line.
    [GeneratedRegex(
        """(?<prefix>\bAuthorization['"]?\s*[:=]\s*['"]?(?:(?:token|bearer|basic)\s+)?)[^\s'",}][^'"\r\n,}]*""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationValue();

    // Upstream: (Bearer\s+)[a-zA-Z0-9_\-\.]{20,}. Shorter values are masked too, from 8 characters, so prose
    // such as "bearer auth" is left alone.
    [GeneratedRegex(@"(?<prefix>\bBearer\s+)[A-Za-z0-9_\-\.~+/=]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerValue();

    // The "token <value>" scheme GitHub accepts, outside an Authorization header (for example a logged header
    // dictionary value). Requires 20+ characters so "token expired" and similar prose stay readable.
    [GeneratedRegex(@"(?<prefix>\btoken\s+)[A-Za-z0-9_\-\.]{20,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenSchemeValue();

    [GeneratedRegex(@"\bgithub_pat_[A-Za-z0-9_]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex FineGrainedToken();

    // Upstream: \bghp_[a-zA-Z0-9]{36,}. Also the OAuth, user, server and refresh prefixes.
    [GeneratedRegex(@"\b(?<kind>gh[pousr])_[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex ClassicToken();
}
