using System.ComponentModel;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.ViewModels.Rows;
using BlenderLauncher.Core.Builds;
using CommunityToolkit.Mvvm.Input;

namespace BlenderLauncher.App.ViewModels.Detail;

/// <summary>
/// The detail panel's body for an installed build: the header and Launch / Starting / running strip, the
/// Build, Installation and Options sections, the More section and "Remove build". Reads the shared row and calls
/// <see cref="BuildActions"/>; lives while the row is selected (<see cref="Dispose"/> unhooks it).
/// </summary>
public sealed partial class InstalledDetailViewModel : ViewModelBase, IDisposable
{
    // Row properties that change what the More section offers.
    private static readonly HashSet<string> MoreInputs =
    [
        nameof(InstalledBuildRowViewModel.Update), nameof(InstalledBuildRowViewModel.IsFrozen),
        nameof(InstalledBuildRowViewModel.IsPortable), nameof(InstalledBuildRowViewModel.IsRunning),
        nameof(InstalledBuildRowViewModel.Hash), nameof(InstalledBuildRowViewModel.Kind),
        nameof(InstalledBuildRowViewModel.Build),
    ];

    public InstalledDetailViewModel(DetailPanelViewModel panel, InstalledBuildRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(row);
        Panel = panel;
        Row = row;
        More = BuildMore();
        Row.PropertyChanged += OnRowChanged;
    }

    public DetailPanelViewModel Panel { get; }

    public InstalledBuildRowViewModel Row { get; }

    private BuildActions Actions => Panel.Shell.Actions;

    /// <summary>"Launch 5.2.2", "Launch new instance" while one runs, "Starting…" while starting.</summary>
    public string LaunchText =>
        Row.IsStarting ? Loc.Tr("ui.detail.starting")
        : Row.IsRunning ? Loc.Tr("ui.detail.launch_new_instance")
        : Loc.Tr("ui.detail.launch_version", ("version", Row.Version));

    /// <summary>Launch is offered: a readable build and a way to start it.</summary>
    public bool CanLaunch => Row.IsRecognised && Actions.CanLaunch;

    public bool IsNotStarting => !Row.IsStarting;

    /// <summary>The Build, Installation and Options sections apply (a readable build).</summary>
    public bool IsRecognised => Row.IsRecognised;

    /// <summary>The note shown instead of the sections for an unrecognised or damaged folder.</summary>
    public string? ProblemText =>
        Row.IsUnrecognised ? Loc.Tr("ui.list.unrecognised_hint")
        : Row.IsDamaged ? Loc.Tr("msg.err.damaged", ("build", Row.FolderName)) + (Row.ErrorText is { } e ? "\n" + e : "")
        : null;

    public bool HasProblem => ProblemText is not null;

    public bool HasHash => !string.IsNullOrEmpty(Row.Hash);

    /// <summary>The commit as the design shows it: up to twelve characters (the full hash is in the tooltip and copied).</summary>
    public string CommitText => Row.Hash is { Length: > 12 } h ? h[..12] : Row.Hash ?? Loc.Tr("ui.units.unknown");

    // ------------------------------------------------------------------ options (two-way with the check boxes)

    /// <summary>"Use as default".</summary>
    public bool IsDefault
    {
        get => Row.IsDefault;
        set
        {
            if (value == Row.IsDefault)
            {
                return;
            }

            if (value)
            {
                Actions.SetDefault(Row);
            }
            else
            {
                Actions.ClearDefault(Row);
            }

            OnPropertyChanged();
        }
    }

    /// <summary>"Favorite".</summary>
    public bool IsFavourite
    {
        get => Row.IsFavourite;
        set
        {
            if (value != Row.IsFavourite)
            {
                _ = Actions.SetFavouriteAsync(Row, value);
            }
        }
    }

    /// <summary>"Portable config".</summary>
    public bool IsPortable
    {
        get => Row.IsPortable;
        set
        {
            if (value != Row.IsPortable)
            {
                _ = Actions.SetPortableAsync(Row, value);
            }
        }
    }

