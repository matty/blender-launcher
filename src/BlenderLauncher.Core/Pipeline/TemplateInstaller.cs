using System.Text.RegularExpressions;

namespace BlenderLauncher.Core.Pipeline;

/// <summary>
/// Port of <c>threads/template_installer.py</c>: copies the library's
/// <c>template</c> folder into a build's version config folder.
/// </summary>
public static partial class TemplateInstaller
{
    /// <summary>
    /// Merges the contents of <paramref name="templateFolder"/> into the first child of <paramref name="buildFolder"/>
    /// (ordinal order) whose name matches <c>^\d+\.\d+.*</c>, overwriting files of the same name
    /// (<c>copytree(dirs_exist_ok=True)</c>). Returns that child, or null when there is none. A missing template
    /// folder is treated as empty and is not created (upstream creates it).
    /// </summary>
    /// <exception cref="PipelineException"><see cref="PipelineErrorKind.Permission"/> when writing is refused.</exception>
    public static string? Install(string templateFolder, string buildFolder)
    {
        ArgumentException.ThrowIfNullOrEmpty(templateFolder);
        ArgumentException.ThrowIfNullOrEmpty(buildFolder);
        try
        {
            var target = Directory.GetDirectories(buildFolder)
                .Order(StringComparer.Ordinal)
                .FirstOrDefault(d => VersionFolder().IsMatch(Path.GetFileName(d)));
            if (target is null)
            {
                return null;
            }

            if (Directory.Exists(templateFolder))
            {
                Extractor.CopyDirectory(templateFolder, target);
            }

            return target;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new PipelineException(PipelineErrorKind.Permission,
                $"Template installation failed: no write permission for {buildFolder}", ex) { Folder = buildFolder };
        }
        catch (IOException ex)
        {
            throw new PipelineException(PipelineErrorKind.Io, $"Template installation failed for {buildFolder}: {ex.Message}", ex)
            {
                Folder = buildFolder,
            };
        }
    }

    [GeneratedRegex(@"^\d+\.\d+.*", RegexOptions.CultureInvariant)]
    private static partial Regex VersionFolder();
}
