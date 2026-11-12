namespace BlenderLauncher.Core.Pipeline;

/// <summary>Entry-name rules shared by the zip and 7z paths of <see cref="Extractor"/>.</summary>
public static class ArchivePaths
{
    /// <summary>An entry name with backslashes turned into forward slashes (7z and some zip tools write either).</summary>
    public static string Normalise(string entryName) => entryName.Replace('\\', '/');

    /// <summary>
    /// The full path <paramref name="entryName"/> extracts to under <paramref name="root"/>, or throws
    /// <see cref="PipelineException"/> (<see cref="PipelineErrorKind.UnsafeArchive"/>) when the entry is absolute,
    /// names a drive or stream (<c>:</c>), has a <c>..</c> segment (or one of only dots and spaces, which Windows
    /// would shorten), or lands outside <paramref name="root"/> once normalised. Upstream had no such check
    ///.
    /// </summary>
    public static string Resolve(string root, string entryName)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(entryName);
        var name = Normalise(entryName);
        if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || name.Contains('\0'))
        {
            throw Unsafe(entryName);
        }

        foreach (var segment in name.Split('/'))
        {
            if (segment == "..")
            {
                throw Unsafe(entryName);
            }

            if (segment.Length > 1 && segment.Trim('.', ' ').Length == 0)
            {
                throw Unsafe(entryName);
            }
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(fullRoot, name.Replace('/', Path.DirectorySeparatorChar)));
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        if (!trimmed.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw Unsafe(entryName);
        }

        return trimmed;
    }

    /// <summary>
    /// Upstream's <c>_get_build_folder</c> (<c>extractor.py:226-233</c>): the single top-level folder among names
    /// containing <c>/</c>, or null when there are none or several.
    /// </summary>
    public static string? FindTopFolder(IEnumerable<string> entryNames)
    {
        var names = entryNames.Select(Normalise).ToList();
        var tops = names.Where(n => n.Length > 0 && n.Contains('/')).Select(n => n[..n.IndexOf('/')]).ToHashSet(StringComparer.Ordinal);
        var folders = tops.Where(t => names.Any(n => n.StartsWith(t + "/", StringComparison.Ordinal))).ToList();
        return folders.Count == 1 ? folders[0] : null;
    }

    /// <summary>
    /// Upstream's UPBGE flat-archive test: the top folder is <c>bin</c> and some entry sits under <c>bin/Release/</c>.
    /// Such archives are extracted into a folder named after the archive.
    /// </summary>
    public static bool IsUpbgeFlat(string? topFolder, IEnumerable<string> entryNames) =>
        topFolder == "bin" && entryNames.Any(n => Normalise(n).StartsWith("bin/Release/", StringComparison.Ordinal));

    private static PipelineException Unsafe(string entryName) =>
        new(PipelineErrorKind.UnsafeArchive, $"The archive has an entry that would be written outside its folder: '{entryName}'");
}
