using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.Core.Processes;

/// <summary>One running process started from a build.</summary>
/// <param name="BuildFolder">The build's folder (normalised).</param>
/// <param name="ProcessId">The tracked process: Blender itself, or <c>cmd.exe</c> for a <c>.cmd</c>/<c>.bat</c> executable or script.</param>
/// <param name="StartedAt">When it was started.</param>
/// <param name="ThroughCmd">True when the tracked process is <c>cmd.exe</c> running a batch file.</param>
public sealed record RunningBuildProcess(string BuildFolder, int ProcessId, DateTimeOffset StartedAt, bool ThroughCmd);

/// <summary>Arguments of <see cref="LaunchService.Launched"/>.</summary>
public sealed class BuildLaunchedEventArgs(RunningBuildProcess process, int runningCount, OnLaunchAction action) : EventArgs
{
    public RunningBuildProcess Process { get; } = process;

    /// <summary>Processes of this build running now, this one included.</summary>
    public int RunningCount { get; } = runningCount;

    /// <summary>
    /// The on-launch action for the UI to apply (minimise or close the launcher): the setting when this launch took
    /// the build from none to one running process, as upstream's observer <c>started</c> signal; otherwise
    /// <see cref="OnLaunchAction.DoNothing"/>.
    /// </summary>
    public OnLaunchAction Action { get; } = action;
}

/// <summary>Arguments of <see cref="LaunchService.RunningCountChanged"/>.</summary>
public sealed class RunningCountChangedEventArgs(string buildFolder, int count, int processId, int? exitCode) : EventArgs
{
    /// <summary>The build's folder.</summary>
    public string BuildFolder { get; } = buildFolder;

    /// <summary>Processes of the build running now.</summary>
    public int Count { get; } = count;

    /// <summary>The process that started or exited.</summary>
    public int ProcessId { get; } = processId;

    /// <summary>Its exit code when it exited; null when it started.</summary>
    public int? ExitCode { get; } = exitCode;
}

/// <summary>What <see cref="LaunchService.QuitAll"/> did.</summary>
/// <param name="Processes">Running processes of the build.</param>
/// <param name="WindowsAsked">Top-level windows sent a close request.</param>
public sealed record QuitAllResult(int Processes, int WindowsAsked);

/// <summary>
/// Starts builds and tracks the running processes per build (a port of
/// <c>launch_build</c>, <c>platform_utils._popen</c> and <c>threads/observer.py</c>). Exits are observed by waiting on
/// each process handle, not by polling. Each launch stamps the build's last-used time in the library state.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The command comes from <see cref="LaunchArgsBuilder"/> with the launching settings (startup arguments,
/// hide console); the working directory is the build folder and the environment is the launcher's.</item>
/// <item>For a <c>.cmd</c>/<c>.bat</c> executable or a Debug-menu script the tracked process is <c>cmd.exe</c>,
/// as upstream. Blender started by the script is a child of it: the
/// count drops when the script ends, and "Quit all" reaches only windows that <c>cmd.exe</c> itself owns, so it
/// cannot close such a Blender.</item>
/// <item>Only processes started by this service are known; a build started by upstream or before a restart is not
/// counted.</item>
/// </list>
/// Thread-safe. Events are raised on the launching thread (start) or a thread-pool thread (exit).
/// </remarks>
public sealed class LaunchService : IAsyncDisposable
{
    private readonly IProcessLauncher _launcher;
    private readonly LibraryService? _library;
    private readonly Func<LaunchingSettings> _settings;
    private readonly Log? _log;
    private readonly TimeProvider _time;
    private readonly Func<string, bool> _fileExists;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Tracked>> _running = new(LibraryLayout.PathComparer);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _watches = [];
    private bool _disposed;

    public LaunchService(
        IProcessLauncher launcher,
        LibraryService? library,
        Func<LaunchingSettings> settings,
        Log? log = null,
        TimeProvider? time = null,
        Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(settings);
        _launcher = launcher;
        _library = library;
        _settings = settings;
        _log = log;
        _time = time ?? TimeProvider.System;
        _fileExists = fileExists ?? File.Exists;
    }

    /// <summary>False when the platform cannot launch anything.</summary>
    public bool IsAvailable => _launcher.IsAvailable;

    /// <summary>A build was started.</summary>
    public event EventHandler<BuildLaunchedEventArgs>? Launched;

    /// <summary>The number of running processes of a build changed (a start or an exit).</summary>
    public event EventHandler<RunningCountChangedEventArgs>? RunningCountChanged;

    /// <summary>Running processes over all builds.</summary>
    public int TotalRunning
    {
        get
        {
            lock (_gate)
            {
                return _running.Values.Sum(l => l.Count);
            }
        }
    }

