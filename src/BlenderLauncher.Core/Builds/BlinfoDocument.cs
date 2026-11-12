namespace BlenderLauncher.Core.Builds;

/// <summary>
/// The on-disk shape of a build folder's <c>.blinfo</c> file (upstream <c>build_info.py</c>).
/// Property names are written in snake_case by <see cref="Serialization.LauncherJsonContext"/>.
/// </summary>
public sealed class BlinfoDocument
{
    public string? FileVersion { get; set; }

    public List<BlinfoEntry> Blinfo { get; set; } = [];
}

/// <summary>
/// One entry of <see cref="BlinfoDocument.Blinfo"/>. The <c>link</c> is not stored; on read it is the folder path.
/// <see cref="CommitTime"/> stays a string here because upstream accepts several legacy formats on read.
/// A missing key takes upstream's <c>from_dict</c> default: <c>""</c> for the strings, including
/// <see cref="CustomExecutable"/>, and null for <see cref="BuildHash"/>. An explicit JSON null stays null.
/// </summary>
public sealed record BlinfoEntry
{
    public string? Branch { get; set; } = "";

    public string? Subversion { get; set; } = "";

    public string? BuildHash { get; set; }

    public string? CommitTime { get; set; }

    public string? CustomName { get; set; } = "";

    public bool IsFavorite { get; set; }

    public string? CustomExecutable { get; set; } = "";

    public bool IsFrozen { get; set; }
}
