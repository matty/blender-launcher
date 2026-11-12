using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Library;

/// <summary>Whether a build keeps its settings in its own folder.</summary>
public enum PortableState
{
    /// <summary>Neither the portable folder nor its disabled form exists.</summary>
    None,

    /// <summary>The portable folder exists: the build is portable.</summary>
    Portable,

    /// <summary>Only the <c>_</c>-prefixed (disabled) form exists.</summary>
    Disabled,
}

/// <summary>Where "Show Config Folder" should open (<c>library_widget.py:1094-1149</c>).</summary>
public enum ConfigFolderKind
{
    /// <summary>The build's portable folder.</summary>
    Portable,

    /// <summary>The user config folder for the build's version.</summary>
    Version,

    /// <summary>
    /// The version folder does not exist; <see cref="ConfigFolderTarget.Path"/> is the general config folder,
    /// which upstream offers to open instead ("Open General Config Folder" / "Cancel").
    /// </summary>
    GeneralFallback,
}

/// <summary>The result of <see cref="BuildConfigFolders.ResolveConfigFolder"/>.</summary>
public sealed record ConfigFolderTarget(ConfigFolderKind Kind, string Path, string? MissingVersionPath = null);

/// <summary>Why <see cref="BuildConfigFolders.CopyPortableSettings"/> did not copy.</summary>
public enum PortableCopyResult
{
    /// <summary>Copied.</summary>
    Copied,

    /// <summary>The old build has no portable folder (upstream <c>msg.err.port.no_settings</c>).</summary>
    NoSettings,

    /// <summary>The new build's folder for it is missing (upstream <c>msg.err.port.no_build_dir</c>).</summary>
    NoBuildFolder,
}

/// <summary>Upstream's fork config folders (<c>build_info.py:39-52</c>).</summary>
public sealed record ForkConfigFolder(string ConfigFolder, string ConfigSubfolder);

/// <summary>
/// A build's config folders: its portable folder (and the <c>_</c>-prefixed disabled form), make and unmake
/// portable, the per-user config folder with the Bforartists and UPBGE mapping, and copying portable settings to
/// a replacement build. Ports <c>library_widget.py:672-707, 1079-1149</c>, <c>platform_utils.py:311-332</c>,
/// <c>build_info.py:39-52, 801-835</c> and <c>download_widget.py:417-450</c>. Windows paths only.
/// </summary>
public static class BuildConfigFolders
{
    /// <summary>Prefix that disables a portable folder.</summary>
    public const string DisabledPrefix = "_";

    /// <summary>The default vendor folder and subfolder under <c>%APPDATA%</c>.</summary>
    public static ForkConfigFolder BlenderConfig { get; } = new("Blender Foundation", "blender");

    /// <summary>
    /// Upstream's <c>get_fork_config_paths</c>: the first fork whose name starts <paramref name="branch"/>
    /// (<c>bforartists</c> → <c>bforartists\bforartists</c>; <c>upbge…</c> → <c>UPBGE\Blender</c>, the Windows
    /// subfolder), else null.
    /// </summary>
    public static ForkConfigFolder? ForkConfigOf(string branch)
    {
        if (branch.StartsWith("bforartists", StringComparison.Ordinal))
        {
            return new("bforartists", "bforartists");
        }

        if (branch.StartsWith("upbge", StringComparison.Ordinal))
        {
            return new("UPBGE", "Blender");
        }

        return null;
    }

    /// <summary>
    /// Upstream's <c>make_portable_path</c>: <c>&lt;build&gt;\portable</c> for Bforartists 4.1 and later, UPBGE
    /// 0.42 and later and anything else 4.2 and later; otherwise <c>&lt;build&gt;\&lt;M.m&gt;\config</c>. The
    /// version is compared as numbers, where upstream compared the text (so "10.0" would have counted as older
    /// than "4.2").
    /// </summary>
    public static string PortablePath(BuildInfo build)
    {
        var folder = build.Link;
        var (major, minor, text) = MajorMinor(build);
        var branch = build.Branch;
        bool usesPortable;
        if (branch == "bforartists")
        {
            usesPortable = AtLeast(major, minor, 4, 1);
        }
        else if (branch.Contains("upbge", StringComparison.Ordinal))
        {
            usesPortable = AtLeast(major, minor, 0, 42);
        }
        else
        {
            usesPortable = AtLeast(major, minor, 4, 2);
        }

        return usesPortable ? Path.Combine(folder, "portable") : Path.Combine(folder, text, "config");
    }

