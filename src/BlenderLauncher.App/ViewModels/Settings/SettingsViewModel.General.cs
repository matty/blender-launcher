using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.ViewModels.Detail;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Logging;
using BlenderLauncher.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlenderLauncher.App.ViewModels.Settings;

/// <summary>Settings → General.</summary>
public sealed partial class SettingsViewModel
{
    private static readonly LogLevel[] LogLevels = [LogLevel.Debug, LogLevel.Info, LogLevel.Warning, LogLevel.Error];

    /// <summary>Language names in <see cref="SettingsValidation.Languages"/> order ("Auto" first).</summary>
    public IReadOnlyList<string> LanguageNames { get; } =
        SettingsValidation.Languages.Select(l => l == "auto" ? Loc.Tr("settings.general.app.language_auto") : Loc.Tr("ui.settings.language_names." + l)).ToArray();

    public IReadOnlyList<string> DeleteActionNames { get; } =
        [Loc.Tr("settings.general.advanced.delete_actions.0"), Loc.Tr("settings.general.advanced.delete_actions.1")];

    public IReadOnlyList<string> LogLevelNames { get; } = LogLevels.Select(l => Loc.Tr("ui.settings.log_levels." + l.ToString().ToLowerInvariant())).ToArray();

    // ------------------------------------------------------------------ library folder

    /// <summary>The library folder as saved, or a "none chosen" text.</summary>
    public string LibraryFolderText => Current.General.LibraryFolder ?? Loc.Tr("ui.settings.library.none");

    public bool HasLibraryFolder => Current.General.LibraryFolder is not null;

    /// <summary>Why the library folder cannot be used, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLibraryProblem))]
    public partial string? LibraryProblem { get; private set; }

    public bool HasLibraryProblem => LibraryProblem is not null;

    /// <summary>Advice about the library folder (protected location, Blender Version Manager folder), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLibraryWarning))]
    public partial string? LibraryWarning { get; private set; }

    public bool HasLibraryWarning => LibraryWarning is not null;

    /// <summary>The relative folder an upstream import could not resolve, as a prompt to choose it; null when none.</summary>
    public string? UpstreamRelativeHint => Current.Internal.UpstreamRelativeLibraryFolder is { } path
        ? Loc.Tr("ui.settings.library.upstream_relative", ("path", path))
        : null;

    public bool HasUpstreamRelativeHint => UpstreamRelativeHint is not null;

    /// <summary>"Browse…": picks, validates and saves a library folder, offering a path relative to the exe folder.</summary>
    [RelayCommand]
    private async Task ChooseLibraryFolderAsync()
    {
        if (Host is not { } host)
        {
            return;
        }

        var start = Services?.LibraryRoot ?? Current.General.LibraryFolder;
        var picked = await host.PickFolderAsync(Loc.Tr("msg.popup.select_library"), start).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        await SetLibraryFolderAsync(picked).ConfigureAwait(true);
    }

    /// <summary>Validates <paramref name="path"/> and, when usable, saves it as the library folder.</summary>
    public async Task<bool> SetLibraryFolderAsync(string path)
    {
        var check = await Task.Run(() => LibraryLayout.Validate(path, _environment.ExeDirectory)).ConfigureAwait(true);
        ShowLibraryValidation(check);
        if (check is not { IsValid: true, FullPath: { } full })
        {
            await InformAsync(DialogKind.Error, Loc.Tr("ui.settings.library.invalid_title"), LibraryProblem ?? "").ConfigureAwait(true);
            return false;
        }

        var saved = full;
        if (LibraryLayout.IsSameOrInside(full, _environment.ExeDirectory) && !LibraryLayout.PathComparer.Equals(full, LibraryLayout.NormaliseRoot(_environment.ExeDirectory)))
        {
            var answer = await AskAsync(DialogKind.Question, Loc.Tr("ui.settings.library.relative_title"), Loc.Tr("msg.popup.relative_path_found"),
                new PromptButton(Loc.Tr("act.yes"), IsPrimary: true), new PromptButton(Loc.Tr("act.no"), IsCancel: true)).ConfigureAwait(true);
            if (answer == 0)
            {
                saved = Path.GetRelativePath(_environment.ExeDirectory, full);
            }
        }

        Update(s => s with
        {
            General = s.General with { LibraryFolder = saved },
            Internal = s.Internal with { UpstreamRelativeLibraryFolder = null },
        });
        return true;
    }

    private void InitialiseGeneral()
    {
        if (Current.General.LibraryFolder is { } folder)
        {
            // The write probe touches the disk; run it off the UI thread and show the outcome when it is back.
            _ = Task.Run(() => LibraryLayout.Validate(folder, _environment.ExeDirectory))
                .ContinueWith(t => UiDispatcher.Post(() => ShowLibraryValidation(t.Result)), TaskContinuationOptions.OnlyOnRanToCompletion);
        }
    }

    private void ShowLibraryValidation(LibraryFolderValidation check)
    {
        LibraryProblem = check.Problem switch
        {
            null => null,
            LibraryFolderProblem.Empty => Loc.Tr("ui.settings.library.problem.empty"),
            LibraryFolderProblem.InvalidPath => Loc.Tr("ui.settings.library.problem.invalid_path"),
            LibraryFolderProblem.DoesNotExist => Loc.Tr("ui.settings.library.problem.does_not_exist"),
            LibraryFolderProblem.IsAFile => Loc.Tr("ui.settings.library.problem.is_a_file"),
            LibraryFolderProblem.InsideAppFolder => Loc.Tr("ui.settings.library.problem.inside_app_folder"),
            _ => Loc.Tr("ui.settings.library.problem.not_writable"),
        };
        LibraryWarning = check.Warnings.Count == 0
            ? null
            : string.Join(" ", check.Warnings.Select(w => w == LibraryFolderWarning.ProtectedLocation
                ? Loc.Tr("ui.settings.library.warning.protected_location")
                : Loc.Tr("ui.settings.library.warning.blender_version_manager_root")));
    }

