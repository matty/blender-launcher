namespace BlenderLauncher.Core.Settings.Upstream;

/// <summary>Where an upstream secret was found.</summary>
public enum UpstreamSecretSource
{
    /// <summary>Credential target <c>BlenderLauncher</c> whose user name is the key.</summary>
    Keyring,

    /// <summary>Credential target <c>&lt;key&gt;@BlenderLauncher</c>.</summary>
    KeyringCompound,

    /// <summary>Plain text in the INI (upstream's legacy storage and its fallback when the keyring failed).</summary>
    Ini,
}

/// <summary>A secret read from upstream's storage.</summary>
public sealed record UpstreamSecret(string Value, UpstreamSecretSource Source);

/// <summary>
/// Finds an upstream keyring value the way Python keyring's Windows backend stores it, then upstream's
/// INI fallback:
/// <list type="number">
/// <item>Generic credential <c>BlenderLauncher</c> when its user name is the key. keyring writes the first user
/// name of a service there.</item>
/// <item>Generic credential <c>&lt;key&gt;@BlenderLauncher</c>, where keyring puts every further user name of the
/// same service.</item>
/// <item>The INI value of the same key (<c>github_token</c>, <c>proxy/host</c>…) in plain text.</item>
/// </list>
/// Secrets are UTF-16 blobs, which <see cref="ILegacyCredentialReader"/> decodes. Values are trimmed and an empty
/// value counts as absent, as upstream.
/// </summary>
public static class UpstreamCredentialLookup
{
    /// <summary>The compound target keyring uses for <paramref name="key"/>: <c>&lt;key&gt;@BlenderLauncher</c>.</summary>
    public static string CompoundTarget(string key) => $"{key}@{UpstreamPaths.KeyringService}";

    /// <summary>
    /// Looks <paramref name="key"/> up. A credential store failure is reported through <paramref name="error"/>
    /// and the lookup carries on with the next place.
    /// </summary>
    public static UpstreamSecret? Find(string key, ILegacyCredentialReader? reader, QSettingsIni? ini, out string? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        error = null;
        if (reader is not null)
        {
            try
            {
                var main = reader.Read(UpstreamPaths.KeyringService);
                if (main is not null && string.Equals(main.UserName, key, StringComparison.Ordinal) && Trimmed(main.Secret) is { } a)
                {
                    return new UpstreamSecret(a, UpstreamSecretSource.Keyring);
                }

                var compound = reader.Read(CompoundTarget(key));
                if (compound is not null && Trimmed(compound.Secret) is { } b)
                {
                    return new UpstreamSecret(b, UpstreamSecretSource.KeyringCompound);
                }
            }
            catch (CredentialStoreException e)
            {
                error = e.Message;
            }
        }

        return Trimmed(ini?.GetString(key)) is { } c ? new UpstreamSecret(c, UpstreamSecretSource.Ini) : null;
    }

    private static string? Trimmed(string? value)
    {
        var t = value?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }
}
