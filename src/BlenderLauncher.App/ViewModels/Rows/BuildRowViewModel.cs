using Avalonia.Media;
using BlenderLauncher.App.Theme;
using BlenderLauncher.Core.Builds;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlenderLauncher.App.ViewModels.Rows;

/// <summary>
/// What an installed row and a download row have in common: the build's identity and display fields, the channel and
/// its colours. A projection of Core state: <see cref="BuildCatalog"/> creates and updates rows from
/// the services' events on the UI thread; views, the detail panel and the context menu only read them (actions go
/// through <see cref="BuildActions"/>). Shared by every region, so the lists, the detail panel and the context menu
/// all see the same instance for the same build.
/// </summary>
public abstract partial class BuildRowViewModel : ViewModelBase
{
    /// <summary>Stable identity: the build folder for installed rows, the de-duplication key for downloads.</summary>
    public abstract string Id { get; }

    /// <summary>True for <see cref="InstalledBuildRowViewModel"/>.</summary>
    public abstract bool IsInstalledRow { get; }

    /// <summary>The build's <c>.blinfo</c> fields; null for an unrecognised or damaged folder.</summary>
    [ObservableProperty]
    public partial BuildInfo? Build { get; protected set; }

    /// <summary>"5.2.2" (<see cref="BuildInfo.DisplayVersion"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string Version { get; protected set; } = "";

    /// <summary>The prerelease label beside the version: "Alpha", "Beta", "RC"; empty for releases.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLabel), nameof(AccessibleName))]
    public partial string Label { get; protected set; } = "";

    public bool HasLabel => Label.Length > 0;

    /// <summary>The row's name for screen readers and UI Automation: "5.4.0 Alpha, Daily".</summary>
    public virtual string AccessibleName => (HasLabel ? Version + " " + Label : Version) + ", " + ChannelName;

    /// <summary>The row's second line and the detail panel's subtitle: "Blender 5.2 LTS", "Blender 5.4 Alpha", or the custom name.</summary>
    [ObservableProperty]
    public partial string Name { get; protected set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChannelKey), nameof(ChannelName), nameof(ChannelColours), nameof(ChannelForeground),
        nameof(ChannelBackground), nameof(CanReplaceOnNewBuild), nameof(AccessibleName))]
    public partial Channel Channel { get; protected set; }

    /// <summary>"stable", "lts", "daily", … (the sidebar key; see <see cref="DisplayFormat.ChannelKey"/>).</summary>
    public string ChannelKey => DisplayFormat.ChannelKey(Channel);

    /// <summary>The pill text: "Stable", "LTS", …</summary>
    public string ChannelName => DisplayFormat.ChannelName(Channel);

    public ChannelColours ChannelColours => ChannelPalette.For(Channel);

    /// <summary>Pill text colour and "New" tag text colour.</summary>
    public IBrush ChannelForeground => ChannelColours.Foreground;

    /// <summary>Pill background and "New" tag background (the channel colour at 14%).</summary>
    public IBrush ChannelBackground => ChannelColours.Background;

    /// <summary>"Replace on new build" is offered for Daily and Experimental (and patch) builds only.</summary>
    public bool CanReplaceOnNewBuild => Channel is Channel.Daily or Channel.Experimental or Channel.Patch;

    /// <summary>The branch as the design shows it: "v5.2.2", "main", "npr-prototype".</summary>
    [ObservableProperty]
    public partial string Branch { get; protected set; } = "";

    /// <summary>The full commit hash, or null when unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShortHash))]
    public partial string? Hash { get; protected set; }

    /// <summary>The first seven characters of <see cref="Hash"/>; empty when unknown.</summary>
    public string ShortHash => DisplayFormat.ShortHash(Hash);

    /// <summary>The commit (build) time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BuiltText))]
    public partial DateTimeOffset? BuiltAt { get; protected set; }

    /// <summary>"Oct 14, 2026".</summary>
    public string BuiltText => DisplayFormat.Date(BuiltAt);

    /// <summary>Archive size for downloads, folder size for installed builds; null when not known (yet).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial long? SizeBytes { get; protected set; }

    /// <summary>"412 MB", or a dash.</summary>
    public string SizeText => DisplayFormat.Size(SizeBytes);

    /// <summary>The PR title of a patch build, or the design's description; null hides the description card.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    public partial string? Description { get; protected set; }

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    /// <summary>The bundled Python version ("3.11.9"); installed builds only, null when not read yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PythonText), nameof(HasPython))]
    public partial string? PythonVersion { get; protected set; }

    public bool HasPython => PythonVersion is not null;

    public string PythonText => PythonVersion ?? Localisation.Loc.Tr("ui.units.unknown");

    /// <summary>A download newer than the last check and not installed (the "New" tag and the sidebar's "N new").</summary>
    [ObservableProperty]
    public partial bool IsNew { get; protected set; }

    /// <summary>Fills the display fields from <paramref name="build"/>.</summary>
    protected void ApplyBuild(BuildInfo build, Channel channel)
    {
        Build = build;
        Channel = channel;
        Version = build.DisplayVersion;
        Label = BuildText.LabelOf(build, channel);
        Name = BuildText.NameOf(build, channel);
        Branch = BuildText.BranchOf(build, channel);
        Hash = build.BuildHash;
        BuiltAt = build.CommitTime.ToDateTimeOffset(TimeZoneInfo.Local);
    }
}
