using System.Text.Json.Nodes;

namespace BlenderLauncher.Core.Settings;

/// <summary>
/// One schema step: rewrites a <c>settings.json</c> document of version <see cref="FromVersion"/> into version
/// <c>FromVersion + 1</c>, in place, before the typed model is bound.
/// </summary>
public sealed record SettingsMigration(int FromVersion, Action<JsonObject> Apply);

/// <summary>The migration pipeline: one step per schema bump.</summary>
public static class SettingsMigrations
{
    /// <summary>
    /// The steps this build knows, in order. Version 1 is the first schema, so there are none yet; a file without
    /// <c>schemaVersion</c> reads as version 1. The next bump adds <c>new(1, doc => …)</c> here.
    /// </summary>
    public static IReadOnlyList<SettingsMigration> All { get; } = [];

    /// <summary>
    /// Runs the steps that take <paramref name="document"/> from <paramref name="fromVersion"/> to
    /// <paramref name="toVersion"/> and sets its <c>schemaVersion</c>. Returns the version reached. A missing step
    /// stops the pipeline there (the document keeps the version it reached, so the next build can finish it).
    /// </summary>
    public static int Run(JsonObject document, int fromVersion, int toVersion, IReadOnlyList<SettingsMigration> steps)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(steps);
        var version = fromVersion;
        while (version < toVersion)
        {
            var step = steps.FirstOrDefault(s => s.FromVersion == version);
            if (step is null)
            {
                break;
            }

            step.Apply(document);
            version++;
            document["schemaVersion"] = version;
        }

        return version;
    }
}
