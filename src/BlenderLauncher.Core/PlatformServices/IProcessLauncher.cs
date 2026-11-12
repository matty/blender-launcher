using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.Core.PlatformServices;

/// <summary>A process started by an <see cref="IProcessLauncher"/>. Owns the OS handle; dispose to release it.</summary>
public interface ILaunchedProcess : IDisposable
{
    /// <summary>The process id.</summary>
    int Id { get; }

    /// <summary>Completes with the exit code when the process exits. Waits without polling.</summary>
    Task<int> WaitForExitAsync(CancellationToken ct = default);
}

/// <summary>
/// Starts Blender builds and asks them to close. Implemented by <c>Platform.Windows</c>;
/// <see cref="NullProcessLauncher"/> stands in when there is none.
/// </summary>
public interface IProcessLauncher
{
    /// <summary>False when this implementation cannot start anything; the UI then hides Launch.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Starts <paramref name="command"/> in <paramref name="workingDirectory"/> with the launcher's environment.
    /// The console is hidden when <see cref="LaunchCommand.HideConsole"/> is set; a batch file runs through
    /// <c>%ComSpec%</c>.
    /// </summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The process could not be started.</exception>
    /// <exception cref="PlatformNotSupportedException">The launcher is the null one.</exception>
    ILaunchedProcess Start(LaunchCommand command, string workingDirectory);

    /// <summary>
    /// Asks the processes <paramref name="processIds"/> to close by sending each of their visible top-level windows
    /// a close request (as the window's close button would). Nothing is killed; Blender may ask to save first.
    /// Returns the number of windows asked.
    /// </summary>
    int RequestClose(IReadOnlyCollection<int> processIds);
}

/// <summary>The launcher used when the platform has none: starts nothing.</summary>
public sealed class NullProcessLauncher : IProcessLauncher
{
    public static NullProcessLauncher Instance { get; } = new();

    public bool IsAvailable => false;

    public ILaunchedProcess Start(LaunchCommand command, string workingDirectory) =>
        throw new PlatformNotSupportedException("Launching builds is not supported on this platform");

    public int RequestClose(IReadOnlyCollection<int> processIds) => 0;
}
