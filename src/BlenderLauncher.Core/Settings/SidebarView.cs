using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace BlenderLauncher.Core.Settings;

/// <summary>Which list a sidebar entry shows.</summary>
public enum SidebarSection
{
    /// <summary>The Library group: installed builds.</summary>
    Installed = 0,

    /// <summary>The Download group: available builds.</summary>
    Download = 1,
}

/// <summary>
/// A sidebar entry as stored in <see cref="AppearanceSettings.DefaultView"/>: <c>installed:&lt;key&gt;</c> or
/// <c>download:&lt;key&gt;</c>. Keys are lower-case channel folder names (<c>stable</c>, <c>daily</c>,
/// <c>upbge-weekly</c>…) or the installed-only entries <see cref="AllKey"/> and <see cref="FavoritesKey"/>. Which
/// channels exist is the sidebar's business; this type only checks the shape.
/// </summary>
public readonly partial record struct SidebarView(SidebarSection Section, string Key)
{
    /// <summary>The Library group's "All builds" entry.</summary>
    public const string AllKey = "all";

    /// <summary>The Library group's "Favorites" entry.</summary>
    public const string FavoritesKey = "favorites";

    /// <summary>The default view: all installed builds.</summary>
    public static SidebarView Default { get; } = new(SidebarSection.Installed, AllKey);

    public override string ToString() => (Section == SidebarSection.Installed ? "installed:" : "download:") + Key;

    /// <summary>Parses <c>installed:key</c> or <c>download:key</c>. "All" and "Favorites" exist only under installed.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out SidebarView? view)
    {
        view = null;
        if (text is null)
        {
            return false;
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return false;
        }

        SidebarSection section;
        switch (text[..colon])
        {
            case "installed":
                section = SidebarSection.Installed;
                break;
            case "download":
                section = SidebarSection.Download;
                break;
            default:
                return false;
        }

        var key = text[(colon + 1)..];
        if (!KeyPattern().IsMatch(key) || (section == SidebarSection.Download && key is AllKey or FavoritesKey))
        {
            return false;
        }

        view = new SidebarView(section, key);
        return true;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
