using System.ComponentModel;
using System.Runtime.InteropServices;
using BlenderLauncher.Core;
using BlenderLauncher.Platform.Windows.Interop;

namespace BlenderLauncher.Platform.Windows.Credentials;

/// <summary>A generic credential read back from Credential Manager.</summary>
public sealed record StoredCredential(string Target, string? UserName, string Secret);

/// <summary>
/// Generic credentials in Windows Credential Manager. Secrets are stored as UTF-16, as Python keyring
/// does, so entries written by upstream can be read the same way.
/// </summary>
public static class CredentialStore
{
    /// <summary>The launcher's target name for a key, for example "BlenderLauncherNet/github_token".</summary>
    public static string TargetFor(string key) => AppIdentity.CredentialTargetPrefix + key;

    /// <exception cref="Win32Exception">CredWriteW failed.</exception>
    public static unsafe void Write(string target, string? userName, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentNullException.ThrowIfNull(secret);

        fixed (char* targetPtr = target)
        fixed (char* userPtr = userName)
        fixed (char* secretPtr = secret)
        {
            var credential = new CredentialNative.CredentialW
            {
                Type = CredentialNative.CredTypeGeneric,
                TargetName = targetPtr,
                UserName = userPtr,
                CredentialBlob = (byte*)secretPtr,
                CredentialBlobSize = (uint)(secret.Length * sizeof(char)),
                Persist = CredentialNative.CredPersistLocalMachine,
            };
            if (!CredentialNative.CredWrite(in credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"CredWriteW failed for {target}");
            }
        }
    }

    /// <summary>Returns the credential, or null when there is none.</summary>
    /// <exception cref="Win32Exception">CredReadW failed for a reason other than "not found".</exception>
    public static unsafe StoredCredential? Read(string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        if (!CredentialNative.CredRead(target, CredentialNative.CredTypeGeneric, 0, out var buffer))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == CredentialNative.ErrorNotFound
                ? null
                : throw new Win32Exception(error, $"CredReadW failed for {target}");
        }

        try
        {
            var credential = (CredentialNative.CredentialW*)buffer;
            var secret = credential->CredentialBlob is null
                ? string.Empty
                : new string((char*)credential->CredentialBlob, 0, (int)(credential->CredentialBlobSize / sizeof(char)));
            var userName = credential->UserName is null ? null : new string(credential->UserName);
            return new StoredCredential(target, userName, secret);
        }
        finally
        {
            CredentialNative.CredFree(buffer);
        }
    }

    /// <summary>Deletes the credential. Returns false when there was none.</summary>
    /// <exception cref="Win32Exception">CredDeleteW failed for a reason other than "not found".</exception>
    public static bool Delete(string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        if (CredentialNative.CredDelete(target, CredentialNative.CredTypeGeneric, 0))
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        return error == CredentialNative.ErrorNotFound
            ? false
            : throw new Win32Exception(error, $"CredDeleteW failed for {target}");
    }
}