    /// <summary>"Replace on new build" (Daily and Experimental only: <see cref="BuildRowViewModel.CanReplaceOnNewBuild"/>).</summary>
    public bool ReplaceOnNewBuild
    {
        get => Row.ReplaceOnNewBuild;
        set
        {
            if (value != Row.ReplaceOnNewBuild)
            {
                Actions.SetReplaceOnNewBuild(Row, value);
            }
        }
    }

    // ------------------------------------------------------------------ More

    /// <summary>The More section's entries (upstream actions the design has no place for).</summary>
    public IReadOnlyList<BuildMenuEntry> More { get; private set; }

    public bool HasMore => More.Count > 0;

    /// <summary>The "Update to 5.3.0" note above the More list when an update is offered.</summary>
    public bool HasUpdate => Row.Update is not null && Row.IsRecognised;

    /// <summary>"Update to 5.3.0".</summary>
    public string UpdateText => Row.Update is { } u ? Loc.Tr("ui.menu.update_to", ("version", u.Candidate.Build.DisplayVersion)) : "";

    // ------------------------------------------------------------------ commands

    /// <summary>Launch; Shift opens the previous file (<c>--open-last</c>), as upstream's Shift-launch.</summary>
    [RelayCommand]
    private void Launch() => Actions.Launch(Row, ModifierKeys.IsShiftDown ? LaunchMode.OpenLast : null);

    [RelayCommand]
    private void QuitAll() => Actions.QuitAll(Row);

    [RelayCommand]
    private void OpenFolder() => Actions.OpenFolder(Row.Path);

    [RelayCommand]
    private Task CopyHashAsync() => Actions.CopyHashAsync(Row);

    [RelayCommand]
    private Task UpdateAsync() => Actions.UpdateAsync(Row);

    /// <summary>"Remove build": the default delete action, Shift inverting it.</summary>
    [RelayCommand]
    private Task RemoveAsync() => Actions.RemoveAsync(Row, ModifierKeys.IsShiftOrControlDown);

    public void Dispose() => Row.PropertyChanged -= OnRowChanged;

    private IReadOnlyList<BuildMenuEntry> BuildMore() =>
        new BuildContextMenuViewModel(Panel.Shell, [Row], BuildMenuScope.MoreSection).FlatEntries;

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(InstalledBuildRowViewModel.IsStarting):
                OnPropertyChanged(nameof(LaunchText));
                OnPropertyChanged(nameof(IsNotStarting));
                break;
            case nameof(InstalledBuildRowViewModel.RunningCount) or nameof(InstalledBuildRowViewModel.Version):
                OnPropertyChanged(nameof(LaunchText));
                break;
            case nameof(InstalledBuildRowViewModel.IsDefault):
                OnPropertyChanged(nameof(IsDefault));
                break;
            case nameof(InstalledBuildRowViewModel.IsFavourite):
                OnPropertyChanged(nameof(IsFavourite));
                break;
            case nameof(InstalledBuildRowViewModel.ReplaceOnNewBuild):
                OnPropertyChanged(nameof(ReplaceOnNewBuild));
                break;
            case nameof(InstalledBuildRowViewModel.Hash):
                OnPropertyChanged(nameof(HasHash));
                OnPropertyChanged(nameof(CommitText));
                break;
            case nameof(InstalledBuildRowViewModel.Kind) or nameof(InstalledBuildRowViewModel.ErrorText):
                OnPropertyChanged(nameof(IsRecognised));
                OnPropertyChanged(nameof(CanLaunch));
                OnPropertyChanged(nameof(ProblemText));
                OnPropertyChanged(nameof(HasProblem));
                break;
        }

        if (e.PropertyName == nameof(InstalledBuildRowViewModel.IsPortable))
        {
            OnPropertyChanged(nameof(IsPortable));
        }

        if (e.PropertyName == nameof(InstalledBuildRowViewModel.Update))
        {
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(UpdateText));
        }

        if (e.PropertyName is { } name && MoreInputs.Contains(name))
        {
            More = BuildMore();
            OnPropertyChanged(nameof(More));
            OnPropertyChanged(nameof(HasMore));
        }
    }
}
