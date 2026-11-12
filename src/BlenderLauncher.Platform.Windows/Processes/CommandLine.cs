using BlenderLauncher.Core.Processes;

namespace BlenderLauncher.Platform.Windows.Processes;

/// <summary>
/// Builds Windows command lines for ordinary programs. The rules (CommandLineToArgvW / MSVC CRT) live in
/// <see cref="WindowsCommandLine"/> in Core, which is tested against shell32; this class only forwards to it.
/// For <c>.bat</c> and <c>.cmd</c>, use <c>LaunchArgsBuilder</c>, which builds the <c>cmd /S /C</c> string.
/// </summary>
public static class CommandLine
{
    /// <summary>Joins the program path and arguments into one command line.</summary>
    public static string Build(string fileName, IEnumerable<string> arguments) =>
        WindowsCommandLine.Build(fileName, arguments);

    /// <summary>Quotes one argument when it needs it.</summary>
    public static string Quote(string argument) => WindowsCommandLine.Quote(argument);
}
