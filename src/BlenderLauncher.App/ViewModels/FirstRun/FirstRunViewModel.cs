using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.ViewModels.Settings;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Settings;
using BlenderLauncher.Core.Settings.Upstream;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlenderLauncher.App.ViewModels.FirstRun;

/// <summary>What the wizard asks of its window.</summary>
public interface IFirstRunHost
{
    /// <summary>Asks for a folder; null when cancelled.</summary>
    Task<string?> PickFolderAsync(string title, string? startFolder);
}

/// <summary>What the wizard reads and writes besides the settings.</summary>
public sealed record FirstRunEnvironment
{
    /// <summary>The library folder suggested to a new user.</summary>
    public required string SuggestedLibraryFolder { get; init; }

    /// <summary>Upstream's INI when one was found (offered for import), else null.</summary>
    public string? UpstreamIniPath { get; init; }

    /// <summary>Where imported secrets go when the wizard finishes; null leaves them out.</summary>
    public ICredentialStore? Credentials { get; init; }

    /// <summary>Upstream's keyring entries, read for the import.</summary>
    public ILegacyCredentialReader? LegacyCredentials { get; init; }

    /// <summary>For legacy integer minimum versions in the INI; the bundled data when null.</summary>
    public ApiData? ApiData { get; init; }

    public Log? Log { get; init; }

    /// <summary>The library folder is created with its channel folders on Finish (false in tests that only look).</summary>
    public bool CreateLibraryFolders { get; init; } = true;
}

/// <summary>The wizard's pages, in order.</summary>
public enum FirstRunPage
{
    Welcome,
    Library,
    Channels,
    Shortcuts,
    Appearance,
    Background,
}

/// <summary>One entry of the step list on the left.</summary>
public sealed partial class FirstRunStep(int number, string title) : ObservableObject
{
    public int Number { get; } = number;

    public string Title { get; } = title;

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    public partial bool IsDone { get; set; }
}

/// <summary>
/// The first-run wizard: welcome, library folder (with an offer to import upstream's settings), channels,
/// shortcuts and file association, appearance, background running. Every choice is made on a draft copy of the
/// settings (<see cref="Draft"/>, edited through a <see cref="SettingsViewModel"/> so the controls behave as in
/// Settings); Finish commits it on a worker: creates the library folder, copies imported secrets, saves the settings
/// with <c>firstRunSeen</c> set. A failure shows the error page ("Continue anyway" / "Cancel"). Cancel, on any page,
/// means "quit", as upstream.
/// </summary>
/// <remarks>
/// New users get the launcher's own channel defaults (<see cref="FirstRunDefaults"/>); an import starts from the stored
/// settings instead, so upstream's own defaults fill the keys its INI does not contain. Shortcuts and the file
/// association are only recorded (<c>internal.pending*</c>): the shell integration creates them.
/// </remarks>
public sealed partial class FirstRunViewModel : ViewModelBase, IDisposable
{
    private static readonly string[] SecretKeys = ["github_token", "proxy/user", "proxy/password"];

    private readonly SettingsStore _target;
    private readonly FirstRunEnvironment _environment;
    private readonly AppSettings _fresh;
    private readonly QSettingsIni? _upstreamIni;
    private string? _importedLibrary;
    private int _checkVersion;
    private bool _libraryReady;
    private bool _completed;

