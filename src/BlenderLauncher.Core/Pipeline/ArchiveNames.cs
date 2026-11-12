namespace BlenderLauncher.Core.Pipeline;

/// <summary>Archive file names and kinds.</summary>
public static class ArchiveNames
{
    /// <summary>
    /// Upstream's <c>extract_filename_from_url</c>: the last path segment, URL-decoded, except for legacy
    /// Bforartists Nextcloud links, whose name is the <c>files</c> query parameter. Characters Windows forbids in
    /// file names become <c>_</c>. Throws <see cref="ArgumentException"/> when no usable name remains.
    /// </summary>
    public static string FromUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        string? name = null;
        if (url.IsAbsoluteUri && url.Host.Contains("cloud.bforartists.de", StringComparison.OrdinalIgnoreCase) && url.Query.Length > 1)
        {
            foreach (var pair in url.Query[1..].Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0 && Uri.UnescapeDataString(pair[..eq]) == "files")
                {
                    name = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                    break;
                }
            }
        }

        if (name is null)
        {
            var path = url.IsAbsoluteUri ? url.AbsolutePath : url.OriginalString;
            name = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
        }

        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 || c is '\\' or '/' or ':' ? '_' : c).ToArray()).Trim();
        if (clean.Length == 0 || clean.Trim('.').Length == 0)
        {
            throw new ArgumentException($"No file name in {url}", nameof(url));
        }

        return clean;
    }

    /// <summary>The archive's kind from its name, or null when unsupported.</summary>
    public static ArchiveKind? KindOf(string fileName)
    {
        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return ArchiveKind.Zip;
        }

        if (fileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            return ArchiveKind.SevenZip;
        }

        return null;
    }

    /// <summary>Python's <c>Path(name).stem</c>: the name without its last extension.</summary>
    public static string Stem(string fileName) => Path.GetFileNameWithoutExtension(fileName);
}

/// <summary>Archive formats the extractor handles on Windows. <c>.tar.*</c> and <c>.dmg</c> wait for Linux and macOS.</summary>
public enum ArchiveKind
{
    Zip,
    SevenZip,
}
