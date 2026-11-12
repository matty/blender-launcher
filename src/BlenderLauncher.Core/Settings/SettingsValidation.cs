using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Net;
using BlenderLauncher.Core.Versioning;

namespace BlenderLauncher.Core.Settings;

/// <summary>
/// The allowed range of each setting, applied on load and on every change (<see cref="AppSettings.Normalised"/>).
/// Out-of-range numbers are clamped, as upstream did for the check interval; anything that cannot be repaired
/// goes back to its default.
/// </summary>
public static class SettingsValidation
{
    /// <summary>Upstream's <c>language</c> values.</summary>
    public static IReadOnlyList<string> Languages { get; } = ["auto", "en", "es", "fr", "ja", "zh"];

    /// <summary>The lowest <see cref="GeneralSettings.LaunchTimerSeconds"/>: -1, disabled.</summary>
    public const int MinimumLaunchTimer = -1;

    public static AppSettings Normalise(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with
        {
            SchemaVersion = Math.Max(settings.SchemaVersion, 0),
            General = Normalise(settings.General ?? new GeneralSettings()),
            Appearance = Normalise(settings.Appearance ?? new AppearanceSettings()),
            Connection = Normalise(settings.Connection ?? new ConnectionSettings()),
            Builds = Normalise(settings.Builds ?? new BuildsSettings()),
            Launching = Normalise(settings.Launching ?? new LaunchingSettings()),
            Internal = Normalise(settings.Internal ?? new InternalSettings()),
        };
    }

    /// <summary>Clamps a check interval to 6..596 hours.</summary>
    public static int ClampCheckInterval(int hours) =>
        Math.Clamp(hours, BuildsSettings.MinimumCheckIntervalHours, BuildsSettings.MaximumCheckIntervalHours);

    /// <summary>
    /// Reads a <c>minimum_blender_stable_version</c> value as <c>"major.minor"</c>, or null when it is not a version.
    /// <c>"None"</c> reads as the default, as upstream.
    /// </summary>
    public static string? NormaliseMinimumStableVersion(string? text)
    {
        if (text is null)
        {
            return null;
        }

        text = text.Trim();
        if (text == "None")
        {
            return BuildsSettings.DefaultMinimumStableVersion;
        }

        return text.Contains('.', StringComparison.Ordinal) && SemVer.TryParse(text, out var v, optionalMinorAndPatch: true)
            ? $"{v.Major}.{v.Minor}"
            : null;
    }

    private static GeneralSettings Normalise(GeneralSettings g) => g with
    {
        LibraryFolder = string.IsNullOrWhiteSpace(g.LibraryFolder) ? null : g.LibraryFolder.Trim(),
        Language = g.Language is not null && Languages.Contains(g.Language) ? g.Language : "auto",
        WorkerThreadCount = g.WorkerThreadCount is { } n ? Math.Max(1, n) : null,
        LaunchTimerSeconds = Math.Max(g.LaunchTimerSeconds, MinimumLaunchTimer),
        DefaultDeleteAction = Defined(g.DefaultDeleteAction, DeleteAction.SendToTrash),
        LogLevel = Defined(g.LogLevel, LogLevel.Info),
    };

    private static AppearanceSettings Normalise(AppearanceSettings a) => a with
    {
        DefaultView = SidebarView.TryParse(a.DefaultView, out var view) ? view.Value.ToString() : SidebarView.Default.ToString(),
        Accent = Defined(a.Accent, Accent.Blue),
        Density = Defined(a.Density, Density.Comfortable),
    };

    private static ConnectionSettings Normalise(ConnectionSettings c)
    {
        var userId = c.UserId?.Trim();
        return c with
        {
            UserId = ConnectionOptions.IsValidUserId(userId) ? userId : null,
            ProxyType = Defined(c.ProxyType, ProxyType.None),
            ProxyHost = string.IsNullOrWhiteSpace(c.ProxyHost) ? ConnectionOptions.DefaultProxyHost : c.ProxyHost.Trim(),
            ProxyPort = c.ProxyPort is >= 1 and <= 65535 ? c.ProxyPort : ConnectionOptions.DefaultProxyPort,
        };
    }