    /// <summary>
    /// Starts <paramref name="build"/> (its <see cref="BuildInfo.Link"/> is the folder) in <paramref name="mode"/>,
    /// or runs <paramref name="script"/> (a Debug-menu script, relative to the build folder) instead of Blender.
    /// </summary>
    /// <param name="build">The build.</param>
    /// <param name="mode">Plain, open the last file, or open a .blend file.</param>
    /// <param name="script">A Debug-menu script to run instead of Blender.</param>
    /// <param name="extraArguments">Arguments for Blender from the command line, added last.</param>
    /// <exception cref="System.ComponentModel.Win32Exception">The process could not be started.</exception>
    public RunningBuildProcess Launch(BuildInfo build, LaunchMode? mode = null, string? script = null, IReadOnlyList<string>? extraArguments = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var settings = _settings();
        var folder = LibraryLayout.NormaliseRoot(build.Link);
        var request = LaunchRequest.For(build with { Link = folder }) with
        {
            StartupArguments = settings.BlenderStartupArguments,
            Mode = mode ?? LaunchMode.Plain,
            HideConsole = settings.HideConsole,
            Script = script,
            ExtraArguments = extraArguments ?? [],
        };
        var command = LaunchArgsBuilder.Build(request, _fileExists);
        _log?.Info($"Launching {command.CommandLine} in {folder}{(command.HideConsole ? " (console hidden)" : "")}");

        var process = _launcher.Start(command, folder);
        var record = new RunningBuildProcess(folder, process.Id, _time.GetUtcNow(), command.UsesCmd);
        var tracked = new Tracked(record, process);
        int count;
        lock (_gate)
        {
            if (!_running.TryGetValue(folder, out var list))
            {
                list = [];
                _running[folder] = list;
            }

            list.Add(tracked);
            count = list.Count;
        }

        StampLastUsed(folder);
        Raise(RunningCountChanged, new RunningCountChangedEventArgs(folder, count, process.Id, null));
        Raise(Launched, new BuildLaunchedEventArgs(record, count, count == 1 ? settings.OnLaunchAction : OnLaunchAction.DoNothing));

        // Watched only after the start events, so an instant exit is never reported before the start.
        var watch = WatchAsync(tracked);
        lock (_gate)
        {
            _watches.RemoveAll(t => t.IsCompleted);
            _watches.Add(watch);
        }

        return record;
    }

    /// <summary>Running processes of <paramref name="buildFolder"/>.</summary>
    public int GetRunningCount(string buildFolder)
    {
        var folder = LibraryLayout.NormaliseRoot(buildFolder);
        lock (_gate)
        {
            return _running.TryGetValue(folder, out var list) ? list.Count : 0;
        }
    }

    /// <summary>The running processes of <paramref name="buildFolder"/>, or of every build when null.</summary>
    public IReadOnlyList<RunningBuildProcess> GetRunning(string? buildFolder = null)
    {
        lock (_gate)
        {
            if (buildFolder is null)
            {
                return [.. _running.Values.SelectMany(l => l).Select(t => t.Record)];
            }

            return _running.TryGetValue(LibraryLayout.NormaliseRoot(buildFolder), out var list) ? [.. list.Select(t => t.Record)] : [];
        }
    }

    /// <summary>
    /// "Quit all": asks each running instance of the build to close its windows. Nothing is
    /// killed; the count drops as the processes exit.
    /// </summary>
    public QuitAllResult QuitAll(string buildFolder)
    {
        var running = GetRunning(buildFolder);
        if (running.Count == 0)
        {
            return new QuitAllResult(0, 0);
        }

        var asked = _launcher.RequestClose([.. running.Select(r => r.ProcessId)]);
        _log?.Info($"Quit all for {buildFolder}: asked {asked} window(s) of {running.Count} process(es) to close");
        return new QuitAllResult(running.Count, asked);
    }

    /// <summary>Completes once no process of <paramref name="buildFolder"/> runs (at once when none does).</summary>
    public async Task WhenNotRunningAsync(string buildFolder, CancellationToken ct = default)
    {
        var folder = LibraryLayout.NormaliseRoot(buildFolder);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, RunningCountChangedEventArgs e)
        {
            if (e.Count == 0 && LibraryLayout.PathComparer.Equals(e.BuildFolder, folder))
            {
                idle.TrySetResult();
            }
        }

        RunningCountChanged += OnChanged;
        try
        {
            if (GetRunningCount(folder) == 0)
            {
                return;
            }

            await idle.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            RunningCountChanged -= OnChanged;
        }
    }

    /// <summary>Stops watching and releases the process handles. Running builds keep running.</summary>
    public async ValueTask DisposeAsync()
    {
        Task[] watches;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            watches = [.. _watches];
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(watches).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        lock (_gate)
        {
            foreach (var tracked in _running.Values.SelectMany(l => l))
            {
                tracked.Process.Dispose();
            }

            _running.Clear();
        }

        _lifetime.Dispose();
    }

    private async Task WatchAsync(Tracked tracked)
    {
        int? exitCode = null;
        try
        {
            exitCode = await tracked.Process.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            _log?.Warning($"Lost track of process {tracked.Record.ProcessId} of {tracked.Record.BuildFolder}", e);
        }

        int count;
        lock (_gate)
        {
            if (!_running.TryGetValue(tracked.Record.BuildFolder, out var list) || !list.Remove(tracked))
            {
                return;
            }

            count = list.Count;
            if (count == 0)
            {
                _running.Remove(tracked.Record.BuildFolder);
            }
        }

        tracked.Process.Dispose();
        _log?.Info($"Process {tracked.Record.ProcessId} of {tracked.Record.BuildFolder} exited with {exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}; {count} still running");
        Raise(RunningCountChanged, new RunningCountChangedEventArgs(tracked.Record.BuildFolder, count, tracked.Record.ProcessId, exitCode));
    }

    private void StampLastUsed(string folder)
    {
        if (_library is not { LibraryRoot: { } root } || !LibraryLayout.IsSameOrInside(folder, root))
        {
            return;
        }

        try
        {
            _library.MarkLaunched(folder);
        }
        catch (InvalidOperationException e)
        {
            _log?.Debug($"Could not stamp last-used for {folder}: {e.Message}");
        }
    }

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        try
        {
            handler?.Invoke(this, args);
        }
        catch (Exception e)
        {
            _log?.Error("A launch event handler threw", e);
        }
    }

    private sealed record Tracked(RunningBuildProcess Record, ILaunchedProcess Process);
}
