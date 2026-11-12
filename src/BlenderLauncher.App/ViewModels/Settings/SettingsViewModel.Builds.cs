using BlenderLauncher.App.Localisation;
using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.ViewModels.Settings;

/// <summary>Settings → Blender Builds.</summary>
public sealed partial class SettingsViewModel
{
    // Upstream's combo order (Major, Minor, Patch); the enum's numbers are the port's own.
    private static readonly UpdateBehaviour[] Behaviours = [UpdateBehaviour.Major, UpdateBehaviour.Minor, UpdateBehaviour.Patch];

    // "Mark as favorite" channels in upstream's combo order.
    private static readonly NewDownloadDefault[] DefaultChannels =
    [
        NewDownloadDefault.Stable, NewDownloadDefault.Daily, NewDownloadDefault.Experimental, NewDownloadDefault.Bforartists,
        NewDownloadDefault.UpbgeStable, NewDownloadDefault.UpbgeWeekly,
    ];

    private string _startupArgumentsDraft = "";
    private IReadOnlyList<string>? _minimumStableVersions;

    /// <summary>"Visibility and Downloading": one row per repository, as upstream.</summary>
    public IReadOnlyList<RepoRowViewModel> Repos { get; private set; } = [];

    /// <summary>The advanced update button's per-channel rows.</summary>
    public IReadOnlyList<UpdateRowViewModel> UpdateRows { get; private set; } = [];

    public IReadOnlyList<string> BehaviourNames { get; } =
        [Loc.Tr("ui.settings.builds.behaviour.major"), Loc.Tr("ui.settings.builds.behaviour.minor"), Loc.Tr("ui.settings.builds.behaviour.patch")];

    public IReadOnlyList<string> DefaultChannelNames { get; } =
    [
        Loc.Tr("ui.channel.stable"), Loc.Tr("ui.channel.daily"), Loc.Tr("act.tabs.experimental_and_patch"), Loc.Tr("ui.channel.bforartists"),
        Loc.Tr("ui.channel.upbge-stable"), Loc.Tr("ui.channel.upbge-weekly"),
    ];

    public IReadOnlyList<string> OnLaunchActionNames { get; } =
    [
        Loc.Tr("settings.blender_builds.on_blender_launch_actions.0"), Loc.Tr("settings.blender_builds.on_blender_launch_actions.1"),
        Loc.Tr("settings.blender_builds.on_blender_launch_actions.2"),
    ];

    public static int BehaviourIndexOf(UpdateBehaviour behaviour) => Math.Max(0, Array.IndexOf(Behaviours, behaviour));

    public static UpdateBehaviour? BehaviourAt(int index) => index >= 0 && index < Behaviours.Length ? Behaviours[index] : null;

    // ------------------------------------------------------------------ checking for builds

    /// <summary>
    /// The "major.minor" series offered as the minimum stable version: the API data's series (newest first), plus the
    /// saved value when the list lacks it.
    /// </summary>
    public IReadOnlyList<string> MinimumStableVersions => _minimumStableVersions ??= BuildMinimumStableVersions();

    public int MinimumStableIndex
    {
        get => Math.Max(0, MinimumStableVersions.ToList().IndexOf(Current.Builds.MinimumStableVersion));
        set
        {
            if (value >= 0 && value < MinimumStableVersions.Count)
            {
                var version = MinimumStableVersions[value];
                Update(s => s with { Builds = s.Builds with { MinimumStableVersion = version } });
            }
        }
    }

    public bool CheckAutomatically
    {
        get => Current.Builds.CheckAutomatically;
        set => Update(s => s with { Builds = s.Builds with { CheckAutomatically = value } });
    }

    public decimal? CheckIntervalHours
    {
        get => Current.Builds.CheckIntervalHours;
        set
        {
            if (value is { } v)
            {
                Update(s => s with { Builds = s.Builds with { CheckIntervalHours = SettingsValidation.ClampCheckInterval((int)v) } });
            }
        }
    }

    public static decimal MinimumCheckInterval => BuildsSettings.MinimumCheckIntervalHours;

    public static decimal MaximumCheckInterval => BuildsSettings.MaximumCheckIntervalHours;

    public bool CheckOnStartup
    {
        get => Current.Builds.CheckOnStartup;
        set => Update(s => s with { Builds = s.Builds with { CheckOnStartup = value } });
    }

    public bool ShowDailyArchive
    {
        get => Current.Builds.ShowDailyArchive;
        set => Update(s => s with { Builds = s.Builds with { ShowDailyArchive = value } });
    }

    public bool ShowExperimentalArchive
    {
        get => Current.Builds.ShowExperimentalArchive;
        set => Update(s => s with { Builds = s.Builds with { ShowExperimentalArchive = value } });
    }

