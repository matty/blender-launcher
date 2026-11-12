using System.Collections.ObjectModel;
using System.Globalization;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlenderLauncher.App.ViewModels;

/// <summary>What the picker was asked to do: <c>launch [-f FILE | -ol] [-v QUERY] [-- ARGS]</c>, or a double-clicked file.</summary>
public sealed record LaunchPickerRequest(
    string? File = null,
    string? Query = null,
    bool OpenLast = false,
    IReadOnlyList<string>? BlenderArgs = null);

/// <summary>The library as the picker needs it.</summary>
/// <param name="Builds">The library's entries; only those with a readable build are offered.</param>
/// <param name="DefaultBuildFolder">The default build's folder, or null.</param>
/// <param name="SavedQueries">Setting <c>versionSpecificQueries</c>.</param>
/// <param name="LaunchTimerSeconds">Setting <c>launchTimer</c>: -1 off, 0 at once, N seconds.</param>
public sealed record LaunchPickerLibrary(
    IReadOnlyList<LibraryEntry> Builds,
    string? DefaultBuildFolder,
    IReadOnlyDictionary<string, string> SavedQueries,
    int LaunchTimerSeconds);

/// <summary>Where the picker gets its builds and how it launches: the running app's services, or its own.</summary>
public interface ILaunchPickerHost
{
    /// <summary>Loads (or returns) the installed builds and the settings the picker uses.</summary>
    Task<LaunchPickerLibrary> LoadAsync(CancellationToken ct);

    /// <summary>Saves <paramref name="queryText"/> for files made in <paramref name="versionKey"/> (<c>4.2.0</c>).</summary>
    void SaveQuery(string versionKey, string queryText);

    /// <summary>Starts the build. Throws when it could not be started.</summary>
    void Launch(BuildInfo build, LaunchMode mode, IReadOnlyList<string> blenderArgs);
}

/// <summary>One installed build in the picker's list.</summary>
public sealed partial class LaunchPickerRow : ObservableObject
{
    public LaunchPickerRow(LibraryEntry entry, VersionSearchCandidate candidate, bool isDefault)
    {
        Entry = entry;
        Candidate = candidate;
        var build = entry.Build!;
        Name = build.DisplayLabel;
        Version = build.Subversion;
        Branch = build.Branch;
        Date = build.CommitTime.ToDateTimeOffset(TimeZoneInfo.Local).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Folder = entry.Name;
        IsDefault = isDefault;
    }

    public LibraryEntry Entry { get; }

    public BuildInfo Build => Entry.Build!;

    public VersionSearchCandidate Candidate { get; }

    public string Name { get; }

    public string Version { get; }

    public string Branch { get; }

    public string Date { get; }

    public string Folder { get; }

    public bool IsDefault { get; }

    /// <summary>Matches the current search: drawn bold and sorted first; the others are dimmed.</summary>
    [ObservableProperty]
    public partial bool IsMatch { get; set; }
}

/// <summary>
/// The launch picker (upstream's <c>LaunchingWindow</c>): opened for a
/// double-clicked <c>.blend</c> and for <c>launch</c> without <c>-c</c>. It reads the builds, chooses the starting
/// search with <see cref="BlendLaunchResolver"/> (explicit query, else the query saved for the file's version, else
/// <c>major.minor.^</c>, else the default build), and lets the user change the version, branch, hash and date fields;
/// every edit re-runs the search. When exactly one build matches, the launch timer counts down (0 launches at once,
/// -1 never); any edit or selection stops it.
/// </summary>
/// <remarks>
/// Deviation from upstream: the Blender arguments given after <c>--</c> are passed on here too; upstream honoured
/// them only with <c>-c</c>.
/// </remarks>
public sealed partial class LaunchPickerViewModel : ViewModelBase, IDisposable
{
    private readonly ILaunchPickerHost _host;
    private readonly LaunchPickerRequest _request;
    private readonly List<LaunchPickerRow> _all = [];
    private CancellationTokenSource? _countdown;
    private bool _populating;
    private bool _finished;
    private bool _loadStarted;
    private BlendFileHeader? _header;
    private string? _queryText;

