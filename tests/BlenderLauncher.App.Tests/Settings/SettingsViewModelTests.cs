using Avalonia.Headless.XUnit;
using BlenderLauncher.App.ViewModels.Settings;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.App.Tests.Settings;

/// <summary>The settings view model's rules that the window relies on.</summary>
public class SettingsViewModelTests
{
    [AvaloniaFact]
    public void Restart_prompt_lists_only_restart_settings_still_changed_since_opening()
    {
        var store = SettingsStore.InMemory();
        var credentials = new InMemoryCredentialStore();
        using var vm = new SettingsViewModel(store, credentials);

        vm.ShowDetailsPanel = false;
        vm.WorkerCount = 9;
        vm.LanguageIndex = 2;
        vm.LanguageIndex = 0;
        Assert.Equal([SettingsViewModel.RestartLabel("general.workerThreadCount")], vm.PendingRestart());

        vm.ProxyUserDraft = "someone";
        vm.CommitDrafts();
        Assert.Equal("someone", credentials.Get(CredentialKeys.ProxyUser));
        Assert.Equal(2, vm.PendingRestart().Count);
        Assert.False(store.Current.Appearance.ShowDetailsPanel);
    }

    [AvaloniaFact]
    public void Invalid_user_id_is_reported_and_not_saved()
    {
        var store = SettingsStore.InMemory(new AppSettings { Connection = new ConnectionSettings { UserId = "valid-id-123" } });
        using var vm = new SettingsViewModel(store, new InMemoryCredentialStore());

        vm.UserIdDraft = "bad id!";
        vm.CommitDrafts();
        Assert.True(vm.HasUserIdError);
        Assert.Equal("valid-id-123", store.Current.Connection.UserId);

        vm.UserIdDraft = "";
        vm.CommitDrafts();
        Assert.False(vm.HasUserIdError);
        Assert.Null(store.Current.Connection.UserId);
    }

    [AvaloniaFact]
    public void Repository_eye_and_download_follow_upstreams_rules()
    {
        var store = SettingsStore.InMemory();
        using var vm = new SettingsViewModel(store, new InMemoryCredentialStore());
        var daily = vm.Repos[1];

        daily.IsShown = false;
        Assert.False(store.Current.Builds.ShowDaily);
        Assert.False(store.Current.Builds.ScrapeDaily);

        daily.IsShown = true;
        Assert.True(store.Current.Builds.ScrapeDaily);

        var weekly = vm.Repos[5];
        Assert.False(weekly.IsShown);
        weekly.IsDownloaded = true;
        Assert.True(store.Current.Builds.ShowUpbgeWeekly);
        Assert.True(store.Current.Builds.ScrapeUpbgeWeekly);
    }
}
