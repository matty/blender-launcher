using System.Text.RegularExpressions;

namespace BlenderLauncher.Core.Updates;

/// <summary>
/// Names and URLs of a launcher release's files. Pure functions; <c>scripts/package-release.ps1</c>,
/// <c>release.yml</c> and the update check must agree with them.
/// </summary>
/// <remarks>
/// A release is built by Velopack (<c>vpk pack</c>). The installed and portable copies update from
/// <see cref="VelopackFeedName"/> and the <c>.nupkg</c> files it lists, which keep Velopack's own names. The two files
/// people download are renamed to <see cref="SetupName"/> and <see cref="PortableZipName"/>; the portable zip keeps
/// upstream's <c>Blender_Launcher_{tag}_Windows_x64</c> stem, so winget-style <c>installers-regex</c> rules and the
/// update check's "release with a Windows zip" rule keep working.
/// </remarks>
public static partial class ReleaseAssets
{
    /// <summary>Velopack's package id. Also its install folder under <c>%LOCALAPPDATA%</c>, deleted on uninstall: the
    /// launcher's own folder (<see cref="AppIdentity.DataFolderName"/>), whose <c>data</c> subfolder holds the data. Never
    /// upstream's <c>Blender Launcher</c>.</summary>
    public const string VelopackPackId = AppIdentity.DataFolderName;

    /// <summary>Velopack's release feed for the default Windows channel.</summary>
    public const string VelopackFeedName = "releases.win.json";

    /// <summary>The installer for <paramref name="tag"/> (as tagged, for example <c>v1.2.0</c>).</summary>
    public static string SetupName(string tag) => $"Blender_Launcher_{CheckTag(tag)}_Windows_x64_Setup.exe";

    /// <summary>The portable zip (Velopack's portable package) for <paramref name="tag"/>.</summary>
    public static string PortableZipName(string tag) => $"Blender_Launcher_{CheckTag(tag)}_Windows_x64_Portable.zip";

    /// <summary>The release page, which the update dialog links to when it cannot update in place.</summary>
    public static Uri ReleasePageUrl(string repo, string tag) =>
        new($"https://github.com/{CheckRepo(repo)}/releases/tag/{Uri.EscapeDataString(CheckTag(tag))}");

    /// <summary><c>https://github.com/{repo}</c>, the URL Velopack's GitHub source takes.</summary>
    public static string RepositoryUrl(string repo) => $"https://github.com/{CheckRepo(repo)}";

    /// <summary>True for <c>owner/name</c> as GitHub allows them.</summary>
    public static bool IsValidRepo(string? repo) => repo is not null && RepoPattern().IsMatch(repo);

    private static string CheckRepo(string repo) =>
        IsValidRepo(repo) ? repo : throw new ArgumentException($"'{repo}' is not a GitHub repository as owner/name.", nameof(repo));

    private static string CheckTag(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        if (tag.AsSpan().IndexOfAny("/\\?#%: ") >= 0)
        {
            throw new ArgumentException($"'{tag}' is not a usable release tag.", nameof(tag));
        }

        return tag;
    }

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoPattern();
}
