namespace BlenderLauncher.Core.Settings;

/// <summary>
/// The new-user channel defaults: Stable, LTS, Daily and Experimental are on for a
/// new user; Bforartists, UPBGE and UPBGE Weekly are off until enabled. <see cref="AppSettings"/> keeps upstream's
/// defaults (Bforartists and UPBGE on), because the upstream importer applies only the keys an INI contains over the
/// current settings, and an upstream user who never touched those toggles has them on. So the decision is applied
/// here, by the first-run wizard, and only to settings that did not come from an upstream import.
/// </summary>
public static class FirstRunDefaults
{
    /// <summary>
    /// <paramref name="settings"/> with Bforartists, UPBGE and UPBGE Weekly hidden and not checked when they are a new
    /// user's settings. Settings imported from upstream (<see cref="InternalSettings.UpstreamImportDone"/>) keep the
    /// user's values, and so do settings of someone who already finished the wizard (<c>-force-first-time</c>).
    /// </summary>
    public static AppSettings Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Internal.UpstreamImportDone || settings.Internal.FirstRunSeen)
        {
            return settings;
        }

        return settings with
        {
            Builds = settings.Builds with
            {
                ShowBforartists = false,
                ScrapeBforartists = false,
                ShowUpbge = false,
                ScrapeUpbge = false,
                ShowUpbgeWeekly = false,
                ScrapeUpbgeWeekly = false,
            },
        };
    }
}
