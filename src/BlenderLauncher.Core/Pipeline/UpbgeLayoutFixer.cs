using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.PlatformServices;

namespace BlenderLauncher.Core.Pipeline;

/// <summary>
/// Port of upstream's <c>_fix_upbge_structure</c> (<c>extractor.py:236-313</c>), run on UPBGE
/// builds after extraction. The extractor calls it while the build is still in staging.
/// </summary>
public static class UpbgeLayoutFixer
{
    /// <summary>What <see cref="Fix"/> changed.</summary>
    public enum Change
    {
        None,

        /// <summary><c>bin/Release</c>'s contents moved up to the build root and <c>bin</c> removed.</summary>
        FlattenedBinRelease,

        /// <summary>The root had no executable and one subfolder that has it; that subfolder became the root.</summary>
        LiftedSingleSubfolder,
    }

    /// <summary>
    /// Fixes the layout of the build at <paramref name="buildPath"/> in place and says what changed. The path
    /// itself stays the same.
    /// <list type="bullet">
    /// <item>If <c>bin/Release</c> exists, each item in it replaces any item of the same name at the root, then
    /// <c>bin</c> is deleted.</item>
    /// <item>Otherwise, if the root has no <c>blender.exe</c> or <c>blender</c> and exactly one subfolder, which
    /// has one, that subfolder is moved aside to <c>&lt;name&gt;_temp</c>, the root is deleted (with any loose
    /// files in it, as upstream does) and the subfolder renamed to the root's name.</item>
    /// </list>
    /// </summary>
    public static Change Fix(string buildPath, Log? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(buildPath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(buildPath));
        var binRelease = Path.Combine(root, "bin", "Release");
        if (Directory.Exists(binRelease))
        {
            log?.Info($"Detected UPBGE bin/Release structure in {root}");
            foreach (var item in new DirectoryInfo(binRelease).EnumerateFileSystemInfos().ToList())
            {
                var dest = Path.Combine(root, item.Name);
                PermanentDelete.Run(dest);
                if (item is DirectoryInfo dir)
                {
                    dir.MoveTo(dest);
                }
                else
                {
                    File.Move(item.FullName, dest);
                }
            }

            PermanentDelete.Run(Path.Combine(root, "bin"));
            return Change.FlattenedBinRelease;
        }

        if (HasExecutable(root))
        {
            return Change.None;
        }

        var subfolders = Directory.GetDirectories(root);
        if (subfolders.Length != 1 || !HasExecutable(subfolders[0]))
        {
            return Change.None;
        }

        log?.Info($"Detected UPBGE nested folder {Path.GetFileName(subfolders[0])} inside {Path.GetFileName(root)}");
        var temp = root + "_temp";
        PermanentDelete.Run(temp);
        Directory.Move(subfolders[0], temp);
        PermanentDelete.Run(root);
        Directory.Move(temp, root);
        return Change.LiftedSingleSubfolder;
    }

    private static bool HasExecutable(string folder) =>
        File.Exists(Path.Combine(folder, "blender.exe")) || File.Exists(Path.Combine(folder, "blender"));
}

/// <summary>
/// Upstream's <c>move_bforartists_patch_note</c> (<c>download_widget.py:266-283</c>).
/// </summary>
public static class BforartistsPatchNotes
{
    /// <summary>
    /// For each <c>*.txt</c> directly in <paramref name="bforartistsFolder"/>, the version is the last three
    /// characters of its stem joined by dots (<c>…430</c> gives <c>4.3.0</c>); the file moves into the first
    /// subfolder (ordinal order) whose name contains that version. Files with no match stay. Returns how many moved.
    /// </summary>
    public static int Move(string bforartistsFolder, Log? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(bforartistsFolder);
        if (!Directory.Exists(bforartistsFolder))
        {
            return 0;
        }

        var folders = Directory.GetDirectories(bforartistsFolder).Order(StringComparer.Ordinal).ToList();
        var moved = 0;
        foreach (var file in Directory.GetFiles(bforartistsFolder, "*.txt").Order(StringComparer.Ordinal))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var version = string.Join('.', stem[Math.Max(0, stem.Length - 3)..].ToCharArray());
            var target = folders.FirstOrDefault(f => Path.GetFileName(f).Contains(version, StringComparison.Ordinal));
            if (target is null)
            {
                continue;
            }

            try
            {
                File.Move(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Warning($"Failed to move {Path.GetFileName(file)} to {Path.GetFileName(target)}", ex);
            }
        }

        return moved;
    }
}
