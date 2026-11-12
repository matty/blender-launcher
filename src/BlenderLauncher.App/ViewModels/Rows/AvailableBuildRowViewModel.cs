using System.Globalization;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Pipeline;
using BlenderLauncher.Core.Scraping;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlenderLauncher.App.ViewModels.Rows;

/// <summary>
/// One downloadable build: a projection of <see cref="ScrapeService"/>'s <see cref="AvailableBuild"/> and, while it
/// installs, of its <see cref="InstallJob"/>. <see cref="InstalledRow"/> links to the installed copy when there is one
/// ("Installed" with Launch, "Installed — show in library"). Updated in place by <see cref="BuildCatalog"/>.
/// </summary>
/// <remarks>
/// Row states, as the design: <see cref="IsIdle"/> (Download button), <see cref="IsBusy"/> (progress bar and cancel),
/// <see cref="IsInstalled"/> ("Installed" and Launch). Exactly one is true.
/// </remarks>
public sealed partial class AvailableBuildRowViewModel : BuildRowViewModel
{
    public AvailableBuildRowViewModel(AvailableBuild source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Key = ScrapeService.DeduplicationKey(source.Build);
        Source = source;
        ApplySource(source);
    }

    /// <summary><see cref="ScrapeService.DeduplicationKey"/> of the build: the row's identity.</summary>
    public string Key { get; }

    public override string Id => Key;

    public override bool IsInstalledRow => false;

    /// <summary>The scraped build, as last seen.</summary>
    [ObservableProperty]
    public partial AvailableBuild Source { get; private set; }

    /// <summary>The install job for this build while one exists (queued, running or just finished).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsBusy), nameof(IsInstalled), nameof(CanCancel), nameof(StageText))]
    public partial InstallState? JobState { get; private set; }

    /// <summary>The installed copy of this build, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsBusy), nameof(IsInstalled))]
    public partial InstalledBuildRowViewModel? InstalledRow { get; set; }

    /// <summary>Not installed and no job running: the Download button.</summary>
    public bool IsIdle => !IsInstalled && !IsBusy;

    /// <summary>Queued, downloading, extracting or finishing: progress and cancel.</summary>
    public bool IsBusy => InstalledRow is null && JobState is InstallState.Idle or InstallState.Downloading or InstallState.Extracting or InstallState.Finishing;

    /// <summary>Installed: "Installed" with Launch.</summary>
    public bool IsInstalled => InstalledRow is not null;

    public bool CanCancel => _job?.CanCancel ?? false;

    /// <summary>The current step, for the detail panel: "Downloading", "Extracting…", "Finishing…".</summary>
    public string StageText => JobState switch
    {
        InstallState.Extracting => Localisation.Loc.Tr("ui.detail.extracting"),
        InstallState.Finishing => Localisation.Loc.Tr("ui.detail.finishing"),
        _ => Localisation.Loc.Tr("act.prog.downloading"),
    };

    /// <summary>0 to 1; meaningful when <see cref="IsIndeterminate"/> is false.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    public partial double Progress { get; private set; }

    /// <summary>True while the total size is unknown (no Content-Length).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    public partial bool IsIndeterminate { get; private set; }

    [ObservableProperty]
    public partial long BytesDone { get; private set; }

    [ObservableProperty]
    public partial long? BytesTotal { get; private set; }

    /// <summary>"42%" (blank while indeterminate).</summary>
    public string PercentText => IsIndeterminate ? "" : (Progress * 100).ToString("0", CultureInfo.CurrentCulture) + "%";

    /// <summary>The last job's error message when it failed.</summary>
    [ObservableProperty]
    public partial string? JobError { get; private set; }

    private InstallJob? _job;

    /// <summary>The live job, for <see cref="BuildActions"/> (cancel).</summary>
    public InstallJob? Job => _job;

    /// <summary>Applies a newer scrape of the same build.</summary>
    public void ApplySource(AvailableBuild source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        ApplyBuild(source.Build, Channels.FromBranch(source.Build.Branch));
        SizeBytes = source.SizeBytes;
        IsNew = source.IsNew;
        Description = source.Description;
    }

    /// <summary>Applies the state and progress of <paramref name="job"/> (or clears them when null).</summary>
    public void ApplyJob(InstallJob? job)
    {
        _job = job;
        if (job is null)
        {
            JobState = null;
            Progress = 0;
            IsIndeterminate = false;
            BytesDone = 0;
            BytesTotal = null;
            return;
        }

        ApplyProgress(job.Progress);
        JobError = job.State == InstallState.Failed ? job.Error?.Message : null;
        JobState = job.State;
        OnPropertyChanged(nameof(CanCancel));
    }

    /// <summary>Applies a progress report.</summary>
    public void ApplyProgress(TransferProgress progress)
    {
        BytesDone = progress.Done;
        BytesTotal = progress.Total;
        IsIndeterminate = progress.IsIndeterminate;
        Progress = progress.Fraction ?? 0;
    }

    /// <summary>For the design-time data only.</summary>
    internal void ApplySample(string? python, InstallState? state = null, double progress = 0)
    {
        PythonVersion = python;
        JobState = state;
        Progress = progress;
        if (BytesTotal is null && SizeBytes is { } size)
        {
            BytesTotal = size;
            BytesDone = (long)(size * progress);
        }
    }
}
