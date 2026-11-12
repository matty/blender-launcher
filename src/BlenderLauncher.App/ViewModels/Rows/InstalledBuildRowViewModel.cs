using BlenderLauncher.App.Localisation;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlenderLauncher.App.ViewModels.Rows;

/// <summary>
/// One installed build (or an unrecognised or damaged folder): a projection of <see cref="LibraryService"/>'s entry,
/// its <see cref="LibraryState"/> extras, <see cref="Core.Processes.LaunchService"/>'s running count and
/// <see cref="BuildUpdateService"/>'s update. Updated in place by <see cref="BuildCatalog"/>; read-only to views.
/// </summary>
public sealed partial class InstalledBuildRowViewModel : BuildRowViewModel
{
    public InstalledBuildRowViewModel(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = path;
    }

    /// <summary>The build folder, absolute. The row's identity.</summary>
    public string Path { get; }

    public override string Id => Path;

    public override bool IsInstalledRow => true;

    /// <summary>The folder name.</summary>
    public string FolderName => System.IO.Path.GetFileName(Path);

    /// <summary>Build, Unrecognised (offer "Initialise") or Damaged (show <see cref="ErrorText"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecognised), nameof(IsUnrecognised), nameof(IsDamaged), nameof(AccessibleName))]
    public partial LibraryEntryKind Kind { get; private set; }

    public bool IsRecognised => Kind == LibraryEntryKind.Build;

    /// <inheritdoc />
    public override string AccessibleName => IsRecognised ? base.AccessibleName : FolderName;

    public bool IsUnrecognised => Kind == LibraryEntryKind.Unrecognised;

    public bool IsDamaged => Kind == LibraryEntryKind.Damaged;

    /// <summary>Why a damaged build could not be read.</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>Running instances of this build (the green dot, "2 running", the detail panel's "Quit all").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(RunningText), nameof(InstancesRunningText))]
    public partial int RunningCount { get; set; }

    public bool IsRunning => RunningCount > 0;

    /// <summary>The row's running label: "Running", or "2 running".</summary>
    public string RunningText => RunningCount > 1 ? Loc.Tr("ui.list.running_count", ("count", RunningCount)) : Loc.Tr("ui.list.running");

    /// <summary>The detail panel's "1 instance running" / "3 instances running".</summary>
    public string InstancesRunningText => Loc.Plural("ui.detail.instances_running", RunningCount);

    /// <summary>Between "Launch" and the process being counted as running (the spinner, "Starting").</summary>
    [ObservableProperty]
    public partial bool IsStarting { get; set; }

    /// <summary>The library's default build (status bar "Default", the "Default" tag, "Use as default").</summary>
    [ObservableProperty]
    public partial bool IsDefault { get; set; }

    /// <summary>The <c>.blinfo</c> favourite flag (the star, the Favorites view).</summary>
    [ObservableProperty]
    public partial bool IsFavourite { get; set; }

    /// <summary>The build keeps its preferences in its own folder ("Portable config").</summary>
    [ObservableProperty]
    public partial bool IsPortable { get; set; }

    /// <summary>The sidecar's replace-on-new-build flag.</summary>
    [ObservableProperty]
    public partial bool ReplaceOnNewBuild { get; set; }

    /// <summary>The <c>.blinfo</c> freeze flag (no update offers).</summary>
    [ObservableProperty]
    public partial bool IsFrozen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledOnText))]
    public partial DateTimeOffset? InstalledOn { get; set; }

    /// <summary>"Oct 15, 2026".</summary>
    public string InstalledOnText => DisplayFormat.Date(InstalledOn);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastUsedText))]
    public partial DateTimeOffset? LastUsed { get; set; }

    /// <summary>"Never", "Just now", "Today, 09:41", "Yesterday, 18:12", or the date.</summary>
    public string LastUsedText => DisplayFormat.LastUsed(LastUsed);

    /// <summary>A newer build to update to, when the update rules offer one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    public partial BuildUpdate? Update { get; set; }

    public bool HasUpdate => Update is { ShowButton: true };

    /// <summary>Applies a library entry (its kind and <c>.blinfo</c> fields).</summary>
    public void ApplyEntry(LibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Kind = entry.Kind;
        ErrorText = entry.Error?.Message;
        if (entry.Build is { } build)
        {
            var channel = Channels.FromBranch(build.Branch, entry.ParentFolder);
            ApplyBuild(build, channel);
            IsFavourite = build.IsFavorite;
            IsFrozen = build.IsFrozen;
        }
        else
        {
            Build = null;
            Channel = Channel.Custom;
            Version = entry.Name;
            Label = "";
            Name = entry.Name;
            Branch = "";
            Hash = null;
            BuiltAt = null;
            IsFavourite = false;
            IsFrozen = false;
        }
    }

    /// <summary>Applies the sidecar extras.</summary>
    public void ApplyState(BuildState state, DateTimeOffset? installedOn)
    {
        ArgumentNullException.ThrowIfNull(state);
        SizeBytes = state.SizeBytes;
        PythonVersion = state.PythonVersion;
        ReplaceOnNewBuild = state.ReplaceOnNewBuild;
        LastUsed = state.LastUsed;
        InstalledOn = state.InstalledOn ?? installedOn;
    }

    /// <summary>For the design-time data only: sets the fields Core would supply.</summary>
    internal void ApplySample(BuildInfo build, Channel channel, long? sizeBytes, string? python, string? description)
    {
        Kind = LibraryEntryKind.Build;
        ApplyBuild(build, channel);
        IsFavourite = build.IsFavorite;
        SizeBytes = sizeBytes;
        PythonVersion = python;
        Description = description;
    }

    /// <summary>Re-announces the time-relative texts ("Today, 09:41") after midnight or a while idle.</summary>
    public void RefreshRelativeTimes() => OnPropertyChanged(nameof(LastUsedText));
}
