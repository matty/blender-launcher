using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BlenderLauncher.Core.Library;

/// <summary>Folder size and bundled Python version of an installed build.</summary>
public static partial class BuildFolderInspector
{
    /// <summary>
    /// Total size in bytes of the files under <paramref name="folder"/>. Reparse points (junctions, symbolic links)
    /// are not followed and count nothing; entries that cannot be read are skipped. Throws
    /// <see cref="OperationCanceledException"/> when cancelled and <see cref="DirectoryNotFoundException"/> when the
    /// folder is missing.
    /// </summary>
    public static long MeasureSize(string folder, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException(folder);
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        long total = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(folder));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos("*", options);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            try
            {
                foreach (var entry in entries)
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo sub)
                    {
                        pending.Push(sub);
                    }
                    else if (entry is FileInfo file)
                    {
                        total += file.Length;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return total;
    }

    /// <summary>
    /// The version of the Python bundled with the build, or null when there is none. Looks in the highest
    /// <c>&lt;M.m&gt;\python</c> folder of the build: first the version resource of <c>bin\python3*.dll</c> or
    /// <c>bin\python.exe</c> (for example <c>3.11.9</c>), then the digits of a <c>python3NN.dll</c> name, then a
    /// <c>lib\python3.NN</c> folder (both giving <c>3.NN</c>).
    /// </summary>
    public static string? ReadPythonVersion(string buildFolder)
    {
        try
        {
            if (!Directory.Exists(buildFolder))
            {
                return null;
            }

            var versionFolders = Directory.GetDirectories(buildFolder)
                .Select(d => (Path: d, Match: VersionFolder().Match(Path.GetFileName(d))))
                .Where(x => x.Match.Success)
                .OrderByDescending(x => int.Parse(x.Match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
                .ThenByDescending(x => int.Parse(x.Match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
                .Select(x => Path.Combine(x.Path, "python"));

            foreach (var python in versionFolders)
            {
                if (!Directory.Exists(python))
                {
                    continue;
                }

                var bin = Path.Combine(python, "bin");
                var dlls = Directory.Exists(bin) ? Directory.GetFiles(bin, "python3*.dll") : [];
                Array.Sort(dlls, StringComparer.OrdinalIgnoreCase);
                var candidates = dlls.Where(d => DllName().IsMatch(Path.GetFileName(d))).Concat([Path.Combine(bin, "python.exe")]);
                foreach (var file in candidates)
                {
                    if (File.Exists(file) && FromVersionResource(file) is { } fromResource)
                    {
                        return fromResource;
                    }
                }

                foreach (var dll in dlls)
                {
                    if (DllName().Match(Path.GetFileName(dll)) is { Success: true } m)
                    {
                        return $"3.{m.Groups[1].Value}";
                    }
                }

                var lib = Path.Combine(python, "lib");
                if (Directory.Exists(lib))
                {
                    foreach (var dir in Directory.GetDirectories(lib).Order(StringComparer.OrdinalIgnoreCase))
                    {
                        if (LibFolder().Match(Path.GetFileName(dir)) is { Success: true } m)
                        {
                            return m.Groups[1].Value;
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static string? FromVersionResource(string file)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(file);
            foreach (var text in new[] { info.ProductVersion, info.FileVersion })
            {
                if (text is not null && LeadingVersion().Match(text) is { Success: true } m)
                {
                    return m.Value;
                }
            }

            if (info.FileMajorPart == 3)
            {
                return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
        }

        return null;
    }

    [GeneratedRegex(@"^(\d+)\.(\d+)$")]
    private static partial Regex VersionFolder();

    [GeneratedRegex(@"^python3(\d{1,2})\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex DllName();

    [GeneratedRegex(@"^python(3\.\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex LibFolder();

    [GeneratedRegex(@"^3\.\d+(\.\d+)?")]
    private static partial Regex LeadingVersion();
}

/// <summary>
/// Measures folder sizes one at a time on a dedicated below-normal-priority background thread, so a library of
/// many builds never competes with the UI or a download for the disk.
/// </summary>
public sealed class BuildSizeMeasurer : IDisposable
{
    private readonly BlockingCollection<Request> _queue = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;

    /// <summary>Starts the worker thread.</summary>
    public BuildSizeMeasurer()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Build size measurer",
        };
        _thread.Start();
    }

    /// <summary>Queues <paramref name="folder"/>; the task completes with its size, or faults or cancels.</summary>
    public Task<long> MeasureAsync(string folder, CancellationToken ct = default)
    {
        var request = new Request(folder, ct, new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            _queue.Add(request, ct);
        }
        catch (OperationCanceledException)
        {
            request.Completion.TrySetCanceled(ct);
        }
        catch (InvalidOperationException e)
        {
            request.Completion.TrySetException(new ObjectDisposedException(nameof(BuildSizeMeasurer), e));
        }

        return request.Completion.Task;
    }

    /// <summary>Stops the worker; queued requests are cancelled.</summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        _stop.Cancel();
        _thread.Join(TimeSpan.FromSeconds(5));
        while (_queue.TryTake(out var left))
        {
            left.Completion.TrySetCanceled();
        }

        _stop.Dispose();
    }

    private void Run()
    {
        try
        {
            foreach (var request in _queue.GetConsumingEnumerable(_stop.Token))
            {
                if (request.Cancellation.IsCancellationRequested)
                {
                    request.Completion.TrySetCanceled(request.Cancellation);
                    continue;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.Cancellation, _stop.Token);
                try
                {
                    request.Completion.TrySetResult(BuildFolderInspector.MeasureSize(request.Folder, linked.Token));
                }
                catch (OperationCanceledException)
                {
                    request.Completion.TrySetCanceled(request.Cancellation);
                }
                catch (Exception e)
                {
                    request.Completion.TrySetException(e);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record Request(string Folder, CancellationToken Cancellation, TaskCompletionSource<long> Completion);
}
