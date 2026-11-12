using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace BlenderLauncher.Core.Logging;

/// <summary>Log levels, with upstream's names and Python's numeric values.</summary>
public enum LogLevel
{
    Debug = 10,
    Info = 20,
    Warning = 30,
    Error = 40,
}

/// <summary>Settings for <see cref="Log"/>. The defaults match upstream's <c>setup_logging</c>.</summary>
public sealed record LogOptions
{
    /// <summary>Rotate when a record would take the file to this size or beyond.</summary>
    public long MaxBytes { get; init; } = 1024 * 1024;

    /// <summary>Old files kept: <c>.1</c> (newest) to <c>.N</c>.</summary>
    public int BackupCount { get; init; } = 2;

    /// <summary>Records waiting to be written before <see cref="Log.Write"/> blocks the caller.</summary>
    public int QueueCapacity { get; init; } = 4096;

    /// <summary>Clock for timestamps (local time, as Python's <c>asctime</c>).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// Called on the writer thread with each record's level and its formatted, redacted text (no trailing line
    /// break); for example a console echo. Exceptions it throws are swallowed.
    /// </summary>
    public Action<LogLevel, string>? Listener { get; init; }
}

/// <summary>
/// The launcher log, a port of upstream <c>utils/logger.py</c> without a logging library.
/// <list type="bullet">
/// <item>File <c>blender-launcher.log</c> in the given folder, UTF-8 with CRLF line ends (as Python's text mode on Windows), appended to.</item>
/// <item>Records formatted <c>[yyyy-MM-dd HH:mm:ss,fff:LEVEL] message</c>, as Python's
/// <c>[%(asctime)s:%(levelname)s] %(message)s</c>.</item>
/// <item>Rotates as Python's <c>RotatingFileHandler</c> (1 MB, 2 backups by default). When a rotation fails
/// because another process holds the file, logging carries on in the same file without rotating, as upstream's
/// fallback to a plain <c>FileHandler</c>.</item>
/// <item>Secrets are removed by <see cref="LogRedactor"/> before anything is queued.</item>
/// </list>
/// Writing is safe from any thread and does not wait for the disk: records go to a bounded queue drained by one
/// background thread. <see cref="Flush"/> and <see cref="FlushAsync"/> wait for what is queued; disposing
/// writes everything still queued and closes the file.
/// </summary>
public sealed class Log : IDisposable, IAsyncDisposable
{
    public const string FileName = "blender-launcher.log";

    private readonly LogOptions _options;
    private readonly BlockingCollection<Item> _queue;
    private readonly Thread _writer;
    private FileStream? _file;
    private int _disposed;

