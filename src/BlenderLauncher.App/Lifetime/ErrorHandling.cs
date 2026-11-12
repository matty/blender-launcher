using System.Runtime.InteropServices;
using Avalonia.Threading;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Logging;

namespace BlenderLauncher.App.Lifetime;

/// <summary>
/// Unhandled errors. Every handler logs the exception with the version and OS. Exceptions on the UI thread are
/// marked handled and shown in an error window with "Open log folder", so the launcher carries on instead of closing
/// silently; an unobserved task exception is logged and observed; an AppDomain exception (fatal) is logged and the log
/// flushed before the process ends.
/// </summary>
internal static class ErrorHandling
{
    private const int MaxDialogs = 3;
    private static int _dialogsShown;
    private static bool _dialogOpen;
    private static bool _processHandlersInstalled;
    private static bool _uiHandlerInstalled;

    /// <summary>The log to write to; null before it is open and after it is closed (then a temp file is used).</summary>
    public static Log? Log { get; set; }

    /// <summary>The folder "Open log folder" opens.</summary>
    public static string? LogFolder { get; set; }

    /// <summary>AppDomain and TaskScheduler handlers. Call first thing in <c>Main</c>.</summary>
    public static void InstallProcessHandlers()
    {
        if (_processHandlersInstalled)
        {
            return;
        }

        _processHandlersInstalled = true;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Write($"AppDomain, terminating={e.IsTerminating}", e.ExceptionObject as Exception);
            try
            {
                Log?.Flush();
            }
            catch (Exception)
            {
                // Nothing more can be done while the process is going down.
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>The UI dispatcher handler. Call once Avalonia is running.</summary>
    public static void InstallUiHandler()
    {
        if (_uiHandlerInstalled)
        {
            return;
        }

        _uiHandlerInstalled = true;
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Write("UI thread", e.Exception);
            ShowDialog(e.Exception);
        };
    }

    /// <summary>The log line's header: where it came from, the version and the OS.</summary>
    public static string Header(string source) =>
        $"Unhandled exception ({source}); {AppIdentity.DisplayName} {AppIdentity.Version}; " +
        $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}; {RuntimeInformation.FrameworkDescription}";

    private static void Write(string source, Exception? exception)
    {
        var header = Header(source);
        if (Log is { } log)
        {
            log.Error(header, exception);
            return;
        }

        try
        {
            var file = Path.Combine(Path.GetTempPath(), AppIdentity.TechnicalId + "-crash.log");
            File.AppendAllText(file, $"{DateTimeOffset.Now:O} {header}{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No log and no temp folder: nothing left to write to.
        }
    }

    private static void ShowDialog(Exception exception)
    {
        if (_dialogOpen || _dialogsShown >= MaxDialogs)
        {
            return;
        }

        _dialogsShown++;
        _dialogOpen = true;
        try
        {
            var shown = DialogService.ErrorAsync(
                Loc.Tr("ui.error.title"),
                Loc.Tr("ui.error.message"),
                details: $"{exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{exception.StackTrace}",
                logFolder: LogFolder);
            shown.ContinueWith(_ => _dialogOpen = false, TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (Exception e)
        {
            _dialogOpen = false;
            Write("error dialog", e);
        }
    }
}