    public LaunchPickerViewModel(ILaunchPickerHost host, LaunchPickerRequest request)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(request);
        _host = host;
        _request = request;
        FilePath = request.File is { Length: > 0 } f ? Path.GetFullPath(f) : null;
        Status = Loc.Tr("launching.status.reading");
        DateChoices = [Loc.Tr("launching.chrono.latest"), Loc.Tr("launching.chrono.any"), Loc.Tr("launching.chrono.oldest")];
    }

    /// <summary>The picker finished: true after a launch, false when cancelled or closed.</summary>
    public event EventHandler<bool>? Finished;

    /// <summary>The .blend file, or null.</summary>
    public string? FilePath { get; }

    public bool HasFile => FilePath is not null;

    public string FileName => FilePath is null ? "" : Path.GetFileName(FilePath);

    /// <summary>The heading: the file name, or what launching without a file does.</summary>
    public string Heading => FilePath is not null ? FileName
        : _request.OpenLast ? Loc.Tr("ui.launch_picker.open_last") : Loc.Tr("ui.launch_picker.no_file");

    public string ArgsText => _request.BlenderArgs is { Count: > 0 } a
        ? Loc.Tr("ui.launch_picker.args", ("args", string.Join(' ', a.Select(x => x.Contains(' ', StringComparison.Ordinal) ? $"\"{x}\"" : x))))
        : "";

    public bool HasArgs => _request.BlenderArgs is { Count: > 0 };

    public IReadOnlyList<string> DateChoices { get; }

    public ObservableCollection<LaunchPickerRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string Status { get; set; }

    [ObservableProperty]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty]
    public partial string VersionText { get; set; } = "";

    [ObservableProperty]
    public partial string BranchText { get; set; } = "";

    [ObservableProperty]
    public partial string HashText { get; set; } = "";

    /// <summary>0 latest (<c>@^</c>), 1 any (<c>@*</c>), 2 oldest (<c>@-</c>).</summary>
    [ObservableProperty]
    public partial int DateIndex { get; set; } = 1;

    [ObservableProperty]
    public partial bool VersionInvalid { get; set; }

    [ObservableProperty]
    public partial bool BranchInvalid { get; set; }

    [ObservableProperty]
    public partial bool HashInvalid { get; set; }

    /// <summary>Why the search is invalid, or why nothing can be launched; empty when fine.</summary>
    [ObservableProperty]
    public partial string Message { get; set; } = "";

    [ObservableProperty]
    public partial bool MessageIsError { get; set; }

    [ObservableProperty]
    public partial LaunchPickerRow? SelectedRow { get; set; }

    [ObservableProperty]
    public partial int MatchCount { get; set; }

    /// <summary>Seconds left on the launch timer, or null when it is not running.</summary>
    [ObservableProperty]
    public partial int? SecondsLeft { get; set; }

    public bool IsCountingDown => SecondsLeft is not null;

    public string CountdownText => SecondsLeft is { } s ? Loc.Tr("launching.timer", ("time", s)) : "";

    /// <summary>The build Launch starts: the selected row when it matches, else the only match.</summary>
    public LaunchPickerRow? Target =>
        SelectedRow is { IsMatch: true } selected ? selected
        : MatchCount == 1 ? _all.First(r => r.IsMatch)
        : null;

    public bool CanLaunch => Target is not null && !_finished;

    /// <summary>"Save current search for .blend files made in X.Y": offered with a readable file header.</summary>
    public bool CanSaveQuery => _header is not null && _queryText is not null;

    public bool ShowSaveQuery => _header is not null;

    public string SaveQueryText => _header is { } h
        ? Loc.Tr("launching.save_current", ("short_v", $"{h.Major}.{h.Minor}"))
        : "";

    /// <summary>The current search as query text, or null while a field is invalid.</summary>
    public string? QueryText => _queryText;

    /// <summary>Reads the builds and the file header, chooses the first search and starts the timer.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_loadStarted)
        {
            return;
        }

        _loadStarted = true;
        LaunchPickerLibrary library;
        try
        {
            library = await _host.LoadAsync(ct).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Status = Loc.Tr("ui.launch_picker.load_failed", ("error", e.Message));
            return;
        }

        var defaultFolder = library.DefaultBuildFolder;
        foreach (var entry in library.Builds)
        {
            if (entry.Build is { } build && VersionSearchCandidate.TryFromBuildInfo(build, out var candidate, entry.Path))
            {
                _all.Add(new LaunchPickerRow(entry, candidate, defaultFolder is not null && LibraryLayout.PathComparer.Equals(entry.Path, defaultFolder)));
            }
        }

        Status = Loc.Tr("launching.status.found", ("count", _all.Count));
        if (FilePath is not null)
        {
            try
            {
                _header = await BlendFileHeaderReader.ReadAsync(FilePath, ct).ConfigureAwait(true);
                Status = Loc.Tr("launching.status.header", ("version", $"{_header.Major}.{_header.Minor}"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotABlendFileException or NotSupportedException)
            {
                Status = Loc.Tr("ui.launch_picker.header_unreadable", ("count", _all.Count));
            }
        }

        VersionSearchQuery? explicitQuery = null;
        if (_request.Query is { } text && !VersionSearchQuery.TryParse(text, out explicitQuery))
        {
            // Shown in the version field, where the user can correct it.
            explicitQuery = null;
        }

        var resolution = BlendLaunchResolver.Resolve(
            _all, r => r.Candidate, _header, BlendLaunchResolver.ParseSavedQueries(library.SavedQueries), explicitQuery, defaultFolder);

        _populating = true;
        try
        {
            if (_request.Query is { } raw && explicitQuery is null)
            {
                VersionText = raw;
                BranchText = "";
                HashText = "";
                DateIndex = 1;
            }
            else if (resolution.Source == BlendLaunchSource.DefaultBuild && resolution.Matches.Count > 0)
            {
                // The default build is chosen by folder, which no field can show: describe that build instead.
                var row = resolution.Matches[0];
                var v = row.Candidate.Version;
                VersionText = $"{v.Major}.{v.Minor}.{v.Patch}";
                BranchText = row.Build.Branch;
                HashText = row.Build.BuildHash ?? "";
                DateIndex = 1;
                SelectedRow = row;
            }
            else
            {
                Fill(resolution.Query);
            }
        }
        finally
        {
            _populating = false;
        }

        Recompute();
        if (SelectedRow is null && MatchCount > 0)
        {
            Select(Rows[0]);
        }

        IsLoaded = true;
        OnPropertyChanged(nameof(ShowSaveQuery));
        OnPropertyChanged(nameof(SaveQueryText));
        OnPropertyChanged(nameof(CanSaveQuery));

        if (_all.Count == 0)
        {
            SetMessage(Loc.Tr("ui.launch_picker.no_builds"), error: true);
        }

        if (MatchCount == 1 && library.LaunchTimerSeconds >= 0)
        {
            if (library.LaunchTimerSeconds == 0)
            {
                Launch();
            }
            else
            {
                StartCountdown(library.LaunchTimerSeconds);
            }
        }
    }

    /// <summary>Launches <see cref="Target"/>; on success the picker is finished.</summary>
    public bool Launch()
    {
        StopCountdown();
        if (Target is not { } target || _finished)
        {
            return false;
        }

        var mode = FilePath is not null ? LaunchMode.WithBlendFile(FilePath)
            : _request.OpenLast ? LaunchMode.OpenLast
            : LaunchMode.Plain;
        try
        {
            _host.Launch(target.Build, mode, _request.BlenderArgs ?? []);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetMessage(Loc.Tr("ui.toast.launch_failed", ("name", target.Name), ("error", e.Message)), error: true);
            return false;
        }

        Finish(true);
        return true;
    }

    public void Cancel() => Finish(false);

    /// <summary>Saves the current search for files made in the header's version.</summary>
    public void SaveQuery()
    {
        StopCountdown();
        if (_header is not { } h || _queryText is not { } text)
        {
            return;
        }

        var key = BlendLaunchResolver.SavedQueryKey(h.Major, h.Minor);
        _host.SaveQuery(key, text);
        SetMessage(Loc.Tr("ui.launch_picker.saved", ("short_v", $"{h.Major}.{h.Minor}"), ("query", text)), error: false);
    }

    /// <summary>Upstream's double-click: copies the build into the fields, so the search finds it.</summary>
    public void UseRow(LaunchPickerRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        StopCountdown();
        _populating = true;
        try
        {
            var v = row.Candidate.Version;
            VersionText = $"{v.Major}.{v.Minor}.{v.Patch}";
            BranchText = row.Build.Branch;
            HashText = row.Build.BuildHash ?? "";
            DateIndex = 1;
        }
        finally
        {
            _populating = false;
        }

        Recompute();
        Select(row);
    }

    /// <summary>Stops the launch timer (any edit, selection or key press).</summary>
    public void StopCountdown()
    {
        if (_countdown is null)
        {
            return;
        }

        _countdown.Cancel();
        _countdown.Dispose();
        _countdown = null;
        SecondsLeft = null;
    }

    public void Dispose() => StopCountdown();

    partial void OnVersionTextChanged(string value) => OnFieldEdited();

    partial void OnBranchTextChanged(string value) => OnFieldEdited();

    partial void OnHashTextChanged(string value) => OnFieldEdited();

    partial void OnDateIndexChanged(int value) => OnFieldEdited();

    partial void OnSelectedRowChanged(LaunchPickerRow? value)
    {
        if (!_populating)
        {
            StopCountdown();
        }

        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(CanLaunch));
    }

    partial void OnSecondsLeftChanged(int? value)
    {
        OnPropertyChanged(nameof(IsCountingDown));
        OnPropertyChanged(nameof(CountdownText));
    }

    private void OnFieldEdited()
    {
        if (_populating)
        {
            return;
        }

        StopCountdown();
        Recompute();
    }

    private void Fill(VersionSearchQuery query)
    {
        VersionText = $"{query.Major}.{query.Minor}.{query.Patch}";
        BranchText = query.Branch is { Count: > 0 } b ? string.Join(',', b) : "";
        HashText = query.BuildHash ?? "";
        DateIndex = query.CommitTime?.Kind switch
        {
            VersionSearchPositionKind.Newest => 0,
            VersionSearchPositionKind.Oldest => 2,
            _ => 1,
        };
    }

    /// <summary>Validates each field, builds the query, matches it and reorders the list (matches first).</summary>
    private void Recompute()
    {
        var version = VersionText.Trim();
        if (version.Length == 0)
        {
            version = "*.*.*";
        }

        var branch = string.Join(',', BranchText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var hash = HashText.Trim();
        VersionInvalid = !VersionSearchQuery.TryParse(version, out _);
        BranchInvalid = branch.Length > 0 && !VersionSearchQuery.TryParse("*.*.*-" + branch, out _);
        HashInvalid = hash.Length > 0 && !VersionSearchQuery.TryParse("*.*.*+" + hash, out _);

        var text = version
            + (branch.Length > 0 ? "-" + branch : "")
            + (hash.Length > 0 ? "+" + hash : "")
            + DateIndex switch { 0 => "@^", 2 => "@-", _ => "" };
        VersionSearchQuery? query = null;
        var valid = !VersionInvalid && !BranchInvalid && !HashInvalid && VersionSearchQuery.TryParse(text, out query);
        _queryText = valid ? text : null;

        var matches = query is not null ? query.Match(_all, r => r.Candidate).ToHashSet() : [];
        foreach (var row in _all)
        {
            row.IsMatch = matches.Contains(row);
        }

        MatchCount = matches.Count;
        if (_queryText is null)
        {
            SetMessage(Loc.Tr("launching.errors.invalid_query", ("query", text)), error: true);
        }
        else if (_all.Count > 0 && MatchCount == 0)
        {
            SetMessage(Loc.Tr("ui.launch_picker.no_match"), error: true);
        }
        else
        {
            SetMessage("", error: false);
        }

        var selected = SelectedRow;
        var ordered = _all
            .OrderByDescending(r => r.IsMatch)
            .ThenByDescending(r => r.Candidate)
            .ToList();
        _populating = true;
        try
        {
            if (!Rows.SequenceEqual(ordered))
            {
                Rows.Clear();
                foreach (var row in ordered)
                {
                    Rows.Add(row);
                }
            }

            SelectedRow = selected;
        }
        finally
        {
            _populating = false;
        }

        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanSaveQuery));
    }

    private void Select(LaunchPickerRow row)
    {
        _populating = true;
        try
        {
            SelectedRow = row;
        }
        finally
        {
            _populating = false;
        }
    }

    private void SetMessage(string text, bool error)
    {
        Message = text;
        MessageIsError = error;
    }

    private void StartCountdown(int seconds)
    {
        StopCountdown();
        var cts = new CancellationTokenSource();
        _countdown = cts;
        SecondsLeft = seconds;
        _ = RunCountdownAsync(cts.Token);
    }

    private async Task RunCountdownAsync(CancellationToken ct)
    {
        try
        {
            while (SecondsLeft is > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(true);
                SecondsLeft--;
            }

            if (!ct.IsCancellationRequested)
            {
                _countdown?.Dispose();
                _countdown = null;
                SecondsLeft = null;
                Launch();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Finish(bool launched)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        StopCountdown();
        OnPropertyChanged(nameof(CanLaunch));
        Finished?.Invoke(this, launched);
    }
}
