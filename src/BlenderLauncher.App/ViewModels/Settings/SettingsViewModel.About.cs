using BlenderLauncher.Core;
using CommunityToolkit.Mvvm.Input;

namespace BlenderLauncher.App.ViewModels.Settings;

/// <summary>
/// Settings → About: version, commit, licence with the no-warranty notice, credits, links, the third-party
/// notices (GPL-3.0 section 5(d) asks an interactive program to show them), and the log and data folders.
/// </summary>
public sealed partial class SettingsViewModel
{
    public const string SourceUrl = "https://github.com/matty/blender-launcher";
    public const string LicenceUrl = "https://www.gnu.org/licenses/gpl-3.0.html";
    public const string DocumentationUrl = "https://victor-ix.github.io/Blender-Launcher-V2/";
    public const string UpstreamUrl = "https://github.com/Victor-IX/Blender-Launcher-V2";
    public const string OriginalUrl = "https://github.com/DotBow/Blender-Launcher";

    private string? _notices;

    public string AppName => AppIdentity.DisplayName;

    public string VersionText => AppIdentity.Version;

    /// <summary>The build's commit, shortened; null when the build had no source-control information.</summary>
    public string? CommitText => AppIdentity.Commit is { Length: > 0 } commit ? (commit.Length > 12 ? commit[..12] : commit) : null;

    public bool HasCommit => CommitText is not null;

    public string? DataFolder => _environment.Directories?.Root;

    public bool HasDataFolder => DataFolder is not null;

    public bool IsPortable => _environment.Directories?.IsPortable == true;

    /// <summary>THIRD-PARTY-NOTICES.md, embedded in the exe.</summary>
    public string ThirdPartyNotices => _notices ??= ReadNotices();

    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (!string.IsNullOrEmpty(url))
        {
            Host?.Open(url, isFolder: false);
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        if (DataFolder is { } folder)
        {
            Host?.Open(folder, isFolder: true);
        }
    }

    private static string ReadNotices()
    {
        using var stream = typeof(SettingsViewModel).Assembly.GetManifestResourceStream("THIRD-PARTY-NOTICES.md");
        if (stream is null)
        {
            return "THIRD-PARTY-NOTICES.md";
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
