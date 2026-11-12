using BlenderLauncher.Core.Library;

namespace BlenderLauncher.App.ViewModels.FirstRun;

/// <summary>Why a library folder typed in the first-run wizard cannot be used.</summary>
public enum LibraryCheckProblem
{
    Empty,
    NotAbsolute,
    InvalidPath,
    IsAFile,
    NotWritable,

    /// <summary>Inside the folder Velopack replaces on update or deletes on uninstall.</summary>
    InsideAppFolder,
}

/// <summary>The outcome of <see cref="LibraryFolderCheck.Run"/>.</summary>
/// <param name="FullPath">The normalised absolute folder, when the text is a path.</param>
/// <param name="Problem">Why it cannot be used, or null.</param>
/// <param name="Exists">The folder exists already (otherwise it is created when the wizard finishes).</param>
/// <param name="Warnings">Advice that does not block (protected location, Blender Version Manager folder).</param>
public sealed record LibraryCheckResult(string? FullPath, LibraryCheckProblem? Problem, bool Exists, IReadOnlyList<LibraryFolderWarning> Warnings)
{
    public bool IsValid => Problem is null && FullPath is not null;
}

/// <summary>
/// The wizard's library folder check. Unlike <see cref="LibraryLayout.Validate"/> it accepts a folder that
/// does not exist yet (it is created on Finish) and leaves nothing behind: the write probe creates and deletes a
/// uniquely named folder in the folder, or in its nearest existing parent, because the check runs while the user
/// types and must not plant a <c>.temp</c> folder in every folder the path passes through.
/// </summary>
public static class LibraryFolderCheck
{
    public static LibraryCheckResult Run(string? text, IEnumerable<string>? protectedRoots = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new(null, LibraryCheckProblem.Empty, false, []);
        }

        var trimmed = text.Trim().Trim('"');
        string full;
        try
        {
            if (!Path.IsPathFullyQualified(trimmed))
            {
                return new(null, LibraryCheckProblem.NotAbsolute, false, []);
            }

            full = LibraryLayout.NormaliseRoot(Path.GetFullPath(trimmed));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(null, LibraryCheckProblem.InvalidPath, false, []);
        }

        if (File.Exists(full))
        {
            return new(full, LibraryCheckProblem.IsAFile, false, []);
        }

        if (LibraryLayout.IsInsideAppFolder(full))
        {
            return new(full, LibraryCheckProblem.InsideAppFolder, Directory.Exists(full), []);
        }

        var exists = Directory.Exists(full);
        var probeFolder = exists ? full : NearestExistingParent(full);
        if (probeFolder is null)
        {
            return new(full, LibraryCheckProblem.InvalidPath, false, []);
        }

        var warnings = new List<LibraryFolderWarning>();
        if (LibraryLayout.IsUnderAny(full, protectedRoots ?? LibraryLayout.DefaultProtectedRoots()))
        {
            warnings.Add(LibraryFolderWarning.ProtectedLocation);
        }

        if (exists && LibraryLayout.LooksLikeVersionManagerRoot(full))
        {
            warnings.Add(LibraryFolderWarning.BlenderVersionManagerRoot);
        }

        return new(full, CanWrite(probeFolder) ? null : LibraryCheckProblem.NotWritable, exists, warnings);
    }

    private static string? NearestExistingParent(string path)
    {
        var current = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    private static bool CanWrite(string folder)
    {
        var probe = Path.Combine(folder, ".blender-launcher-probe-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
