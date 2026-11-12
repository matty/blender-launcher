using System.Text;

namespace BlenderLauncher.Core.IO;

/// <summary>
/// Whole-file writes that a crash or a concurrent reader never sees half done: the text goes
/// to a temp file in the target's folder, which is then renamed over the target.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes <paramref name="text"/> as UTF-8 without a BOM to <paramref name="path"/>, replacing it.</summary>
    public static async Task WriteAllTextAsync(string path, string text, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        var temp = Path.Combine(Path.GetDirectoryName(full)!, $"{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                var bytes = Utf8NoBom.GetBytes(text);
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