    // ------------------------------------------------------------------ application

    public int LanguageIndex
    {
        get => Math.Max(0, SettingsValidation.Languages.ToList().IndexOf(Current.General.Language));
        set
        {
            if (value >= 0 && value < SettingsValidation.Languages.Count)
            {
                Update(s => s with { General = s.General with { Language = SettingsValidation.Languages[value] } });
            }
        }
    }

    public bool MinimiseToTray
    {
        get => Current.General.MinimiseToTray;
        set => Update(s => s with { General = s.General with { MinimiseToTray = value } });
    }

    public bool LaunchMinimisedToTray
    {
        get => Current.General.LaunchMinimisedToTray;
        set => Update(s => s with { General = s.General with { LaunchMinimisedToTray = value } });
    }

    /// <summary>The worker count shown: the saved one or the automatic value.</summary>
    public decimal? WorkerCount
    {
        get => Current.General.EffectiveWorkerThreadCount(_environment.ProcessorCount);
        set
        {
            if (value is { } v)
            {
                Update(s => s with { General = s.General with { WorkerThreadCount = Math.Max(1, (int)v) } });
            }
        }
    }

    public bool IsWorkerCountAutomatic => Current.General.WorkerThreadCount is null;

    /// <summary>"Automatic (6)" while unset, else the warning when above the CPU count, else empty.</summary>
    public string WorkerCountNote =>
        IsWorkerCountAutomatic ? Loc.Tr("ui.settings.worker_automatic", ("count", Current.General.EffectiveWorkerThreadCount(_environment.ProcessorCount)))
        : IsWorkerCountAboveCpu ? Loc.Tr("settings.general.app.worker_count_warning", ("cnt", _environment.ProcessorCount))
        : "";

    public bool IsWorkerCountAboveCpu => Current.General.WorkerThreadCount > _environment.ProcessorCount;

    [RelayCommand]
    private void UseAutomaticWorkerCount() => Update(s => s with { General = s.General with { WorkerThreadCount = null } });

    public bool UsePreReleaseBuilds
    {
        get => Current.General.UsePreReleaseBuilds;
        set => Update(s => s with { General = s.General with { UsePreReleaseBuilds = value } });
    }

    public string CreateShortcutText => Loc.Tr("settings.general.app.create_shortcut", ("shortcut_type", Loc.Tr("settings.general.app.shortcut_type.windows")));

    // ------------------------------------------------------------------ file association

    public decimal? LaunchTimer
    {
        get => Current.General.LaunchTimerSeconds;
        set
        {
            if (value is { } v)
            {
                Update(s => s with { General = s.General with { LaunchTimerSeconds = (int)v } });
            }
        }
    }

    /// <summary>Upstream's suffix: " (Disabled)" at -1, "s (Immediate)" at 0, "s" otherwise.</summary>
    public string LaunchTimerSuffix => Current.General.LaunchTimerSeconds switch
    {
        < 0 => Loc.Tr("settings.general.file_assoc.launch_timer_suffix.-1"),
        0 => Loc.Tr("settings.general.file_assoc.launch_timer_suffix.0"),
        _ => Loc.Tr("settings.general.file_assoc.launch_timer_suffix.more"),
    };

    // ------------------------------------------------------------------ advanced

    public int DeleteActionIndex
    {
        get => (int)Current.General.DefaultDeleteAction;
        set
        {
            if (value is 0 or 1)
            {
                Update(s => s with { General = s.General with { DefaultDeleteAction = (DeleteAction)value } });
            }
        }
    }

    public bool PurgeTempOnStartup
    {
        get => Current.General.PurgeTempOnStartup;
        set => Update(s => s with { General = s.General with { PurgeTempOnStartup = value } });
    }

    /// <summary>"Purge Temp Folder Now": empties the port's staging folder, refused while downloads run.</summary>
    [RelayCommand]
    private async Task PurgeTempNowAsync()
    {
        var title = Loc.Tr("ui.settings.purge_title");
        if (Services is not { LibraryRoot: { } root } services)
        {
            await InformAsync(DialogKind.Warning, title, Loc.Tr("ui.settings.purge_no_library")).ConfigureAwait(true);
            return;
        }

        if (services.Install.Jobs.Any())
        {
            await InformAsync(DialogKind.Warning, title, Loc.Tr("ui.settings.purge_busy")).ConfigureAwait(true);
            return;
        }

        bool purged;
        try
        {
            purged = await Task.Run(() => services.Remover.PurgeStagingAsync(new LibraryLayout(root).StagingFolder)).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            services.Log.Warning("Purging the temporary folder failed", e);
            purged = false;
        }

        await InformAsync(purged ? DialogKind.Success : DialogKind.Error, title,
            Loc.Tr(purged ? "msg.popup.purge.success" : "msg.popup.purge.error")).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ logging

    public int LogLevelIndex
    {
        get => Math.Max(0, Array.IndexOf(LogLevels, Current.General.LogLevel));
        set
        {
            if (value >= 0 && value < LogLevels.Length)
            {
                Update(s => s with { General = s.General with { LogLevel = LogLevels[value] } });
            }
        }
    }

    public string? LogFolder => _environment.Directories?.Logs;

    public bool HasLogFolder => LogFolder is not null;

    [RelayCommand]
    private void OpenLogFolder()
    {
        if (LogFolder is { } folder)
        {
            Host?.Open(folder, isFolder: true);
        }
    }
}
