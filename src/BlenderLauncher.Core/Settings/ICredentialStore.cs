namespace BlenderLauncher.Core.Settings;

/// <summary>
/// Secret settings, kept out of <c>settings.json</c>: Windows Credential Manager under
/// <c>BlenderLauncherNet/&lt;key&gt;</c> in the app. Keys are <see cref="CredentialKeys"/>. Calls are quick and
/// synchronous. Implementations throw <see cref="CredentialStoreException"/> when the store itself fails; a
/// missing entry is not a failure.
/// </summary>
public interface ICredentialStore
{
    /// <summary>The secret for <paramref name="key"/>, or null when there is none.</summary>
    string? Get(string key);

    /// <summary>Stores <paramref name="value"/>; an empty or whitespace value deletes the entry, as upstream.</summary>
    void Set(string key, string value);

    /// <summary>Deletes the entry. Returns false when there was none.</summary>
    bool Delete(string key);
}

/// <summary>
/// Read access to raw credential targets, for importing upstream's keyring entries, which are not under
/// the port's prefix.
/// </summary>
public interface ILegacyCredentialReader
{
    /// <summary>
    /// The generic credential called <paramref name="target"/>, or null when there is none. Throws
    /// <see cref="CredentialStoreException"/> when the store fails.
    /// </summary>
    LegacyCredential? Read(string target);
}

/// <summary>A raw credential: its user name and its secret decoded as UTF-16 (Python keyring's encoding).</summary>
public sealed record LegacyCredential(string? UserName, string Secret);

/// <summary>The credential store failed (not "not found").</summary>
public sealed class CredentialStoreException : Exception
{
    public CredentialStoreException()
    {
    }

    public CredentialStoreException(string message)
        : base(message)
    {
    }

    public CredentialStoreException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>The credential keys, named as upstream's keyring user names.</summary>
public static class CredentialKeys
{
    /// <summary>The GitHub personal access token.</summary>
    public const string GitHubToken = "github_token";

    /// <summary>The proxy user name.</summary>
    public const string ProxyUser = "proxy/user";

    /// <summary>The proxy password.</summary>
    public const string ProxyPassword = "proxy/password";

    /// <summary>Every key the port stores.</summary>
    public static IReadOnlyList<string> All { get; } = [GitHubToken, ProxyUser, ProxyPassword];
}

/// <summary>
/// An <see cref="ICredentialStore"/> held in memory: the no-op fallback when no platform store exists (secrets then
/// last for the session only), and tests.
/// </summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public string? Get(string key)
    {
        lock (_gate)
        {
            return _values.GetValueOrDefault(key);
        }
    }

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value.Trim();
            }
        }
    }

    public bool Delete(string key)
    {
        lock (_gate)
        {
            return _values.Remove(key);
        }
    }
}
