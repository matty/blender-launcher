namespace BlenderLauncher.Platform.Windows;

/// <summary>Basic facts about the running Windows installation.</summary>
public static class PlatformInfo
{
    /// <summary>True on Windows 11 (build 22000 or later), where the snap-layout flyout exists.</summary>
    public static bool IsWindows11OrLater => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>True on the minimum supported version, Windows 10 1809 (build 17763), or later.</summary>
    public static bool IsSupportedVersion => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);
}
