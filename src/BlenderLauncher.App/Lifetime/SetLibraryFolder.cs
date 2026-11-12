using BlenderLauncher.Core;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Platform.Windows.Cli;

namespace BlenderLauncher.App.Lifetime;

/// <summary>
/// Upstream's <c>-set-library-folder PATH</c>: validates the
/// folder, saves it as the library, creates the library subfolders, then lets normal startup continue. An invalid
/// folder is reported and the process exits with code 1.
/// <para>
/// Deviation: a folder that does not exist yet is created first (upstream refuses it), so a new library can be
/// chosen in one step. The switch is parsed by <see cref="Cli.CommandLine"/>.
/// </para>
/// </summary>
internal static class SetLibraryFolder
{
    /// <summary>Applies the folder. Returns null on success (startup continues) or an exit code on failure.</summary>
    public static int? Apply(string path)
    {
        var dirs = DataDirectories.Resolve();
        var baseDir = AppLocation.BaseDirectory;

        string? message = null;
        try
        {
            if (path.Length > 0)
            {
                Directory.CreateDirectory(LibraryLayout.Resolve(path, baseDir));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            message = $"Cannot create the library folder '{path}': {e.Message}";
        }

        LibraryFolderValidation? check = null;
        if (message is null)
        {
            check = LibraryLayout.Validate(path, baseDir);
            if (check.Problem is { } problem)
            {
                message = $"'{path}' cannot be used as a library folder ({problem}).";
            }
        }

        if (message is not null || check?.FullPath is null)
        {
            Report(message ?? $"'{path}' cannot be used as a library folder.");
            return 1;
        }

        var root = check.FullPath;

        // Load, change and flush the settings synchronously: this runs before Avalonia and before AppServices.
        var store = SettingsStore.LoadAsync(new SettingsStoreOptions { FilePath = dirs.SettingsFile }).GetAwaiter().GetResult();
        try
        {
            store.Update(s => s with { General = s.General with { LibraryFolder = root } });
            store.FlushAsync().GetAwaiter().GetResult();
        }
        finally
        {
            store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        foreach (var warning in check.Warnings)
        {
            Report($"Warning: {warning} ({root}).");
        }

        return null;
    }

    private static void Report(string text)
    {
        // A GUI-subsystem exe: print to the console it was started from when there is one.
        if (ParentConsole.TryAttach(out _) is ConsoleAttachOutcome.Attached or ConsoleAttachOutcome.Redirected)
        {
            Console.Error.WriteLine(text);
        }
    }
}
