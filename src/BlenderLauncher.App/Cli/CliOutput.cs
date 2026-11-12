using Avalonia;
using Avalonia.Controls;
using BlenderLauncher.App.Lifetime;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.Views.Dialogs;
using BlenderLauncher.Core;
using BlenderLauncher.Platform.Windows.Cli;

namespace BlenderLauncher.App.Cli;

/// <summary>
/// Text the command line prints. The exe uses the GUI subsystem, so it prints through the console it was started from
/// (<see cref="ParentConsole"/>) or to redirected output. Started with no console (Explorer, a shortcut), help and
/// errors are shown in a themed dialog instead of upstream's <c>cmd /c type … &amp;&amp; pause</c>.
/// </summary>
internal static class CliOutput
{
    private static bool? _console;

    /// <summary>True when standard output reaches a console or a file/pipe (attaching once).</summary>
    public static bool HasConsole => _console ??= ParentConsole.TryAttach(out _) is
        ConsoleAttachOutcome.Attached or ConsoleAttachOutcome.Redirected or ConsoleAttachOutcome.AlreadyAttached;

    public static string ExeName => Path.GetFileName(Environment.ProcessPath) is { Length: > 0 } name ? name : AppIdentity.ExecutableName;

    /// <summary><c>--help</c>: exit code 0.</summary>
    public static int Help(string[] args)
    {
        var usage = CommandLine.Usage(ExeName);
        if (HasConsole)
        {
            Console.Out.WriteLine(usage);
            Console.Out.Flush();
            return 0;
        }

        ShowDialog(args, DialogKind.Info, Loc.Tr("ui.launch_picker.help_title"), $"{AppIdentity.DisplayName} {AppIdentity.Version}", usage);
        return 0;
    }

    /// <summary>A command line that does not parse: the error and the usage, exit code 2.</summary>
    public static int UsageError(string[] args, string error)
    {
        var usage = CommandLine.Usage(ExeName);
        if (HasConsole)
        {
            Console.Error.WriteLine($"{ExeName}: error: {error}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(usage);
            Console.Error.Flush();
            return CommandLine.UsageExitCode;
        }

        ShowDialog(args, DialogKind.Error, Loc.Tr("ui.launch_picker.help_title"), $"error: {error}", usage);
        return CommandLine.UsageExitCode;
    }

    /// <summary>Prints a line to standard output when there is somewhere to print it.</summary>
    public static void Line(string text = "")
    {
        if (HasConsole)
        {
            Console.Out.WriteLine(text);
            Console.Out.Flush();
        }
    }

    public static void Error(string text)
    {
        if (HasConsole)
        {
            Console.Error.WriteLine(text);
            Console.Error.Flush();
        }
    }

    /// <summary>Runs Avalonia for one themed dialog and returns when it closes.</summary>
    public static void ShowDialog(string[] args, DialogKind kind, string title, string message, string? scrollText)
    {
        App.MainWindowFactory = () =>
        {
            var dialog = new DialogWindow(new DialogRequest
            {
                Kind = kind,
                Title = title,
                Message = message,
                KeepLineBreaks = true,
                ScrollText = scrollText,
                ScrollTextMonospaced = true,
                Buttons = DialogService.OkButtons(),
                Width = 860,
            })
            {
                ShowInTaskbar = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            WindowPositionOverride.Apply(dialog);
            return dialog;
        };
        Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
}
