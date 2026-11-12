using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.Core.Library;

/// <summary>Why a folder cannot be used as a library.</summary>
public enum LibraryFolderProblem
{
    /// <summary>No path was given.</summary>
    Empty,

    /// <summary>The path is not a valid path.</summary>
    InvalidPath,

    /// <summary>Nothing exists at the path (upstream requires it to exist).</summary>
    DoesNotExist,

    /// <summary>A file, not a folder, is at the path.</summary>
    IsAFile,

    /// <summary>The write probe could not create, write or delete its file.</summary>
    NotWritable,

    /// <summary>
    /// Inside the folder Velopack replaces on update or deletes on uninstall (<see cref="AppLocation.VolatileDirectory"/>),
    /// so the builds would be lost.
    /// </summary>
    InsideAppFolder,
}

/// <summary>Advice about a usable library folder (upstream's documentation and wizard rules).</summary>
public enum LibraryFolderWarning
{
    /// <summary>Under a UAC-protected folder such as <c>C:\Program Files</c> (<c>library_folder.md:3-5</c>).</summary>
    ProtectedLocation,

    /// <summary>
    /// Looks like a Blender Version Manager root: builds sit directly in it rather than in the branch folders
    /// (<c>installation.md:3-5</c>).
    /// </summary>
    BlenderVersionManagerRoot,
}

/// <summary>The outcome of <see cref="LibraryLayout.Validate"/>.</summary>
public sealed record LibraryFolderValidation(
    string? FullPath,
    LibraryFolderProblem? Problem,
    IReadOnlyList<LibraryFolderWarning> Warnings,
    Exception? Error = null)
{
    /// <summary>True when the folder can be used (warnings allowed).</summary>
    public bool IsValid => Problem is null;
}

/// <summary>
/// A library's folder set: the branch folders, <c>template</c>, <c>.temp</c>,
/// and the port's own staging folder <c>.temp\BlenderLauncherNet</c>. A port of
/// <c>settings.py:80-95, 181-222</c>, with the write probe moved into the port's staging folder so that it never
/// touches what upstream keeps in <c>.temp</c>.
/// </summary>
public sealed class LibraryLayout
{
    /// <summary>Name of the shared temporary folder at the library root.</summary>
    public const string TempFolderName = ".temp";

    /// <summary>The port's staging folder inside <see cref="TempFolderName"/>; the only part of it the port purges.</summary>
    public const string StagingFolderName = AppIdentity.TechnicalId;

    /// <summary>Template folder name.</summary>
    public const string TemplateFolderName = "template";

    /// <summary>Custom builds folder name.</summary>
    public const string CustomFolderName = "custom";

    /// <summary>The probe file upstream writes, written here inside the staging folder instead.</summary>
    public const string ProbeFileName = "tempfile_checking_write_perms";

    /// <summary>Upstream's <c>build_library_folders</c>: the folders scanned for builds, in its order.</summary>
    public static IReadOnlyList<string> BuildFolders { get; } =
        ["stable", "daily", "experimental", "bforartists", "upbge-stable", "upbge-weekly", "custom"];

    /// <summary>
    /// Parent folder names whose children are drawn as builds (<c>window.py:758-771</c>). Includes <c>lts</c>,
    /// which upstream accepts although it never creates or scans it.
    /// </summary>
    public static IReadOnlyList<string> AcceptedParents { get; } =
        ["stable", "lts", "daily", "experimental", "bforartists", "upbge-stable", "upbge-weekly", "custom"];

    /// <summary>Creates the layout for <paramref name="root"/>, made absolute and normalised.</summary>
    public LibraryLayout(string root)
    {
        Root = NormaliseRoot(root);
    }

    /// <summary>The library root, absolute, without a trailing separator.</summary>
    public string Root { get; }

    /// <summary><c>&lt;root&gt;\.temp</c>, shared with upstream (which may purge it at start-up).</summary>
    public string TempFolder => Path.Combine(Root, TempFolderName);

    /// <summary><c>&lt;root&gt;\.temp\BlenderLauncherNet</c>: the port's downloads and staging.</summary>
    public string StagingFolder => Path.Combine(Root, TempFolderName, StagingFolderName);

