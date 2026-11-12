using System.Runtime.CompilerServices;
using Avalonia.Threading;
using BlenderLauncher.Core;

namespace BlenderLauncher.App.SelfTest;

/// <summary>
/// What a check can use. One instance per check; <see cref="WorkDirectory"/> is that check's own empty folder.
/// </summary>
public sealed class SelfTestContext
{
    private readonly SelfTestLog _log;

    internal SelfTestContext(string checkId, bool offline, string runDirectory, SelfTestLog log)
    {
        CheckId = checkId;
        Offline = offline;
        RunDirectory = runDirectory;
        WorkDirectory = Path.Combine(runDirectory, checkId);
        Directory.CreateDirectory(WorkDirectory);
        _log = log;
    }

    public string CheckId { get; }

    /// <summary>True when <c>--offline</c> was passed. Checks with <see cref="ISelfTestCheck.NeedsNetwork"/> are skipped before they run.</summary>
    public bool Offline { get; }

    /// <summary>Unique temporary folder for the whole run, under %TEMP%. Deleted afterwards unless <c>--keep-temp</c>.</summary>
    public string RunDirectory { get; }

    /// <summary>Empty folder for this check only, inside <see cref="RunDirectory"/>.</summary>
    public string WorkDirectory { get; }

    /// <summary>Folder holding the running executable (the publish folder when run from the published exe).</summary>
    public string AppDirectory => AppContext.BaseDirectory;

    /// <summary>
    /// True in the Native AOT build, false under the JIT (<c>dotnet run</c>, the Debug exe, tests).
    /// <see cref="RuntimeFeature.IsDynamicCodeSupported"/> cannot tell them apart: PublishAot also writes
    /// <c>IsDynamicCodeSupported=false</c> into the JIT build's runtimeconfig so it behaves like AOT.
    /// A JIT build has the managed <c>BlenderLauncher.dll</c> beside the exe; the native publish does not.
    /// </summary>
    public bool IsNativeAot => !File.Exists(Path.Combine(AppDirectory, AppIdentity.AssemblyName + ".dll"));

    /// <summary>The runtime's dynamic-code switch. Must be false in the published exe.</summary>
    public bool IsDynamicCodeSupported => RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>Appends a line to the run's log file (<c>&lt;out&gt;.log</c>), prefixed with the check ID.</summary>
    public void Log(string message) => _log.Write(CheckId, message);

    public Task RunOnUiThreadAsync(Action action) =>
        Dispatcher.UIThread.InvokeAsync(action).GetTask();

    public Task<T> RunOnUiThreadAsync<T>(Func<T> func) =>
        Dispatcher.UIThread.InvokeAsync(func).GetTask();

    public Task RunOnUiThreadAsync(Func<Task> func) =>
        Dispatcher.UIThread.InvokeAsync(func);

    public Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> func) =>
        Dispatcher.UIThread.InvokeAsync(func);
}
