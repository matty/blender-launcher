using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Logging;

namespace BlenderLauncher.Core.Settings;

/// <summary>What <see cref="DefaultBuildImport.Reconcile"/> did.</summary>
public enum DefaultBuildImportOutcome
{
    /// <summary>The settings carried no default build.</summary>
    NothingToImport,

    /// <summary>The settings' default build became the library's and was cleared from the settings.</summary>
    Moved,

    /// <summary>The library already had a default build; the settings' value was cleared and ignored.</summary>
    LibraryAlreadySet,
}

/// <summary>
/// Reconciles the two places a default build can be recorded. The per-library pointer in
/// <see cref="LibraryState"/> is authoritative; <see cref="BuildsSettings.DefaultBuild"/> only carries the value
/// the upstream import found (upstream's first quick-launch path). On the first load of a library, when the
/// library has no default and the settings have one, it is moved over; either way the setting is then cleared,
/// so it is applied once.
/// </summary>
public static class DefaultBuildImport
{
    /// <summary>
    /// Applies the rule to the library <paramref name="library"/> has scanned. A relative setting is taken against
    /// the library root. Call after the first scan of a library.
    /// </summary>
    public static DefaultBuildImportOutcome Reconcile(LibraryService library, SettingsStore settings, Log? log = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Current.Builds.DefaultBuild is not { Length: > 0 } carried || library.LibraryRoot is not { } root)
        {
            return DefaultBuildImportOutcome.NothingToImport;
        }

        DefaultBuildImportOutcome outcome;
        if (library.DefaultBuild is null)
        {
            var folder = Path.IsPathFullyQualified(carried) ? carried : Path.Combine(root, carried);
            library.SetDefaultBuild(LibraryLayout.NormaliseRoot(folder));
            log?.Info($"Default build '{carried}' moved from the settings to the library state of {root}");
            outcome = DefaultBuildImportOutcome.Moved;
        }
        else
        {
            log?.Info($"Library {root} already has a default build; the imported '{carried}' is dropped");
            outcome = DefaultBuildImportOutcome.LibraryAlreadySet;
        }

        settings.Update(s => s with { Builds = s.Builds with { DefaultBuild = null } });
        return outcome;
    }
}