    private Log(string folder, LogLevel minimumLevel, LogOptions options)
    {
        _options = options;
        MinimumLevel = minimumLevel;
        Directory.CreateDirectory(folder);
        FilePath = Path.Combine(Path.GetFullPath(folder), FileName);
        IsRotating = options.MaxBytes > 0 && options.BackupCount > 0;
        _file = TryOpen();
        _queue = new BlockingCollection<Item>(new ConcurrentQueue<Item>(), Math.Max(1, options.QueueCapacity));
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "Log writer" };
        _writer.Start();
    }

    /// <summary>Opens (or creates) <c>blender-launcher.log</c> in <paramref name="folder"/>, creating the folder.</summary>
    public static Log Open(string folder, LogLevel minimumLevel = LogLevel.Info, LogOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return new Log(folder, minimumLevel, options ?? new LogOptions());
    }

    public string FilePath { get; }

    /// <summary>Records below this level are dropped. May be changed at any time.</summary>
    public LogLevel MinimumLevel { get; set; }

    /// <summary>False once a rotation has failed (or rotation is off); the file then just grows.</summary>
    public bool IsRotating { get; private set; }

    /// <summary>False when the log file could not be opened at all; records still reach the listener.</summary>
    public bool IsFileOpen => Volatile.Read(ref _file) is not null;

    public bool IsEnabled(LogLevel level) => level >= MinimumLevel;

    public void Debug(string message) => Write(LogLevel.Debug, message);

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warning(string message, Exception? exception = null) => Write(LogLevel.Warning, message, exception);

    public void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    /// <summary>
    /// Queues one record. With an exception, its type, message and stack trace (and inner exceptions) follow the
    /// message on the next lines, as Python's <c>logger.exception</c> does. Writes after disposal are ignored.
    /// </summary>
    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        if (!IsEnabled(level) || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var line = Format(_options.Time.GetLocalNow().DateTime, level, exception is null ? message : message + Environment.NewLine + exception);
        try
        {
            _queue.Add(new Item(level, LogRedactor.Redact(line), null));
        }
        catch (InvalidOperationException)
        {
            // Disposed between the check and the add.
        }
    }

    /// <summary>Formats a record exactly as the file holds it (before redaction, without the line break).</summary>
    public static string Format(DateTime localTime, LogLevel level, string message) =>
        string.Create(CultureInfo.InvariantCulture, $"[{localTime:yyyy-MM-dd HH:mm:ss,fff}:{LevelName(level)}] {message}");

    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        _ => ((int)level).ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Parses a level name as the <c>log_level</c> setting stores it (case-insensitive); unknown names give Info.</summary>
    public static LogLevel ParseLevel(string? name) => name?.Trim().ToUpperInvariant() switch
    {
        "DEBUG" => LogLevel.Debug,
        "WARNING" or "WARN" => LogLevel.Warning,
        "ERROR" or "CRITICAL" => LogLevel.Error,
        _ => LogLevel.Info,
    };

    /// <summary>Waits until every record queued so far is written and flushed to disk.</summary>
    public Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(new Item(LogLevel.Debug, null, done));
        }
        catch (InvalidOperationException)
        {
            return Task.CompletedTask;
        }

        return done.Task;
    }

    /// <summary>Blocks until every record queued so far is written and flushed to disk.</summary>
    public void Flush() => FlushAsync().GetAwaiter().GetResult();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        _writer.Join();
    }

    public ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return ValueTask.CompletedTask;
        }

        // Joining the writer thread is short (it only drains the queue), so do it off the caller's thread.
        return new ValueTask(Task.Run(Dispose));
    }

    private void WriterLoop()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            if (item.Line is { } line)
            {
                WriteLine(item.Level, line);
            }

            // Flush before signalling a flush request: FlushAsync promises the bytes are in the file when it completes.
            if (item.Flushed is not null || _queue.Count == 0)
            {
                SafeFlush();
            }

            item.Flushed?.TrySetResult();
        }

        SafeFlush();
        _file?.Dispose();
        Volatile.Write(ref _file, null);
    }

    private void WriteLine(LogLevel level, string line)
    {
        try
        {
            _options.Listener?.Invoke(level, line);
        }
        catch (Exception)
        {
            // A broken listener must not stop the log.
        }

        if (_file is null)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
        try
        {
            if (IsRotating && _file.Length + bytes.Length >= _options.MaxBytes && _file.Length > 0)
            {
                Rotate();
            }

            _file?.Write(bytes);
        }
        catch (IOException)
        {
            // Disk full or the file vanished: drop the record rather than crash the app.
        }
    }

    /// <summary>Python's <c>doRollover</c>: delete <c>.N</c>, shift <c>.i</c> to <c>.i+1</c>, then the log to <c>.1</c>.</summary>
    private void Rotate()
    {
        _file!.Flush();
        _file.Dispose();
        _file = null;
        try
        {
            for (var i = _options.BackupCount - 1; i >= 1; i--)
            {
                var from = $"{FilePath}.{i}";
                if (File.Exists(from))
                {
                    File.Move(from, $"{FilePath}.{i + 1}", overwrite: true);
                }
            }

            File.Move(FilePath, FilePath + ".1", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another process has the file open without delete sharing: keep appending to the same file.
            IsRotating = false;
        }

        Volatile.Write(ref _file, TryOpen());
    }

    private FileStream? TryOpen()
    {
        try
        {
            // Delete sharing lets another instance rotate the file while this one has it open.
            return new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 8192);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SafeFlush()
    {
        try
        {
            _file?.Flush(flushToDisk: false);
        }
        catch (IOException)
        {
        }
    }

    private readonly record struct Item(LogLevel Level, string? Line, TaskCompletionSource? Flushed);
}