    /// <summary>
    /// True when <paramref name="folder"/> has the shape of a staging folder (<c>…\.temp\BlenderLauncherNet</c>),
    /// so the purge never empties anything else.
    /// </summary>
    public static bool IsStagingFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var parent = Path.GetDirectoryName(full);
        return string.Equals(Path.GetFileName(full), StagingFolderName, StringComparison.OrdinalIgnoreCase)
               && parent is not null
               && string.Equals(Path.GetFileName(parent), TempFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>&lt;root&gt;\template</c>.</summary>
    public string TemplateFolder => Path.Combine(Root, TemplateFolderName);

    /// <summary><c>&lt;root&gt;\custom</c>.</summary>
    public string CustomFolder => Path.Combine(Root, CustomFolderName);

    /// <summary><c>&lt;root&gt;\&lt;name&gt;</c> for a library subfolder name.</summary>
    public string FolderOf(string name) => Path.Combine(Root, name);

    /// <summary>
    /// True when <paramref name="name"/> is an accepted parent of builds (see <see cref="AcceptedParents"/>).
    /// Case-sensitive, as upstream compares the folder name with <c>in</c>.
    /// </summary>
    public static bool IsAcceptedParent(string name) => AcceptedParents.Contains(name, StringComparer.Ordinal);

    /// <summary>
    /// Upstream's write probe (<c>is_library_folder_valid</c>): writes and deletes an empty probe file in the staging
    /// folder, then removes the staging folder and <c>.temp</c> again when the probe created them, so a library holds
    /// only the folders something has needed. Returns null when it worked, else the exception. Requires the root to
    /// exist.
    /// </summary>
    /// <remarks>
    /// Upstream's <c>create_library_folders</c> is deliberately not ported: each folder (branch folders,
    /// <c>custom</c>, <c>template</c>, staging) is created by whatever first writes into it.
    /// </remarks>
    public Exception? ProbeWritable()
    {
        var created = new List<string>();
        try
        {
            foreach (var folder in new[] { TempFolder, StagingFolder })
            {
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                    created.Add(folder);
                }
            }

            var probe = Path.Combine(StagingFolder, ProbeFileName);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return e;
        }
        finally
        {
            // Innermost first; only folders the probe made, and only while still empty.
            for (var i = created.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(created[i]).Any())
                    {
                        Directory.Delete(created[i]);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Checks <paramref name="path"/> as a library folder: upstream's rule (it exists and the write probe works),
    /// plus advice from upstream's documentation as warnings. A relative path is resolved against
    /// <paramref name="baseDirectory"/> (upstream: the exe folder). <paramref name="protectedRoots"/> defaults to
    /// <see cref="DefaultProtectedRoots"/>. The probe leaves no folders behind (see <see cref="ProbeWritable"/>).
    /// </summary>
    public static LibraryFolderValidation Validate(string? path, string baseDirectory, IEnumerable<string>? protectedRoots = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new(null, LibraryFolderProblem.Empty, []);
        }

        string full;
        try
        {
            full = Resolve(path, baseDirectory);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(null, LibraryFolderProblem.InvalidPath, [], e);
        }

        if (File.Exists(full))
        {
            return new(full, LibraryFolderProblem.IsAFile, []);
        }

        if (IsInsideAppFolder(full))
        {
            return new(full, LibraryFolderProblem.InsideAppFolder, []);
        }

        if (!Directory.Exists(full))
        {
            return new(full, LibraryFolderProblem.DoesNotExist, []);
        }

        var warnings = new List<LibraryFolderWarning>();
        if (IsUnderAny(full, protectedRoots ?? DefaultProtectedRoots()))
        {
            warnings.Add(LibraryFolderWarning.ProtectedLocation);
        }

        if (LooksLikeVersionManagerRoot(full))
        {
            warnings.Add(LibraryFolderWarning.BlenderVersionManagerRoot);
        }

        var error = new LibraryLayout(full).ProbeWritable();
        return error is null
            ? new(full, null, warnings)
            : new(full, LibraryFolderProblem.NotWritable, warnings, error);
    }

    /// <summary>
    /// Resolves a library path as upstream's <c>get_library_folder</c> does: a relative path is taken against
    /// <paramref name="baseDirectory"/>; the result is absolute and normalised (see <see cref="NormaliseRoot"/>).
    /// </summary>
    public static string Resolve(string path, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var expanded = path.Trim();
        return NormaliseRoot(Path.IsPathFullyQualified(expanded) ? expanded : Path.Combine(Path.GetFullPath(baseDirectory), expanded));
    }

    /// <summary>
    /// Absolute path with <c>.</c> and <c>..</c> removed and no trailing separator (a drive root keeps its own).
    /// Case is kept; compare with <see cref="PathComparer"/>.
    /// </summary>
    public static string NormaliseRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        return trimmed.Length == 0 ? full : trimmed;
    }

    /// <summary>File-system path comparison: case-insensitive on Windows and macOS, ordinal elsewhere.</summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The <see cref="StringComparison"/> matching <see cref="PathComparer"/>.</summary>
    public static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// UAC-protected locations: Program Files (both), Common Program Files (both) and the Windows folder; on
    /// other systems the system binary folders. Empty entries (folders the OS does not have) are left out.
    /// </summary>
    public static IReadOnlyList<string> DefaultProtectedRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ["/bin", "/sbin", "/usr/bin", "/usr/sbin", "/usr/lib", "/etc", "/System"];
        }

