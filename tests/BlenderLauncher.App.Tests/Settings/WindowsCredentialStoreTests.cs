using BlenderLauncher.Core.Settings;
using BlenderLauncher.Platform.Windows.Credentials;

namespace BlenderLauncher.App.Tests.Settings;

/// <summary>
/// The Credential Manager adapter, against throwaway targets under a random prefix that are always deleted.
/// Never reads or writes the launcher's or upstream's real entries.
/// </summary>
public sealed class WindowsCredentialStoreTests : IDisposable
{
    private readonly string _prefix = $"BlenderLauncherNetTest-{Guid.NewGuid():N}/";
    private readonly List<string> _rawTargets = [];

    public void Dispose()
    {
        foreach (var key in CredentialKeys.All)
        {
            CredentialStore.Delete(_prefix + key);
        }

        foreach (var target in _rawTargets)
        {
            CredentialStore.Delete(target);
        }
    }

    [Fact]
    public void Set_get_and_delete()
    {
        var store = new WindowsCredentialStore(_prefix);
        Assert.Null(store.Get(CredentialKeys.GitHubToken));

        store.Set(CredentialKeys.GitHubToken, "  ghp_ünïcode-token  ");
        Assert.Equal("ghp_ünïcode-token", store.Get(CredentialKeys.GitHubToken));
        Assert.Equal(CredentialKeys.GitHubToken, CredentialStore.Read(_prefix + CredentialKeys.GitHubToken)!.UserName);

        Assert.True(store.Delete(CredentialKeys.GitHubToken));
        Assert.False(store.Delete(CredentialKeys.GitHubToken));
        Assert.Null(store.Get(CredentialKeys.GitHubToken));
    }

    [Fact]
    public void Setting_blank_deletes()
    {
        var store = new WindowsCredentialStore(_prefix);
        store.Set(CredentialKeys.ProxyPassword, "pw");
        store.Set(CredentialKeys.ProxyPassword, "   ");
        Assert.Null(store.Get(CredentialKeys.ProxyPassword));
    }

    [Fact]
    public void Default_prefix_is_the_app_identity()
    {
        Assert.Equal("BlenderLauncherNet/github_token", new WindowsCredentialStore().TargetFor(CredentialKeys.GitHubToken));
    }

    [Fact]
    public void Reads_a_raw_target_written_as_python_keyring_does()
    {
        // keyring's Windows backend: a generic credential, user name = key, secret as a UTF-16 blob.
        var target = $"github_token@{_prefix.TrimEnd('/')}";
        _rawTargets.Add(target);
        CredentialStore.Write(target, "github_token", "tok-from-keyring");

        var store = new WindowsCredentialStore(_prefix);
        Assert.Equal(new LegacyCredential("github_token", "tok-from-keyring"), store.Read(target));
        Assert.Null(store.Read(target + "-missing"));
    }
}