    /// <summary>The disabled form of a portable path: the same folder with a <c>_</c> prefix.</summary>
    public static string DisabledPathOf(string portablePath) =>
        Path.Combine(Path.GetDirectoryName(portablePath)!, DisabledPrefix + Path.GetFileName(portablePath));

    /// <summary>Whether the build is portable, disabled, or neither.</summary>
    public static PortableState GetPortableState(BuildInfo build)
    {
        var path = PortablePath(build);
        return Directory.Exists(path) ? PortableState.Portable
            : Directory.Exists(DisabledPathOf(path)) ? PortableState.Disabled
            : PortableState.None;
    }

    /// <summary>
    /// Upstream's <c>make_portable</c> toggle. Portable: the folder is renamed to its disabled form. Not portable:
    /// the disabled form is renamed back, or else an empty portable folder is created (its parent must exist, as
    /// upstream's <c>mkdir(parents=False)</c>). Renames retry while the folder is locked. Returns true when the
    /// build is now portable.
    /// </summary>
    public static async Task<bool> TogglePortableAsync(BuildInfo build, CancellationToken ct = default)
    {
        var path = PortablePath(build);
        var disabled = DisabledPathOf(path);
        if (Directory.Exists(path))
        {
            await RetryOnLockAsync(() => Directory.Move(path, disabled), ct).ConfigureAwait(false);
            return false;
        }

        if (Directory.Exists(disabled))
        {
            await RetryOnLockAsync(() => Directory.Move(disabled, path), ct).ConfigureAwait(false);
        }
        else
        {
            var parent = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(parent))
            {
                throw new DirectoryNotFoundException($"Cannot create {path}: {parent} does not exist");
            }

            Directory.CreateDirectory(path);
        }

