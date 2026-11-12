using System.Globalization;
using System.Text;
using BlenderLauncher.Platform.Windows.Cli;

namespace BlenderLauncher.App.Diagnostics;

/// <summary>
/// Output of the developer commands (<c>--dev-install</c>, <c>--dev-launch</c>): each line goes at once to the
/// console the exe was started from (<see cref="ParentConsole"/>) or to redirected standard output, and is appended
/// to <c>--out</c> when given, so a long run can be watched. A GUI-subsystem exe is not waited for by <c>cmd</c>,
/// so <c>--out</c> (or <c>Start-Process -Wait</c>) is the reliable way to read it.
/// </summary>
internal sealed class DevOutput : IDisposable
{
    private readonly Lock _gate = new();
    private readonly string? _outPath;
    private readonly bool _toConsole;
    private readonly ConsoleAttachOutcome _attached;

    public DevOutput(string? outPath)
    {
        _outPath = outPath;
        _attached = ParentConsole.TryAttach(out _);
        _toConsole = _attached is ConsoleAttachOutcome.Attached or ConsoleAttachOutcome.Redirected;
        if (_outPath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_outPath)!);
                File.WriteAllText(_outPath, "", new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _outPath = null;
            }
        }
    }

    public void Line(string text = "")
    {
        lock (_gate)
        {
            if (_outPath is not null)
            {
                try
                {
                    File.AppendAllText(_outPath, text + "\n", new UTF8Encoding(false));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }

            if (_toConsole)
            {
                Console.Out.WriteLine(text);
                Console.Out.Flush();
            }
        }
    }

    public void Dispose()
    {
        if (_attached == ConsoleAttachOutcome.Attached)
        {
            ParentConsole.Detach();
        }
    }

    /// <summary>The value after <paramref name="name"/> in <paramref name="args"/>, or null.</summary>
    public static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>The first argument after <paramref name="command"/> that is not an option or an option's value.</summary>
    public static string? Positional(string[] args, string command, IReadOnlyCollection<string> optionsWithValues)
    {
        var start = Array.FindIndex(args, a => string.Equals(a, command, StringComparison.OrdinalIgnoreCase));
        for (var i = start + 1; i < args.Length; i++)
        {
            if (optionsWithValues.Contains(args[i], StringComparer.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                return args[i];
            }
        }

        return null;
    }

    public static void TryCopy(string from, string to)
    {
        try
        {
            File.Copy(from, to, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.00} GB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MB"),
        >= 1L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
    };

    public static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
