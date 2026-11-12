using System.Globalization;

namespace BlenderLauncher.App.SelfTest;

/// <summary>Free-form diagnostic log written beside the result file. Thread-safe.</summary>
internal sealed class SelfTestLog
{
    private readonly object _gate = new();
    private readonly string? _path;

    public SelfTestLog(string? path)
    {
        _path = path;
        if (_path is not null)
        {
            File.WriteAllText(_path, string.Empty);
        }
    }

    public void Write(string source, string message)
    {
        if (_path is null)
        {
            return;
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss.fff}\t{source}\t{message}{Environment.NewLine}");
        lock (_gate)
        {
            File.AppendAllText(_path, line);
        }
    }
}