        return true;
    }

    /// <summary>
    /// Upstream's <c>get_blender_config_folder</c> on Windows: <c>&lt;appData&gt;\&lt;vendor&gt;\&lt;subfolder&gt;</c>,
    /// with the fork mapping of <see cref="ForkConfigOf"/> (default <c>Blender Foundation\blender</c>).
    /// <paramref name="appDataRoot"/> is <c>%APPDATA%</c>.
    /// </summary>
    public static string GeneralConfigFolder(string branch, string appDataRoot)
    {
        var config = ForkConfigOf(branch) ?? BlenderConfig;
        return Path.Combine(appDataRoot, config.ConfigFolder, config.ConfigSubfolder);
    }

    /// <summary>
    /// The version folder name used under <see cref="GeneralConfigFolder"/>: <c>M.m</c> of the build, mapped to the
    /// Blender version for Bforartists and UPBGE through <paramref name="series"/> (the API's series list, newest
    /// first). Empty when the version is unknown, as upstream, which then points at the general folder.
    /// </summary>
    public static string ConfigVersionOf(BuildInfo build, IReadOnlyList<BlenderSeries> series)
    {
        if (build.SemVersion is not { } version)
        {
            return "";
        }

        SemVer? mapped = version;
        if (ForkConfigOf(build.Branch) is not null)
        {
            if (build.Branch == "bforartists")
            {
                mapped = BforartistsBlenderVersion(version, series);
            }
            else if (build.Branch.StartsWith("upbge", StringComparison.Ordinal))
            {
                mapped = UpbgeBlenderVersion(version, series);
            }
        }

        return mapped is { } v ? $"{v.Major}.{v.Minor}" : "";
    }

    /// <summary>
    /// Upstream's <c>show_config_folder</c> choice: the portable folder when it exists and
    /// <paramref name="preferUserFolder"/> (Shift) is not set; else the user config folder for the version, or the
    /// general folder as <see cref="ConfigFolderKind.GeneralFallback"/> when that does not exist.
    /// </summary>
    public static ConfigFolderTarget ResolveConfigFolder(
        BuildInfo build,
        string appDataRoot,
        IReadOnlyList<BlenderSeries> series,
        bool preferUserFolder = false)
    {
        var portable = PortablePath(build);
        if (Directory.Exists(portable) && !preferUserFolder)
        {
            return new(ConfigFolderKind.Portable, portable);
        }

        var general = GeneralConfigFolder(build.Branch, appDataRoot);
        var version = ConfigVersionOf(build, series);
        var path = version.Length == 0 ? general : Path.Combine(general, version);
        return Directory.Exists(path)
            ? new(ConfigFolderKind.Version, path)
            : new(ConfigFolderKind.GeneralFallback, general, path);
    }

    /// <summary>
    /// Upstream's <c>bfa_version_matcher</c>: Bforartists M.m maps to the series one newer than Blender M.m in
    /// <paramref name="series"/> (newest first). Null when M.m is not listed, is the newest, or is the oldest
    /// (upstream's bounds check excludes both ends).
    /// </summary>
    public static SemVer? BforartistsBlenderVersion(SemVer version, IReadOnlyList<BlenderSeries> series)
    {
        for (var i = 0; i < series.Count; i++)
        {
            if (series[i].Major == version.Major && series[i].Minor == version.Minor)
            {
                return i + 1 < series.Count && i > 0 ? new SemVer(series[i - 1].Major, series[i - 1].Minor) : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Upstream's <c>upbge_version_matcher</c>: the UPBGE minor's digits give the Blender version (two digits
    /// <c>ab</c> → a.b; three digits <c>abc</c> → ab.c, exactly as upstream slices them), which must be in
    /// <paramref name="series"/>; else null.
    /// </summary>
    public static SemVer? UpbgeBlenderVersion(SemVer version, IReadOnlyList<BlenderSeries> series)
    {
        var digits = version.Minor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int major, minor;
        if (digits.Length == 3)
        {
            (major, minor) = (int.Parse(digits[..2], System.Globalization.CultureInfo.InvariantCulture), digits[2] - '0');
        }
        else if (digits.Length == 2)
        {
            (major, minor) = (digits[0] - '0', digits[1] - '0');
        }
        else
        {
            return null;
        }

        return series.Any(s => s.Major == major && s.Minor == minor) ? new SemVer(major, minor) : null;
    }

    /// <summary>
    /// Upstream's <c>handle_portable_settings</c>: copies the old build's portable folder over the new build's
    /// (merging into an existing folder). The old folder is left for the caller, which removes the old build.
    /// </summary>
    public static async Task<PortableCopyResult> CopyPortableSettingsAsync(BuildInfo oldBuild, BuildInfo newBuild, CancellationToken ct = default)
    {
        var from = PortablePath(oldBuild);
        if (!Directory.Exists(from))
        {
            return PortableCopyResult.NoSettings;
        }

        var to = PortablePath(newBuild);
        if (!Directory.Exists(Path.GetDirectoryName(to)))
        {
            return PortableCopyResult.NoBuildFolder;
        }

        await Task.Run(() => FolderCopy.CopyTree(from, to, overwrite: true, progress: null, ct), ct).ConfigureAwait(false);
        return PortableCopyResult.Copied;
    }

    private static bool AtLeast(int major, int minor, int wantMajor, int wantMinor) =>
        major > wantMajor || (major == wantMajor && minor >= wantMinor);

    // Major and minor of the build, plus the version folder text. Upstream takes subversion.rsplit(".", 1)[0];
    // the parsed M.m is used when the subversion parses, which gives the same folder for real version strings.
    private static (int Major, int Minor, string Text) MajorMinor(BuildInfo build)
    {
        if (build.SemVersion is { } v)
        {
            return (v.Major, v.Minor, $"{v.Major}.{v.Minor}");
        }

        var dot = build.Subversion.LastIndexOf('.');
        var text = dot < 0 ? build.Subversion : build.Subversion[..dot];
        return (0, 0, text);
    }

    internal static async Task RetryOnLockAsync(Action action, CancellationToken ct, int attempts = 10, int delayMs = 500)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when (attempt < attempts && e is UnauthorizedAccessException or IOException and not DirectoryNotFoundException and not FileNotFoundException)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }
    }
}
