using System.Collections.Specialized;
using BlenderLauncher.App.ViewModels.Detail;
using BlenderLauncher.App.ViewModels.Rows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlenderLauncher.App.ViewModels;

/// <summary>
/// The detail panel: 300 wide, hidden when <see cref="MainWindowViewModel.IsDetailPanelVisible"/> is false.
/// View: <c>Views/Detail/DetailPanel.axaml</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Follows <see cref="MainWindowViewModel.SelectionChanged"/>: <see cref="Current"/> is an
/// <see cref="InstalledDetailViewModel"/> or an <see cref="AvailableDetailViewModel"/> (typed DataTemplates pick the
/// body), or null for the empty state. A removed installed build is deselected.</item>
/// <item>The Build, Installation, Options and More sections share their expanded state across selections and
/// remember it in <c>internal.collapsedDetailSections</c>: <c>build</c>, <c>install</c>, <c>options</c> when
/// collapsed; More starts collapsed and is stored as <c>more-open</c> while expanded.</item>
/// </list>
/// </remarks>
public sealed partial class DetailPanelViewModel : ViewModelBase
{
    public const string BuildSection = "build";
    public const string InstallSection = "install";
    public const string OptionsSection = "options";
    public const string MoreOpenToken = "more-open";

    private bool _loading;

    public DetailPanelViewModel(MainWindowViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        Shell = shell;

        var stored = shell.Settings?.Current.Internal.CollapsedDetailSections ?? [];
        _loading = true;
        IsBuildExpanded = !stored.Contains(BuildSection);
        IsInstallExpanded = !stored.Contains(InstallSection);
        IsOptionsExpanded = !stored.Contains(OptionsSection);
        IsMoreExpanded = stored.Contains(MoreOpenToken);
        _loading = false;

        shell.SelectionChanged += (_, row) => Show(row);
        shell.Catalog.Installed.CollectionChanged += OnInstalledChanged;
        Show(shell.SelectedBuild);
    }

    public MainWindowViewModel Shell { get; }

    /// <summary>The body for the selection, or null (the "Select a build" state).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ViewModelBase? Current { get; private set; }

    public bool HasSelection => Current is not null;

    [ObservableProperty]
    public partial bool IsBuildExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsInstallExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsOptionsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsMoreExpanded { get; set; }

    /// <summary>The value written to <c>internal.collapsedDetailSections</c> for the current state.</summary>
    public IReadOnlyList<string> CollapsedSections
    {
        get
        {
            var list = new List<string>();
            if (!IsBuildExpanded)
            {
                list.Add(BuildSection);
            }

            if (!IsInstallExpanded)
            {
                list.Add(InstallSection);
            }

            if (!IsOptionsExpanded)
            {
                list.Add(OptionsSection);
            }

            if (IsMoreExpanded)
            {
                list.Add(MoreOpenToken);
            }

            return list;
        }
    }

    partial void OnIsBuildExpandedChanged(bool value) => SaveSections();

    partial void OnIsInstallExpandedChanged(bool value) => SaveSections();

    partial void OnIsOptionsExpandedChanged(bool value) => SaveSections();

    partial void OnIsMoreExpandedChanged(bool value) => SaveSections();

    private void SaveSections()
    {
        if (_loading || Shell.Settings is not { } settings)
        {
            return;
        }

        var sections = CollapsedSections;
        settings.Update(s => s with { Internal = s.Internal with { CollapsedDetailSections = sections } });
    }

    private void Show(BuildRowViewModel? row)
    {
        var existing = Current switch
        {
            InstalledDetailViewModel i => (BuildRowViewModel)i.Row,
            AvailableDetailViewModel a => a.Row,
            _ => null,
        };
        if (ReferenceEquals(existing, row) && row is not null)
        {
            return;
        }

        (Current as IDisposable)?.Dispose();
        Current = row switch
        {
            InstalledBuildRowViewModel installed => new InstalledDetailViewModel(this, installed),
            AvailableBuildRowViewModel available => new AvailableDetailViewModel(this, available),
            _ => null,
        };
    }

    private void OnInstalledChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Shell.SelectedInstalled is { } selected && !Shell.Catalog.Installed.Contains(selected))
        {
            Shell.SelectedInstalled = null;
        }
    }
}