    public FirstRunViewModel(SettingsStore target, FirstRunEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(environment);
        _target = target;
        _environment = environment;
        _fresh = FirstRunDefaults.Apply(target.Current);
        Draft = SettingsStore.InMemory(_fresh);
        Settings = new SettingsViewModel(Draft, new InMemoryCredentialStore(), new SettingsEnvironment { ApiData = environment.ApiData });

        Steps =
        [
            new FirstRunStep(1, Loc.Tr("ui.first_run.step.welcome")),
            new FirstRunStep(2, Loc.Tr("ui.first_run.step.library")),
            new FirstRunStep(3, Loc.Tr("ui.first_run.step.channels")),
            new FirstRunStep(4, Loc.Tr("ui.first_run.step.shortcuts")),
            new FirstRunStep(5, Loc.Tr("ui.first_run.step.appearance")),
            new FirstRunStep(6, Loc.Tr("ui.first_run.step.background")),
        ];

        if (environment.UpstreamIniPath is { } ini)
        {
            UpstreamPath = ini;
            try
            {
                _upstreamIni = QSettingsIni.Parse(File.ReadAllText(ini));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                UpstreamError = Loc.Tr("ui.first_run.library.import_unreadable", ("error", e.Message));
            }
        }

        var current = target.Current.General.LibraryFolder;
        LibraryPath = current is not null && Path.IsPathFullyQualified(current) ? current : environment.SuggestedLibraryFolder;
        if (_upstreamIni is not null)
        {
            // Offered ticked: someone with upstream installed most likely wants their settings.
            ImportUpstream = true;
        }

        UpdateSteps();
    }

    /// <summary>The wizard's copy of the settings; committed to the real store on Finish.</summary>
    public SettingsStore Draft { get; }

    /// <summary>The Settings window's view model over <see cref="Draft"/>: channel rows, appearance and tray options.</summary>
    public SettingsViewModel Settings { get; }

    public IReadOnlyList<FirstRunStep> Steps { get; }

    public IFirstRunHost? Host { get; set; }

    /// <summary>Raised once, on the UI thread: true when setup finished (or the user continued after an error), false to quit.</summary>
    public event EventHandler<bool>? Completed;

    /// <summary>"Welcome to Blender Launcher 0.1.0".</summary>
    public string WelcomeTitle => Loc.Tr("ui.first_run.welcome.title", ("v", AppIdentity.Version));

