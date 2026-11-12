using Avalonia.Media;
using BlenderLauncher.App.Theme;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.ViewModels.Settings;

/// <summary>
/// One row of "Visibility and Downloading" (upstream's <c>RepoGroup</c>): the channel's name, its eye (shown in the
/// sidebar) and its download toggle (checked for new builds). Upstream's rules: turning the eye off also turns
/// downloading off and remembers it; turning downloading on turns the eye on; turning the eye back on restores
/// downloading as it was.
/// </summary>
public sealed class RepoRowViewModel : ViewModelBase
{
    private readonly SettingsStore _store;
    private readonly Func<BuildsSettings, (bool Show, bool Scrape)> _read;
    private readonly Func<BuildsSettings, bool, bool, BuildsSettings> _write;
    private bool _downloadBeforeHidden;

    public RepoRowViewModel(SettingsStore store, Channel channel, string name, string description,
        Func<BuildsSettings, (bool Show, bool Scrape)> read, Func<BuildsSettings, bool, bool, BuildsSettings> write)
    {
        _store = store;
        Name = name;
        Description = description;
        Colour = (IBrush)ChannelPalette.For(channel).Foreground;
        _read = read;
        _write = write;
        _downloadBeforeHidden = read(store.Current.Builds).Scrape;
    }

    public string Name { get; }

    /// <summary>The channel's description, as the name's tooltip.</summary>
    public string Description { get; }

    public IBrush Colour { get; }

    public bool IsShown
    {
        get => _read(_store.Current.Builds).Show;
        set
        {
            var (show, scrape) = _read(_store.Current.Builds);
            if (show == value)
            {
                return;
            }

            if (!value)
            {
                _downloadBeforeHidden = scrape;
                Write(false, false);
            }
            else
            {
                Write(true, _downloadBeforeHidden);
            }
        }
    }

    public bool IsDownloaded
    {
        get => _read(_store.Current.Builds).Scrape;
        set
        {
            var (show, scrape) = _read(_store.Current.Builds);
            if (scrape == value)
            {
                return;
            }

            _downloadBeforeHidden = value;
            Write(value || show, value);
        }
    }

    /// <summary>Re-reads the settings (after a change made elsewhere).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    private void Write(bool show, bool scrape)
    {
        _store.Update(s => s with { Builds = _write(s.Builds, show, scrape) });
        Refresh();
    }
}

/// <summary>One row of the advanced update button: whether the channel's builds show it, and how far it looks.</summary>
public sealed class UpdateRowViewModel : ViewModelBase
{
    private readonly SettingsStore _store;
    private readonly Func<BuildsSettings, (bool Show, UpdateBehaviour Behaviour)> _read;
    private readonly Func<BuildsSettings, bool, UpdateBehaviour, BuildsSettings> _write;

    public UpdateRowViewModel(SettingsStore store, string label, string tooltip, string behaviourTooltip,
        Func<BuildsSettings, (bool Show, UpdateBehaviour Behaviour)> read, Func<BuildsSettings, bool, UpdateBehaviour, BuildsSettings> write)
    {
        _store = store;
        Label = label;
        Tooltip = tooltip;
        BehaviourTooltip = behaviourTooltip;
        _read = read;
        _write = write;
    }

    public string Label { get; }

    public string Tooltip { get; }

    public string BehaviourTooltip { get; }

    /// <summary>Major, Minor, Patch.</summary>
    public IReadOnlyList<string> BehaviourNames { get; init; } = [];

    public bool IsShown
    {
        get => _read(_store.Current.Builds).Show;
        set => _store.Update(s => s with { Builds = _write(s.Builds, value, _read(s.Builds).Behaviour) });
    }

    /// <summary>The behaviour as an index into Major, Minor, Patch (upstream's order).</summary>
    public int BehaviourIndex
    {
        get => SettingsViewModel.BehaviourIndexOf(_read(_store.Current.Builds).Behaviour);
        set
        {
            if (SettingsViewModel.BehaviourAt(value) is { } behaviour)
            {
                _store.Update(s => s with { Builds = _write(s.Builds, _read(s.Builds).Show, behaviour) });
            }
        }
    }

    public void Refresh() => OnPropertyChanged(string.Empty);
}
