using System.Runtime.InteropServices;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.App.Services;
using BlenderLauncher.App.ViewModels.Detail;
using BlenderLauncher.Core;
using BlenderLauncher.Core.Integration;
using BlenderLauncher.Core.PlatformServices;
using CommunityToolkit.Mvvm.Input;

namespace BlenderLauncher.App.ViewModels.Settings;

/// <summary>
/// Settings → General's Windows integration: launch at startup, the launcher shortcut, the
/// <c>.blend</c> file association and the Add/Remove Programs entry. The registry state is read when the window opens
/// and after each change made here.
/// </summary>
public sealed partial class SettingsViewModel
{
    private FileAssociationState _association;
    private bool _startupEnabled;
    private bool _startupExists;

    private PlatformServiceBundle? Platform => _environment.Platform;

    /// <summary>True when the platform has the shell integration (the controls are hidden otherwise).</summary>
    public bool HasIntegration => Platform?.FileAssociation.IsAvailable == true;

    // ------------------------------------------------------------------ launch at startup

    /// <summary>"Launch When System Starts": on only when the Run value starts this executable.</summary>
    public bool LaunchAtStartup
    {
        get => _startupEnabled;
        set
        {
            if (Platform is not { Startup.IsAvailable: true } platform || value == _startupEnabled)
            {
                return;
            }

            Integrate(() =>
            {
                if (value)
                {
                    platform.Startup.Enable(_environment.ExePath);
                }
                else
                {
                    platform.Startup.Disable();
                }
            });
        }
    }

    /// <summary>Shown when the Run value starts another copy of the launcher.</summary>
    public string StartupNote => _startupExists && !_startupEnabled ? Loc.Tr("ui.integration.startup_other") : "";

    public bool HasStartupNote => StartupNote.Length > 0;

    // ------------------------------------------------------------------ file association

    public string AssociationStateText => Loc.Tr("ui.integration.assoc_state." + _association switch
    {
        FileAssociationState.ThisExecutable => "this",
        FileAssociationState.OtherExecutable => "other",
        _ => "none",
    });

    public bool CanRegisterAssociation => HasIntegration && _association != FileAssociationState.ThisExecutable;

    public bool CanUnregisterAssociation => HasIntegration && _association != FileAssociationState.NotRegistered;

    [RelayCommand]
    private Task RegisterAssociationAsync() => Platform is { } platform
        ? IntegrateAndTellAsync(() => platform.FileAssociation.Register(_environment.ExePath), Loc.Tr("ui.integration.assoc_registered"))
        : Task.CompletedTask;

    [RelayCommand]
    private Task UnregisterAssociationAsync() => Platform is { } platform
        ? IntegrateAndTellAsync(platform.FileAssociation.Unregister, Loc.Tr("ui.integration.assoc_unregistered"))
        : Task.CompletedTask;

    // ------------------------------------------------------------------ launcher shortcut

    /// <summary>"Create Blender Launcher Shortcut": asks Start Menu or Desktop, then writes it (asking before replacing another).</summary>
    [RelayCommand]
    private async Task CreateLauncherShortcutAsync()
    {
        if (Platform is not { Shortcuts.IsAvailable: true } platform)
        {
            return;
        }

        var title = Loc.Tr("ui.integration.shortcut_title");
        var answer = await AskAsync(DialogKind.Question, title, Loc.Tr("ui.integration.shortcut_where"),
            new PromptButton(Loc.Tr("ui.integration.start_menu"), IsPrimary: true),
            new PromptButton(Loc.Tr("ui.integration.desktop")),
            new PromptButton(Loc.Tr("act.cancel"), IsCancel: true)).ConfigureAwait(true);
        if (answer is not (0 or 1))
        {
            return;
        }

        var location = answer == 0 ? ShortcutLocation.StartMenu : ShortcutLocation.Desktop;
        var confirm = _environment.ConfirmReplaceShortcut ?? (_ => Task.FromResult(false));
        try
        {
            var outcome = await LauncherShortcut.CreateAsync(platform.Shortcuts, location, _environment.ExePath, confirm).ConfigureAwait(true);
            var path = LauncherShortcut.PathFor(platform.Shortcuts, location) ?? "";
            if (outcome is ShortcutOutcome.Created or ShortcutOutcome.Replaced)
            {
                await InformAsync(DialogKind.Success, title, Loc.Tr("ui.integration.shortcut_created", ("path", path))).ConfigureAwait(true);
            }
            else if (outcome == ShortcutOutcome.Kept)
            {
                await InformAsync(DialogKind.Info, title, Loc.Tr("ui.integration.shortcut_kept", ("path", path))).ConfigureAwait(true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
        {
            await InformAsync(DialogKind.Error, Loc.Tr("ui.integration.failed_title"), e.Message).ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------ Add/Remove Programs

    /// <summary>Opt-in; the app controller creates or removes the entry when this changes.</summary>
    public bool RegisterInAddRemovePrograms
    {
        get => Current.General.RegisterInAddRemovePrograms;
        set => Update(s => s with { General = s.General with { RegisterInAddRemovePrograms = value } });
    }

    /// <summary>The Add/Remove Programs choice is offered unless Velopack's Setup.exe installed this copy (it has an entry).</summary>
    public bool ShowAddRemovePrograms => HasIntegration && AppLocation.Kind != InstallKind.Installed;

    // ------------------------------------------------------------------ helpers

    /// <summary>Reads the registry state the controls show.</summary>
    private void RefreshIntegration()
    {
        if (Platform is not { } platform || !HasIntegration)
        {
            return;
        }

        try
        {
            _association = platform.FileAssociation.GetState(_environment.ExePath);
            _startupExists = platform.Startup.Exists;
            _startupEnabled = platform.Startup.IsEnabled(_environment.ExePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Services?.Log.Warning("Reading the Windows integration state failed", e);
        }

        OnPropertyChanged(nameof(LaunchAtStartup));
        OnPropertyChanged(nameof(StartupNote));
        OnPropertyChanged(nameof(HasStartupNote));
        OnPropertyChanged(nameof(AssociationStateText));
        OnPropertyChanged(nameof(CanRegisterAssociation));
        OnPropertyChanged(nameof(CanUnregisterAssociation));
    }

    private void Integrate(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _ = InformAsync(DialogKind.Error, Loc.Tr("ui.integration.failed_title"), e.Message);
        }

        RefreshIntegration();
    }

    private async Task IntegrateAndTellAsync(Action action, string done)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            RefreshIntegration();
            await InformAsync(DialogKind.Error, Loc.Tr("ui.integration.failed_title"), e.Message).ConfigureAwait(true);
            return;
        }

        RefreshIntegration();
        await InformAsync(DialogKind.Success, Loc.Tr("settings.general.file_assoc.label"), done).ConfigureAwait(true);
    }
}
