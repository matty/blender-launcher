using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.Core.Integration;

/// <summary>What <see cref="Uninstaller.Run"/> works on.</summary>
public sealed record UninstallContext
{
    /// <summary>The integration services (their roots decide which registry keys and folders are touched).</summary>
    public required PlatformServiceBundle Platform { get; init; }

    public required DataDirectories Directories { get; init; }

    /// <summary>The library folder, absolute; never deleted. Null when none was set.</summary>
    public string? LibraryRoot { get; init; }

    /// <summary>The running executable.</summary>
    public required string ExePath { get; init; }

    /// <summary>
    /// The file names a release puts beside the exe (the exe, its native libraries, the licence files). The exe folder
    /// is removed only when it holds nothing else.
    /// </summary>
    public required IReadOnlyCollection<string> ShippedFileNames { get; init; }

    /// <summary>Folders the exe folder must never be (the user profile, Desktop, Downloads, Program Files…).</summary>
    public IReadOnlyCollection<string> ProtectedFolders { get; init; } = [];
}

/// <summary>One line of the uninstall report.</summary>
public sealed record UninstallStep(string Text, bool Ok = true);

/// <summary>The outcome of <see cref="Uninstaller.Run"/>.</summary>
public sealed record UninstallReport(
    IReadOnlyList<UninstallStep> Steps,
    string? LibraryKept,
    IReadOnlyList<string>? ExeFolderFiles,
    string? ExeFolderKeptReason)
{
    /// <summary>True when every step worked.</summary>
    public bool Succeeded => Steps.All(s => s.Ok);

    /// <summary>True when the caller should delete <see cref="ExeFolderFiles"/> and the exe folder once this process exits.</summary>
    public bool RemoveExeFolder => ExeFolderFiles is not null;
}

/// <summary>
/// <c>uninstall</c> (upstream <c>modules/uninstall.py</c>): removes the launcher's registrations, its own
/// shortcuts, its credentials and its data folder. Unlike upstream it never deletes the library, even when the library
/// lies inside the data folder or the exe folder; it removes a registration only when it belongs to
/// this executable, and a shortcut only when it starts this executable.
/// </summary>
public static class Uninstaller
{
    public static UninstallReport Run(UninstallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var steps = new List<UninstallStep>();
        var p = context.Platform;
        var exe = context.ExePath;

        Step(steps, "Launch at startup", () =>
        {
            if (!p.Startup.Exists)
            {
                return "not set";
            }

            if (!p.Startup.IsEnabled(exe))
            {
                return "kept: it starts another copy of the launcher";
            }

            p.Startup.Disable();
            return "removed";
        });

        Step(steps, ".blend file association", () =>
        {
            var state = p.FileAssociation.GetState(exe);
            if (state == FileAssociationState.OtherExecutable)
            {
                return "kept: it opens another copy of the launcher";
            }

            p.FileAssociation.Unregister();
            return state == FileAssociationState.NotRegistered ? "not registered" : "removed";
        });

        Step(steps, "Add/Remove Programs entry", () =>
        {
            if (!p.AppRegistration.IsRegistered())
            {
                return "not registered";
            }

            p.AppRegistration.Unregister();
            return "removed";
        });

        foreach (var location in new[] { ShortcutLocation.StartMenu, ShortcutLocation.Desktop })
        {
            Step(steps, location == ShortcutLocation.StartMenu ? "Start Menu shortcut" : "Desktop shortcut", () =>
            {
                if (LauncherShortcut.PathFor(p.Shortcuts, location) is not { } path || !File.Exists(path))
                {
                    return "none";
                }

                return LauncherShortcut.RemoveIfOurs(p.Shortcuts, location, exe)
                    ? "removed"
                    : "kept: it does not start this copy of the launcher";
            });
        }

        Step(steps, "Saved credentials", () =>
        {
            var removed = CredentialKeys.All.Count(p.Credentials.Delete);
            return removed == 0 ? "none" : $"{removed} removed";
        });

        var library = context.LibraryRoot is { } lib ? LibraryLayout.NormaliseRoot(lib) : null;
        Step(steps, "Data folder", () =>
        {
            var root = context.Directories.Root;
            var kept = DeleteTreeExcept(root, library);
            if (context.Directories.IsPortable && File.Exists(context.Directories.SettingsFile))
            {
                File.Delete(context.Directories.SettingsFile);
            }

            // %LOCALAPPDATA%\BlenderLauncherNet holds only "data" unless Velopack installed into it (it deletes it then).
            if (!kept && !context.Directories.IsPortable && Path.GetDirectoryName(root) is { } parent
                && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }

            return kept ? $"{root} removed, except the library inside it" : $"{root} removed";
        });

        var (files, reason) = PlanExeFolder(context, library);
        return new UninstallReport(steps, library, files, reason);
    }