    // ------------------------------------------------------------------ pages and navigation

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Page), nameof(IsWelcome), nameof(IsLibrary), nameof(IsChannels), nameof(IsShortcuts),
        nameof(IsAppearance), nameof(IsBackground), nameof(CanGoBack), nameof(IsLastPage), nameof(IsNotLastPage), nameof(StepText))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand))]
    public partial int PageIndex { get; private set; }

    public FirstRunPage Page => (FirstRunPage)PageIndex;

    public bool IsWelcome => Page == FirstRunPage.Welcome;

    public bool IsLibrary => Page == FirstRunPage.Library;

    public bool IsChannels => Page == FirstRunPage.Channels;

    public bool IsShortcuts => Page == FirstRunPage.Shortcuts;

    public bool IsAppearance => Page == FirstRunPage.Appearance;

    public bool IsBackground => Page == FirstRunPage.Background;

    public bool CanGoBack => PageIndex > 0;

    public bool IsLastPage => Page == FirstRunPage.Background;

    public bool IsNotLastPage => !IsLastPage;

    /// <summary>"Step 2 of 6".</summary>
    public string StepText => Loc.Tr("ui.first_run.step_count", ("n", PageIndex + 1), ("total", Steps.Count));

    /// <summary>Whether Next (or Finish) may be pressed: the library page needs a usable folder.</summary>
    public bool CanGoNext => Page != FirstRunPage.Library || (LibraryCheck is { IsValid: true } && !IsCheckingLibrary);

    /// <summary>The page list is shown (not the committing or error page).</summary>
    public bool IsOnPages => !IsCommitting && !IsError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnPages))]
    public partial bool IsCommitting { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnPages))]
    public partial bool IsError { get; private set; }

    /// <summary>The error page's text: what failed and the exception.</summary>
    [ObservableProperty]
    public partial string ErrorText { get; private set; } = "";

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        if (!CanGoNext)
        {
            return;
        }

        if (IsLastPage)
        {
            await FinishAsync().ConfigureAwait(true);
            return;
        }

        PageIndex++;
        UpdateSteps();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (PageIndex > 0)
        {
            PageIndex--;
            UpdateSteps();
        }
    }

    /// <summary>Cancel (or closing the window): quits, as upstream's wizard does.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (!IsCommitting)
        {
            Complete(false);
        }
    }

    /// <summary>Error page: carry on with what was saved.</summary>
    [RelayCommand]
    private async Task ContinueAnywayAsync()
    {
        try
        {
            _target.Update(_ => Final(_libraryReady));
            await _target.FlushAsync().ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _environment.Log?.Error("Saving the first-run settings failed again", e);
        }

        Complete(true);
    }

    private void UpdateSteps()
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].IsCurrent = i == PageIndex;
            Steps[i].IsDone = i < PageIndex;
        }
    }

    // ------------------------------------------------------------------ library

    [ObservableProperty]
    public partial string LibraryPath { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext), nameof(LibraryProblemText), nameof(HasLibraryProblem), nameof(LibraryNoteText),
        nameof(HasLibraryNote), nameof(LibraryWarningText), nameof(HasLibraryWarning))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial LibraryCheckResult? LibraryCheck { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext), nameof(LibraryNoteText), nameof(HasLibraryNote))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial bool IsCheckingLibrary { get; private set; }

    public string? LibraryProblemText => LibraryCheck?.Problem switch
    {
        null => null,
        LibraryCheckProblem.Empty => Loc.Tr("ui.settings.library.problem.empty"),
        LibraryCheckProblem.IsAFile => Loc.Tr("ui.settings.library.problem.is_a_file"),
        LibraryCheckProblem.NotWritable => Loc.Tr("ui.settings.library.problem.not_writable"),
        LibraryCheckProblem.InsideAppFolder => Loc.Tr("ui.settings.library.problem.inside_app_folder"),
        _ => Loc.Tr("ui.settings.library.problem.invalid_path"),
    };

    public bool HasLibraryProblem => LibraryProblemText is not null;

    /// <summary>"Checking…", "The folder will be created." or "Existing builds… are picked up."</summary>
    public string? LibraryNoteText => IsCheckingLibrary ? Loc.Tr("ui.first_run.validating")
        : LibraryCheck is not { IsValid: true } check ? null
        : Loc.Tr(check.Exists ? "ui.first_run.library.exists" : "ui.first_run.library.will_create");

    public bool HasLibraryNote => LibraryNoteText is not null;

    public string? LibraryWarningText => LibraryCheck is not { IsValid: true, Warnings.Count: > 0 } check ? null
        : string.Join(" ", check.Warnings.Select(w => w == LibraryFolderWarning.ProtectedLocation
            ? Loc.Tr("ui.settings.library.warning.protected_location")
            : Loc.Tr("ui.settings.library.warning.blender_version_manager_root")));

    public bool HasLibraryWarning => LibraryWarningText is not null;

    [RelayCommand]
    private async Task BrowseLibraryAsync()
    {
        if (Host is not { } host)
        {
            return;
        }

        var start = LibraryCheck?.FullPath is { } full && Directory.Exists(full) ? full : null;
        var picked = await host.PickFolderAsync(Loc.Tr("msg.popup.select_library"), start).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            LibraryPath = picked;
        }
    }

    /// <summary>Re-checks the folder now (tests and the self-test; typing checks after a short pause).</summary>
    public Task CheckLibraryNowAsync() => CheckLibraryAsync(TimeSpan.Zero);

    partial void OnLibraryPathChanged(string value) => _ = CheckLibraryAsync(TimeSpan.FromMilliseconds(250));

    private async Task CheckLibraryAsync(TimeSpan delay)
    {
        var version = Interlocked.Increment(ref _checkVersion);
        IsCheckingLibrary = true;
        var text = LibraryPath;
        var result = await Task.Run(async () =>
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }

            return Volatile.Read(ref _checkVersion) == version ? LibraryFolderCheck.Run(text) : null;
        }).ConfigureAwait(true);

        if (result is null || version != Volatile.Read(ref _checkVersion))
        {
            return;
        }

        LibraryCheck = result;
        IsCheckingLibrary = false;
    }

    // ------------------------------------------------------------------ upstream import

    /// <summary>Upstream's INI, when found.</summary>
    public string? UpstreamPath { get; }

    public bool HasUpstream => UpstreamPath is not null;

    /// <summary>The INI was found but could not be read.</summary>
    public string? UpstreamError { get; }

    public bool HasUpstreamError => UpstreamError is not null;

    public bool CanImport => _upstreamIni is not null;

    /// <summary>Import upstream's settings (ticked when an INI is found).</summary>
    [ObservableProperty]
    public partial bool ImportUpstream { get; set; }

    /// <summary>The preview's lines: library folder, counts, secrets.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> ImportSummary { get; private set; } = [];

    /// <summary>Every imported key, one per line, for the "Show what is imported" expander.</summary>
    [ObservableProperty]
    public partial string ImportDetails { get; private set; } = "";

    /// <summary>Upstream's library folder was relative: the user is asked to choose the folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRelativeHint))]
    public partial string? RelativeHint { get; private set; }

    public bool HasRelativeHint => RelativeHint is not null;

    /// <summary>The import result shown in the preview (null when not importing).</summary>
    public UpstreamImportResult? ImportPreview { get; private set; }

    partial void OnImportUpstreamChanged(bool value) => ApplyImport(value);

    private void ApplyImport(bool import)
    {
        if (!import || _upstreamIni is null || UpstreamPath is null)
        {
            ImportPreview = null;
            ImportSummary = [];
            ImportDetails = "";
            RelativeHint = null;
            Draft.Update(_ => _fresh);
            if (_importedLibrary is not null && LibraryLayout.PathComparer.Equals(LibraryPath, _importedLibrary))
            {
                LibraryPath = _environment.SuggestedLibraryFolder;
            }
            else if (LibraryPath.Length == 0)
            {
                LibraryPath = _environment.SuggestedLibraryFolder;
            }

            _importedLibrary = null;
            return;
        }

        // The preview imports into the stored settings (upstream's defaults for the keys the INI lacks) without a
        // credential store, so no secret is written yet; secrets found are reported as failed and counted here.
        var (settings, result) = UpstreamIniImporter.Import(_target.Current, _upstreamIni, UpstreamPath, new UpstreamImportOptions
        {
            ApiData = ApiDataOrBundled(),
            LegacyCredentials = _environment.LegacyCredentials,
        });
        ImportPreview = result;
        Draft.Update(_ => settings);

        var lines = new List<string>();
        if (result.RelativeLibraryFolder is { } relative)
        {
            RelativeHint = Loc.Tr("ui.first_run.library.relative", ("path", relative));
            lines.Add(Loc.Tr("ui.first_run.library.import_library", ("path", relative)));
            LibraryPath = "";
        }
        else
        {
            RelativeHint = null;
            if (settings.General.LibraryFolder is { } folder)
            {
                lines.Add(Loc.Tr("ui.first_run.library.import_library", ("path", folder)));
                _importedLibrary = folder;
                LibraryPath = folder;
            }
            else
            {
                lines.Add(Loc.Tr("ui.first_run.library.import_library_none"));
            }
        }

        var secrets = result.Entries.Count(e => SecretKeys.Contains(e.Key, StringComparer.OrdinalIgnoreCase));
        var imported = result.Imported.Count();
        lines.Add(Loc.Tr("ui.first_run.library.import_counts", ("imported", imported), ("skipped", result.Skipped.Count())));
        if (secrets > 0)
        {
            lines.Add(Loc.Tr("ui.first_run.library.import_secrets"));
        }

        ImportSummary = lines;
        ImportDetails = string.Join("\n", result.Imported.Select(e => e.Key).Order(StringComparer.Ordinal));
    }

    private ApiData? ApiDataOrBundled()
    {
        if (_environment.ApiData is { } data)
        {
            return data;
        }

        return ApiData.TryParse(BundledApiFiles.ReadApi(), out var bundled) ? bundled : null;
    }

    // ------------------------------------------------------------------ shortcuts (recorded for the shell integration)

    /// <summary>
    /// The Start Menu and Desktop choices are offered unless Velopack's Setup.exe installed this copy: it made both
    /// shortcuts already, and removes them on uninstall.
    /// </summary>
    public bool ShowShortcutChoices { get; init; } = AppLocation.Kind != InstallKind.Installed;

    [ObservableProperty]
    public partial bool AddToStartMenu { get; set; } = AppLocation.Kind != InstallKind.Installed;

    [ObservableProperty]
    public partial bool AddToDesktop { get; set; } = AppLocation.Kind != InstallKind.Installed;

    [ObservableProperty]
    public partial bool RegisterFileAssociation { get; set; }

    // ------------------------------------------------------------------ finishing

    /// <summary>
    /// Finish: on a worker, creates the library folder and its channel folders, copies imported secrets to the
    /// credential store, then saves the settings with <c>firstRunSeen</c>. A failure shows the error page.
    /// </summary>
    public async Task FinishAsync()
    {
        if (IsCommitting || _completed)
        {
            return;
        }

        if (LibraryCheck is not { IsValid: true, FullPath: { } root })
        {
            PageIndex = (int)FirstRunPage.Library;
            UpdateSteps();
            return;
        }

        IsCommitting = true;
        var step = Loc.Tr("ui.first_run.step.library");
        try
        {
            await Task.Run(async () =>
            {
                if (_environment.CreateLibraryFolders)
                {
                    Directory.CreateDirectory(root);
                }

                _libraryReady = true;
                if (ImportPreview is not null && _upstreamIni is not null && UpstreamPath is not null && _environment.Credentials is { } credentials)
                {
                    // Run again with the credential store: only to copy the secrets; the settings come from the draft.
                    step = Loc.Tr("ui.first_run.library.import_check");
                    var (_, result) = UpstreamIniImporter.Import(_target.Current, _upstreamIni, UpstreamPath, new UpstreamImportOptions
                    {
                        ApiData = ApiDataOrBundled(),
                        Credentials = credentials,
                        LegacyCredentials = _environment.LegacyCredentials,
                    });
                    UpstreamIniImporter.LogResult(result, _environment.Log);
                }

                step = Loc.Tr("ui.settings.title");
                _target.Update(_ => Final(includeLibrary: true));
                await _target.FlushAsync().ConfigureAwait(false);
            }).ConfigureAwait(true);

            _environment.Log?.Info($"First-run setup finished; library {root}");
            Complete(true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _environment.Log?.Error("First-run setup failed", e);
            ErrorText = $"{step}\n\n{e}";
            IsCommitting = false;
            IsError = true;
        }
    }

    /// <summary>The settings to save: the draft, the library folder, <c>firstRunSeen</c> and the pending shortcuts and file association.</summary>
    public AppSettings Final(bool includeLibrary)
    {
        var draft = Draft.Current;
        var stored = _target.Current;
        var root = LibraryCheck?.FullPath;
        return draft with
        {
            General = draft.General with { LibraryFolder = includeLibrary && root is not null ? root : stored.General.LibraryFolder },
            Internal = draft.Internal with
            {
                FirstRunSeen = true,
                UpstreamRelativeLibraryFolder = includeLibrary ? null : draft.Internal.UpstreamRelativeLibraryFolder,
                PendingStartMenuShortcut = AddToStartMenu,
                PendingDesktopShortcut = AddToDesktop,
                PendingFileAssociation = RegisterFileAssociation,
                Window = stored.Internal.Window,
            },
        };
    }

    private void Complete(bool accepted)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        UiDispatcher.Post(() => Completed?.Invoke(this, accepted));
    }

    public void Dispose() => Settings.Dispose();
}