    public bool ShowPatchArchive
    {
        get => Current.Builds.ShowPatchArchive;
        set => Update(s => s with { Builds = s.Builds with { ShowPatchArchive = value } });
    }

    // ------------------------------------------------------------------ downloading and saving

    public bool ShowUpdateButton
    {
        get => Current.Builds.ShowUpdateButton;
        set => Update(s => s with { Builds = s.Builds with { ShowUpdateButton = value } });
    }

    public int UpdateBehaviourIndex
    {
        get => BehaviourIndexOf(Current.Builds.UpdateBehaviour);
        set
        {
            if (BehaviourAt(value) is { } behaviour)
            {
                Update(s => s with { Builds = s.Builds with { UpdateBehaviour = behaviour } });
            }
        }
    }

    public bool UseAdvancedUpdateButton
    {
        get => Current.Builds.UseAdvancedUpdateButton;
        set => Update(s => s with { Builds = s.Builds with { UseAdvancedUpdateButton = value } });
    }

    public bool IsSimpleUpdateButton => !UseAdvancedUpdateButton;

    public string ShowUpdateButtonTooltip => Loc.Tr(UseAdvancedUpdateButton
        ? "settings.blender_builds.show_update_button_tooltip_disabled"
        : "settings.blender_builds.show_update_button_tooltip_normal").Trim();

    public bool MarkNewDownloadsAsDefault
    {
        get => Current.Builds.MarkNewDownloadsAsDefault != NewDownloadDefault.None;
        set
        {
            var channel = value ? DefaultChannels[_lastDefaultChannel] : NewDownloadDefault.None;
            Update(s => s with { Builds = s.Builds with { MarkNewDownloadsAsDefault = channel } });
        }
    }

    private int _lastDefaultChannel;

    public int DefaultChannelIndex
    {
        get => Current.Builds.MarkNewDownloadsAsDefault == NewDownloadDefault.None
            ? _lastDefaultChannel
            : Math.Max(0, Array.IndexOf(DefaultChannels, Current.Builds.MarkNewDownloadsAsDefault));
        set
        {
            if (value < 0 || value >= DefaultChannels.Length)
            {
                return;
            }

            _lastDefaultChannel = value;
            if (MarkNewDownloadsAsDefault)
            {
                Update(s => s with { Builds = s.Builds with { MarkNewDownloadsAsDefault = DefaultChannels[value] } });
            }
        }
    }

    public bool InstallTemplate
    {
        get => Current.Builds.InstallTemplate;
        set => Update(s => s with { Builds = s.Builds with { InstallTemplate = value } });
    }

    public bool FetchPrTitles
    {
        get => Current.Builds.FetchPrTitles;
        set => Update(s => s with { Builds = s.Builds with { FetchPrTitles = value } });
    }

    public bool PrependPrNumber
    {
        get => Current.Builds.PrependPrNumber;
        set => Update(s => s with { Builds = s.Builds with { PrependPrNumber = value } });
    }

    // ------------------------------------------------------------------ launching

    public bool QuickLaunchHotkeyEnabled
    {
        get => Current.Launching.QuickLaunchHotkeyEnabled;
        set => Update(s => s with { Launching = s.Launching with { QuickLaunchHotkeyEnabled = value } });
    }