    /// <summary>
    /// Deletes <paramref name="root"/> and everything in it except <paramref name="keep"/> (and the folders leading to
    /// it). Links are removed, never followed. Returns true when something was kept.
    /// </summary>
    public static bool DeleteTreeExcept(string root, string? keep)
    {
        if (!Directory.Exists(root))
        {
            return false;
        }

        var full = LibraryLayout.NormaliseRoot(root);
        if (keep is not null && LibraryLayout.IsSameOrInside(full, keep))
        {
            // The data folder is the library or inside it: nothing here is the launcher's alone.
            return true;
        }

        if (keep is null || !LibraryLayout.IsSameOrInside(keep, full))
        {
            DeleteEntry(new DirectoryInfo(full));
            return false;
        }

        foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos())
        {
            var path = LibraryLayout.NormaliseRoot(entry.FullName);
            if (entry is DirectoryInfo dir && entry.LinkTarget is null && LibraryLayout.IsSameOrInside(keep, path))
            {
                if (!LibraryLayout.PathComparer.Equals(path, keep))
                {
                    DeleteTreeExcept(dir.FullName, keep);
                }

                continue;
            }

            DeleteEntry(entry);
        }

        return true;
    }

    private static void DeleteEntry(FileSystemInfo entry)
    {
        if (entry is DirectoryInfo dir)
        {
            if (dir.LinkTarget is not null)
            {
                dir.Delete(); // the link only
                return;
            }

            foreach (var child in dir.EnumerateFileSystemInfos())
            {
                DeleteEntry(child);
            }

            dir.Attributes = FileAttributes.Directory;
            dir.Delete();
            return;
        }

        entry.Attributes = FileAttributes.Normal;
        entry.Delete();
    }

    /// <summary>
    /// Whether the exe folder can go: only when it holds nothing but the shipped files (plus update leftovers), holds no
    /// library, and is not a protected folder. Returns the files to delete, or null and the reason it stays.
    /// </summary>
    public static (IReadOnlyList<string>? Files, string? Reason) PlanExeFolder(UninstallContext context, string? library)
    {
        ArgumentNullException.ThrowIfNull(context);
        var folder = Path.GetDirectoryName(Path.GetFullPath(context.ExePath));
        if (folder is null || Path.GetPathRoot(folder) is { } driveRoot && LibraryLayout.PathComparer.Equals(LibraryLayout.NormaliseRoot(driveRoot), LibraryLayout.NormaliseRoot(folder)))
        {
            return (null, "it is a drive root");
        }

        folder = LibraryLayout.NormaliseRoot(folder);
        if (context.ProtectedFolders.Any(f => !string.IsNullOrWhiteSpace(f) && LibraryLayout.IsSameOrInside(LibraryLayout.NormaliseRoot(f), folder)))
        {
            return (null, "it is a system or user folder");
        }

        if (library is not null && (LibraryLayout.IsSameOrInside(library, folder) || LibraryLayout.IsSameOrInside(folder, library)))
        {
            return (null, "the library is in it");
        }

        var shipped = new HashSet<string>(context.ShippedFileNames, LibraryLayout.PathComparer);
        var files = new List<string>();
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (entry is not FileInfo || !shipped.Contains(entry.Name))
            {
                return (null, $"it holds other files ({entry.Name})");
            }

            files.Add(entry.FullName);
        }

        return (files, null);
    }

    private static void Step(List<UninstallStep> steps, string what, Func<string> action)
    {
        try
        {
            steps.Add(new UninstallStep($"{what}: {action()}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CredentialStoreException
            or InvalidOperationException or System.Security.SecurityException or ArgumentException)
        {
            steps.Add(new UninstallStep($"{what}: failed ({e.Message})", Ok: false));
        }
    }
}
