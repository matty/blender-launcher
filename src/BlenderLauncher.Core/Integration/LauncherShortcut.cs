using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.PlatformServices;

namespace BlenderLauncher.Core.Integration;

/// <summary>Where the launcher's own shortcut goes.</summary>
public enum ShortcutLocation
{
    StartMenu,
    Desktop,
}

/// <summary>What <see cref="LauncherShortcut.CreateAsync"/> did.</summary>
public enum ShortcutOutcome
{
    Created,

    /// <summary>A shortcut was there and was overwritten (ours silently, another one after the user agreed).</summary>
    Replaced,

    /// <summary>A shortcut to something else was there and the user chose to keep it.</summary>
    Kept,

    /// <summary>The platform has no such folder or no shortcut support.</summary>
    Unavailable,
}

/// <summary>
/// The launcher's own <c>Blender Launcher.lnk</c> in the Start Menu's Programs folder or inside the Desktop folder
/// (upstream wrote <c>Desktop.lnk</c> beside the Desktop folder). Upstream's shortcut has the same
/// name, so an existing shortcut that does not start this executable is replaced only after asking.
/// </summary>
public static class LauncherShortcut
{
    public const string FileName = AppIdentity.DisplayName + ".lnk";

    /// <summary>The shortcut's path at <paramref name="location"/>, or null when the platform has no such folder.</summary>
    public static string? PathFor(IShortcutService shortcuts, ShortcutLocation location)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        if (!shortcuts.IsAvailable)
        {
            return null;
        }

        var folder = location == ShortcutLocation.StartMenu ? shortcuts.StartMenuFolder : shortcuts.DesktopFolder;
        return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, FileName);
    }

    /// <summary>The shortcut to <paramref name="exePath"/>: no arguments, started in the exe's folder, the exe's icon.</summary>
    public static ShortcutSpec SpecFor(string exePath) =>
        new(exePath, "", Path.GetDirectoryName(exePath) ?? "", exePath, 0, AppIdentity.DisplayName);

    /// <summary>True when <paramref name="spec"/> starts <paramref name="exePath"/>.</summary>
    public static bool PointsAt(ShortcutSpec? spec, string exePath) =>
        spec is not null && spec.TargetPath.Length > 0 && SamePath(spec.TargetPath, exePath);

    /// <summary>
    /// Writes the shortcut. An existing file that is not a shortcut to <paramref name="exePath"/> is replaced only when
    /// <paramref name="confirmReplace"/> (given the path) agrees.
    /// </summary>
    public static async Task<ShortcutOutcome> CreateAsync(IShortcutService shortcuts, ShortcutLocation location, string exePath,
        Func<string, Task<bool>> confirmReplace)
    {
        ArgumentNullException.ThrowIfNull(confirmReplace);
        if (PathFor(shortcuts, location) is not { } path)
        {
            return ShortcutOutcome.Unavailable;
        }

        var replaced = false;
        if (File.Exists(path))
        {
            if (!PointsAt(shortcuts.TryRead(path), exePath) && !await confirmReplace(path).ConfigureAwait(false))
            {
                return ShortcutOutcome.Kept;
            }

            replaced = true;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        shortcuts.Write(path, SpecFor(exePath));
        return replaced ? ShortcutOutcome.Replaced : ShortcutOutcome.Created;
    }

    /// <summary>Deletes the shortcut at <paramref name="location"/> when it starts <paramref name="exePath"/>; true if one was deleted.</summary>
    public static bool RemoveIfOurs(IShortcutService shortcuts, ShortcutLocation location, string exePath)
    {
        if (PathFor(shortcuts, location) is not { } path || !File.Exists(path) || !PointsAt(shortcuts.TryRead(path), exePath))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return LibraryLayout.PathComparer.Equals(Path.GetFullPath(a), Path.GetFullPath(b));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>The Run value's command line: the quoted executable path, nothing else.</summary>
public static class StartupCommand
{
    /// <summary><c>"C:\Apps\Blender Launcher\Blender Launcher.exe"</c>.</summary>
    public static string Format(string exePath) => "\"" + exePath + "\"";

    /// <summary>The executable a Run value starts: the quoted first token, or the whole value when unquoted (upstream's form).</summary>
    public static string? ExecutableOf(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
        {
            return null;
        }

        if (v[0] == '"')
        {
            var end = v.IndexOf('"', 1);
            return end > 1 ? v[1..end] : null;
        }

        // Unquoted: upstream stored the bare path, which may contain spaces; take it whole when it ends in .exe.
        var exe = v.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? v[..(exe + 4)] : v;
    }

    /// <summary>True when <paramref name="value"/> starts <paramref name="exePath"/>.</summary>
    public static bool Matches(string? value, string exePath)
    {
        if (ExecutableOf(value) is not { } exe)
        {
            return false;
        }

        try
        {
            return LibraryLayout.PathComparer.Equals(Path.GetFullPath(exe), Path.GetFullPath(exePath));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