    /// <summary>The hotkey as stored (<c>ctrl+alt+f11</c>); set by the key-capture box.</summary>
    public string QuickLaunchHotkey
    {
        get => Current.Launching.QuickLaunchHotkey;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Update(s => s with { Launching = s.Launching with { QuickLaunchHotkey = value } });
            }
        }
    }

    public int OnLaunchActionIndex
    {
        get => (int)Current.Launching.OnLaunchAction;
        set
        {
            if (Enum.IsDefined((OnLaunchAction)value))
            {
                Update(s => s with { Launching = s.Launching with { OnLaunchAction = (OnLaunchAction)value } });
            }
        }
    }

    public bool HideConsole
    {
        get => Current.Launching.HideConsole;
        set => Update(s => s with { Launching = s.Launching with { HideConsole = value } });
    }

    public string StartupArgumentsDraft
    {
        get => _startupArgumentsDraft;
        set => SetProperty(ref _startupArgumentsDraft, value ?? "");
    }

    private void InitialiseBuilds()
    {
        _startupArgumentsDraft = Current.Launching.BlenderStartupArguments;
        if (Array.IndexOf(DefaultChannels, Current.Builds.MarkNewDownloadsAsDefault) is var i and >= 0)
        {
            _lastDefaultChannel = i;
        }

        var store = _store;
        Repos =
        [
            new RepoRowViewModel(store, Channel.Stable, Loc.Tr("ui.channel.stable"), Loc.Tr("repo.stable_tooltip"),
                b => (b.ShowStable, b.ScrapeStable), (b, show, scrape) => b with { ShowStable = show, ScrapeStable = scrape }),
            new RepoRowViewModel(store, Channel.Daily, Loc.Tr("ui.channel.daily"), Loc.Tr("repo.daily_tooltip"),
                b => (b.ShowDaily, b.ScrapeDaily), (b, show, scrape) => b with { ShowDaily = show, ScrapeDaily = scrape }),
            new RepoRowViewModel(store, Channel.Experimental, Loc.Tr("act.tabs.experimental_and_patch"), Loc.Tr("repo.experimental_tooltip"),
                b => (b.ShowExperimentalAndPatch, b.ScrapeExperimental), (b, show, scrape) => b with { ShowExperimentalAndPatch = show, ScrapeExperimental = scrape }),
            new RepoRowViewModel(store, Channel.Bforartists, Loc.Tr("ui.channel.bforartists"), Loc.Tr("repo.bforartists_tooltip"),
                b => (b.ShowBforartists, b.ScrapeBforartists), (b, show, scrape) => b with { ShowBforartists = show, ScrapeBforartists = scrape }),
            new RepoRowViewModel(store, Channel.UpbgeStable, Loc.Tr("ui.channel.upbge-stable"), Loc.Tr("repo.upbge_tooltip"),
                b => (b.ShowUpbge, b.ScrapeUpbge), (b, show, scrape) => b with { ShowUpbge = show, ScrapeUpbge = scrape }),
            new RepoRowViewModel(store, Channel.UpbgeWeekly, Loc.Tr("ui.channel.upbge-weekly"), Loc.Tr("repo.upbge_weekly_tooltip"),
                b => (b.ShowUpbgeWeekly, b.ScrapeUpbgeWeekly), (b, show, scrape) => b with { ShowUpbgeWeekly = show, ScrapeUpbgeWeekly = scrape }),
        ];

        const string p = "settings.blender_builds.";
        UpdateRows =
        [
            Row("stable", b => (b.ShowStableUpdateButton, b.StableUpdateBehaviour),
                (b, show, behaviour) => b with { ShowStableUpdateButton = show, StableUpdateBehaviour = behaviour }),
            Row("daily", b => (b.ShowDailyUpdateButton, b.DailyUpdateBehaviour),
                (b, show, behaviour) => b with { ShowDailyUpdateButton = show, DailyUpdateBehaviour = behaviour }),
            Row("experimental", b => (b.ShowExperimentalUpdateButton, b.ExperimentalUpdateBehaviour),
                (b, show, behaviour) => b with { ShowExperimentalUpdateButton = show, ExperimentalUpdateBehaviour = behaviour }),
            Row("bfa", b => (b.ShowBforartistsUpdateButton, b.BforartistsUpdateBehaviour),
                (b, show, behaviour) => b with { ShowBforartistsUpdateButton = show, BforartistsUpdateBehaviour = behaviour }),
            Row("upbge_stable", b => (b.ShowUpbgeStableUpdateButton, b.UpbgeStableUpdateBehaviour),
                (b, show, behaviour) => b with { ShowUpbgeStableUpdateButton = show, UpbgeStableUpdateBehaviour = behaviour }),
            Row("upbge_weekly", b => (b.ShowUpbgeWeeklyUpdateButton, b.UpbgeWeeklyUpdateBehaviour),
                (b, show, behaviour) => b with { ShowUpbgeWeeklyUpdateButton = show, UpbgeWeeklyUpdateBehaviour = behaviour }),
        ];

        UpdateRowViewModel Row(string key, Func<BuildsSettings, (bool, UpdateBehaviour)> read, Func<BuildsSettings, bool, UpdateBehaviour, BuildsSettings> write) =>
            new(store, Loc.Tr(p + "show_" + key + "_update_button"), Loc.Tr(p + "show_" + key + "_update_button_tooltip").Trim(),
                Loc.Tr(p + "update_" + key + "_behavior_tooltip").Trim(), read, write) { BehaviourNames = BehaviourNames };
    }

    private void CommitBuildsDrafts()
    {
        var arguments = _startupArgumentsDraft.Trim();
        Update(s => s with { Launching = s.Launching with { BlenderStartupArguments = arguments } });
    }

    private List<string> BuildMinimumStableVersions()
    {
        var data = _environment.ApiData ?? Services?.ApiData.Current;
        if (data is null || data.Series.Count == 0)
        {
            ApiData.TryParse(BundledApiFiles.ReadApi(), out var bundled);
            data = bundled;
        }

        var versions = data.Series.Select(s => s.ToString()).Distinct(StringComparer.Ordinal).ToList();
        if (!versions.Contains(Current.Builds.MinimumStableVersion, StringComparer.Ordinal))
        {
            versions.Add(Current.Builds.MinimumStableVersion);
        }

        return versions;
    }
}