        return new[]
            {
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonProgramFiles,
                Environment.SpecialFolder.CommonProgramFilesX86,
                Environment.SpecialFolder.Windows,
            }
            .Select(Environment.GetFolderPath)
            .Where(p => p.Length > 0)
            .Distinct(PathComparer)
            .ToList();
    }

    /// <summary>True when <paramref name="path"/> is one of <paramref name="roots"/> or inside one.</summary>
    public static bool IsUnderAny(string path, IEnumerable<string> roots)
    {
        var full = NormaliseRoot(path);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            if (IsSameOrInside(full, NormaliseRoot(root)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or lies inside it (both normalised).</summary>
    /// <summary>
    /// True when <paramref name="fullPath"/> is the folder Velopack replaces or deletes
    /// (<see cref="AppLocation.VolatileDirectory"/>), or inside it.
    /// </summary>
    public static bool IsInsideAppFolder(string fullPath) => IsInsideAppFolder(fullPath, DataDirectories.DefaultAppRoot());

    /// <summary>
    /// True when <paramref name="fullPath"/> is in the folder Velopack deletes or replaces for this copy, or anywhere in
    /// <paramref name="appRoot"/> (<c>%LOCALAPPDATA%\BlenderLauncherNet</c>): the installer may take that folder over
    /// later, even from a copy that is not installed, and uninstalling deletes all of it.
    /// </summary>
    public static bool IsInsideAppFolder(string fullPath, string appRoot)
    {
        var path = NormaliseRoot(fullPath);
        return IsSameOrInside(path, NormaliseRoot(appRoot))
            || (AppLocation.VolatileDirectory is { } volatileDirectory && IsSameOrInside(path, NormaliseRoot(volatileDirectory)));
    }

    public static bool IsSameOrInside(string path, string root)
    {
        if (string.Equals(path, root, PathComparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    /// <summary>
    /// Heuristic for a Blender Version Manager root: the folder holds <c>Blender Version Manager.exe</c>, or one of
    /// its direct children that is not a library folder holds <c>blender.exe</c> (builds at the root rather than in
    /// the branch folders).
    /// </summary>
    public static bool LooksLikeVersionManagerRoot(string folder)
    {
        try
        {
            if (File.Exists(Path.Combine(folder, "Blender Version Manager.exe")) ||
                File.Exists(Path.Combine(folder, "BlenderVersionManager.exe")))
            {
                return true;
            }

            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                var name = Path.GetFileName(child);
                if (Channels.LibraryFolders.Contains(name, PathComparer))
                {
                    continue;
                }

                if (File.Exists(Path.Combine(child, BuildExecutables.Blender)))
                {
                    return true;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }
}
