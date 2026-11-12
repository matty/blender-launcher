using Microsoft.Win32;

// The folder and namespace are not called "Registry": a BlenderLauncher.Platform.Windows.Registry
// namespace would hide Microsoft.Win32.Registry from every other namespace under Platform.Windows.
namespace BlenderLauncher.Platform.Windows.RegistryAccess;

/// <summary>
/// Small HKEY_CURRENT_USER helpers (Run value, file association under Software\Classes). Uses the
/// framework's Microsoft.Win32.Registry, which is P/Invoke based and AOT-safe. No admin rights needed.
/// </summary>
public static class UserRegistry
{
    public static void SetString(string subKey, string? valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public static void SetDword(string subKey, string valueName, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    /// <summary>True when the key exists and has a value called <paramref name="valueName"/>.</summary>
    public static bool ValueExists(string subKey, string? valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return key is not null && key.GetValueNames().Contains(valueName ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    public static string? GetString(string subKey, string? valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public static bool KeyExists(string subKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return key is not null;
    }

    public static void DeleteValue(string subKey, string? valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(valueName ?? string.Empty, throwOnMissingValue: false);
    }

    /// <summary>Deletes the key and everything below it. Does nothing if it is missing.</summary>
    public static void DeleteTree(string subKey) =>
        Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);

    /// <summary>Deletes the key only if it has no values and no subkeys. Returns true if it is gone.</summary>
    public static bool DeleteIfEmpty(string subKey)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false))
        {
            if (key is null)
            {
                return true;
            }

            if (key.SubKeyCount > 0 || key.ValueCount > 0)
            {
                return false;
            }
        }

        Registry.CurrentUser.DeleteSubKey(subKey, throwOnMissingSubKey: false);
        return true;
    }
}
