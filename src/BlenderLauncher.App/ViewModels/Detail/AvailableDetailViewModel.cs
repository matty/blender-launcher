using System.ComponentModel;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.Core.Pipeline;
using CommunityToolkit.Mvvm.Input;

namespace BlenderLauncher.App.ViewModels.Detail;

/// <summary>
/// The detail panel's body for a download: the header, then "Download · 433 MB", the progress with cancel, or
/// "Installed — show in library"; and the Build section. The design hides Installation, Options and Remove here.
/// </summary>
public sealed partial class AvailableDetailViewModel : ViewModelBase, IDisposable
{
    public AvailableDetailViewModel(DetailPanelViewModel panel, AvailableBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(row);
        Panel = panel;
        Row = row;
        Row.PropertyChanged += OnRowChanged;
    }

    public DetailPanelViewModel Panel { get; }

    public AvailableBuildRowViewModel Row { get; }

    private BuildActions Actions => Panel.Shell.Actions;

    /// <summary>"Download · 433 MB", or "Download" when the size is unknown.</summary>
    public string DownloadText => Row.SizeBytes is null
        ? Loc.Tr("ui.detail.download")
        : Loc.Tr("ui.detail.download_size", ("size", Row.SizeText));

    /// <summary>"Downloading · 120 MB / 433 MB", "Extracting…" or "Finishing…".</summary>
    public string ProgressText
    {
        get
        {
            if (Row.JobState is InstallState.Extracting or InstallState.Finishing)
            {
                return Row.StageText;
            }

            var done = DisplayFormat.Size(Row.BytesDone);
            return Row.BytesTotal is { } total && !Row.IsIndeterminate
                ? Loc.Tr("ui.detail.downloading", ("done", done), ("total", DisplayFormat.Size(total)))
                : Loc.Tr("ui.detail.downloading_unknown", ("done", done));
        }
    }

    public bool HasHash => !string.IsNullOrEmpty(Row.Hash);

    public string CommitText => Row.Hash is { Length: > 12 } h ? h[..12] : Row.Hash ?? Loc.Tr("ui.units.unknown");

    [RelayCommand]
    private void Download() => Actions.Download(Row);

    [RelayCommand]
    private void Cancel() => Actions.CancelDownload(Row);

    [RelayCommand]
    private void ShowInLibrary()
    {
        if (Row.InstalledRow is { } installed)
        {
            Panel.Shell.ShowInstalledBuild(installed);
        }
    }

    [RelayCommand]
    private Task CopyHashAsync() => Actions.CopyHashAsync(Row);

    public void Dispose() => Row.PropertyChanged -= OnRowChanged;

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AvailableBuildRowViewModel.SizeBytes):
                OnPropertyChanged(nameof(DownloadText));
                break;
            case nameof(AvailableBuildRowViewModel.BytesDone) or nameof(AvailableBuildRowViewModel.BytesTotal)
                or nameof(AvailableBuildRowViewModel.IsIndeterminate) or nameof(AvailableBuildRowViewModel.JobState):
                OnPropertyChanged(nameof(ProgressText));
                break;
            case nameof(AvailableBuildRowViewModel.Hash):
                OnPropertyChanged(nameof(HasHash));
                OnPropertyChanged(nameof(CommitText));
                break;
        }
    }
}
