using System.ComponentModel;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.Platform.Windows.Credentials;

/// <summary>
/// <see cref="ICredentialStore"/> over Windows Credential Manager: key <c>github_token</c> is the generic credential
/// <c>BlenderLauncherNet/github_token</c> (<see cref="CredentialStore.TargetFor"/>), with the key as its user name.
/// Also reads raw targets for the upstream import (<see cref="ILegacyCredentialReader"/>). Win32 failures surface
/// as <see cref="CredentialStoreException"/>.
/// </summary>
public sealed class WindowsCredentialStore : ICredentialStore, ILegacyCredentialReader
{
    /// <summary>
    /// The prefix put before each key; <see cref="Core.AppIdentity.CredentialTargetPrefix"/> by default. Tests
    /// use a throwaway prefix.
    /// </summary>
    public WindowsCredentialStore(string? targetPrefix = null)
    {
        TargetPrefix = targetPrefix ?? Core.AppIdentity.CredentialTargetPrefix;
    }

    public string TargetPrefix { get; }

    public string TargetFor(string key) => TargetPrefix + key;

    public string? Get(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        try
        {
            return CredentialStore.Read(TargetFor(key))?.Secret;
        }
        catch (Win32Exception e)
        {
            throw new CredentialStoreException($"Reading credential '{key}' failed: {e.Message}", e);
        }
    }

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            Delete(key);
            return;
        }

        try
        {
            CredentialStore.Write(TargetFor(key), key, value.Trim());
        }
        catch (Win32Exception e)
        {
            throw new CredentialStoreException($"Writing credential '{key}' failed: {e.Message}", e);
        }
    }

    public bool Delete(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        try
        {
            return CredentialStore.Delete(TargetFor(key));
        }
        catch (Win32Exception e)
        {
            throw new CredentialStoreException($"Deleting credential '{key}' failed: {e.Message}", e);
        }
    }

    public LegacyCredential? Read(string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        try
        {
            return CredentialStore.Read(target) is { } c ? new LegacyCredential(c.UserName, c.Secret) : null;
        }
        catch (Win32Exception e)
        {
            throw new CredentialStoreException($"Reading credential '{target}' failed: {e.Message}", e);
        }
    }
}