    private static BuildsSettings Normalise(BuildsSettings b) => b with
    {
        MinimumStableVersion = NormaliseMinimumStableVersion(b.MinimumStableVersion) ?? BuildsSettings.DefaultMinimumStableVersion,
        CheckIntervalHours = ClampCheckInterval(b.CheckIntervalHours),
        UpdateBehaviour = Defined(b.UpdateBehaviour, UpdateBehaviour.Patch),
        StableUpdateBehaviour = Defined(b.StableUpdateBehaviour, UpdateBehaviour.Patch),
        DailyUpdateBehaviour = Defined(b.DailyUpdateBehaviour, UpdateBehaviour.Patch),
        ExperimentalUpdateBehaviour = Defined(b.ExperimentalUpdateBehaviour, UpdateBehaviour.Patch),
        BforartistsUpdateBehaviour = Defined(b.BforartistsUpdateBehaviour, UpdateBehaviour.Patch),
        UpbgeStableUpdateBehaviour = Defined(b.UpbgeStableUpdateBehaviour, UpdateBehaviour.Patch),
        UpbgeWeeklyUpdateBehaviour = Defined(b.UpbgeWeeklyUpdateBehaviour, UpdateBehaviour.Patch),
        MarkNewDownloadsAsDefault = Defined(b.MarkNewDownloadsAsDefault, NewDownloadDefault.None),
        DefaultBuild = string.IsNullOrWhiteSpace(b.DefaultBuild) ? null : b.DefaultBuild.Trim(),
        VersionSpecificQueries = b.VersionSpecificQueries ?? new Dictionary<string, string>(),
    };

    private static LaunchingSettings Normalise(LaunchingSettings l) => l with
    {
        OnLaunchAction = Defined(l.OnLaunchAction, OnLaunchAction.DoNothing),
        BlenderStartupArguments = (l.BlenderStartupArguments ?? "").Trim(),
        BashArguments = (l.BashArguments ?? "").Trim(),
        QuickLaunchHotkey = string.IsNullOrWhiteSpace(l.QuickLaunchHotkey)
            ? LaunchingSettings.DefaultQuickLaunchHotkey
            : l.QuickLaunchHotkey.Trim().ToLowerInvariant(),
    };

    private static InternalSettings Normalise(InternalSettings i)
    {
        var window = i.Window ?? new WindowPlacement();
        return i with
        {
            UpstreamRelativeLibraryFolder = string.IsNullOrWhiteSpace(i.UpstreamRelativeLibraryFolder) ? null : i.UpstreamRelativeLibraryFolder,
            DismissedUpdateVersion = string.IsNullOrWhiteSpace(i.DismissedUpdateVersion) ? null : i.DismissedUpdateVersion.Trim(),
            Window = window with
            {
                Width = window.Width >= WindowPlacement.MinimumWidth ? window.Width : WindowPlacement.DefaultWidth,
                Height = window.Height >= WindowPlacement.MinimumHeight ? window.Height : WindowPlacement.DefaultHeight,
            },
            ViewSorts = NormaliseSorts(i.ViewSorts),
            CollapsedDetailSections = i.CollapsedDetailSections is null
                ? []
                : i.CollapsedDetailSections.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToArray(),
        };
    }

    private static IReadOnlyDictionary<string, ViewSort> NormaliseSorts(IReadOnlyDictionary<string, ViewSort>? sorts)
    {
        var result = new Dictionary<string, ViewSort>(StringComparer.Ordinal);
        foreach (var (view, sort) in sorts ?? new Dictionary<string, ViewSort>())
        {
            if (sort is not null && !string.IsNullOrWhiteSpace(view))
            {
                result[view] = sort with { Column = Defined(sort.Column, SortColumn.Built) };
            }
        }

        return result;
    }

    private static T Defined<T>(T value, T fallback)
        where T : struct, Enum => Enum.IsDefined(value) ? value : fallback;
}
