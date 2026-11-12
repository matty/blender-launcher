using System.Globalization;
using System.Runtime.InteropServices;

namespace BlenderLauncher.Core.Net;

/// <summary>
/// Builds the launcher's User-Agent from <see cref="AppIdentity.UserAgentFormat"/>
/// (<c>BlenderLauncherNet/{version} ({os}; {arch})</c>), using <see cref="AppIdentity.UserAgentWithUserIdFormat"/>
/// only when a valid user ID is set. Upstream sent
/// <c>Blender-Launcher-v2/v.{ver}/{Platform}-{release}/UserID-{uuid}</c> with a generated ID on every request.
/// </summary>
public static class UserAgent
{
    /// <summary>The User-Agent for this process: <see cref="AppIdentity"/>'s version, the OS and its architecture.</summary>
    public static string For(string? userId) => Build(AppIdentity.Version, CurrentOs(), CurrentArchitecture(), userId);

    /// <summary>
    /// Formats the User-Agent. An invalid user ID (see <see cref="ConnectionOptions.IsValidUserId"/>) is left out
    /// rather than sent, so nothing a user types can break the header.
    /// </summary>
    public static string Build(string version, string os, string architecture, string? userId) =>
        ConnectionOptions.IsValidUserId(userId)
            ? string.Format(CultureInfo.InvariantCulture, AppIdentity.UserAgentWithUserIdFormat, AppIdentity.TechnicalId, version, os, architecture, userId)
            : string.Format(CultureInfo.InvariantCulture, AppIdentity.UserAgentFormat, AppIdentity.TechnicalId, version, os, architecture);

    /// <summary><c>Windows 10.0.26100</c>, <c>Linux</c> or <c>macOS</c> (the kernel version is not useful there).</summary>
    public static string CurrentOs()
    {
        if (OperatingSystem.IsWindows())
        {
            var v = Environment.OSVersion.Version;
            return $"Windows {v.Major}.{v.Minor}.{v.Build}";
        }

        return OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "Unknown";
    }

    /// <summary>The OS architecture in .NET's spelling, lower case: <c>x64</c>, <c>arm64</c>, <c>x86</c>.</summary>
    public static string CurrentArchitecture() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        Architecture.Arm => "arm",
        var other => ((int)other).ToString(CultureInfo.InvariantCulture),
    };
}
