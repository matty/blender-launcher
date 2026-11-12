using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace BlenderLauncher.Core.Scraping;

/// <summary>Upstream's <c>get_platform()</c> values.</summary>
public enum HostOs
{
    Windows,
    Linux,
    MacOS,
}

/// <summary>
/// The platform and architecture rules the scrapers share, as pure functions of an OS and a machine name:
/// <list type="bullet">
/// <item>the machine name, in Python's <c>platform.machine().lower()</c> spelling (<see cref="MachineName"/>);</item>
/// <item>upstream's file-name filter <c>regex_filter()</c> (<c>threads/scraping/base.py</c>);</item>
/// <item>the builder.blender.org JSON entry match with its any-architecture fallback (<c>automated.py:50-64</c>);</item>
/// <item>the Windows link post-filter every scraped build passes through (<c>scraper.py:123-145</c>).</item>
/// </list>
/// </summary>
public static partial class BuildPlatform
{
    /// <summary>The OS this process runs on.</summary>
    public static HostOs CurrentOs =>
        OperatingSystem.IsWindows() ? HostOs.Windows : OperatingSystem.IsMacOS() ? HostOs.MacOS : HostOs.Linux;

    /// <summary>
    /// This machine's name as upstream's <c>get_architecture()</c> returns it: Python's <c>platform.machine()</c>
    /// lowercased, but from the OS architecture rather than the process's. The launcher ships as x64 only, so on
    /// ARM64 Windows it runs emulated and still offers native ARM64 Blender builds (an emulated x64 Python reports
    /// <c>amd64</c> there).
    /// </summary>
    public static string CurrentMachine => MachineName(CurrentOs, RuntimeInformation.OSArchitecture);

    /// <summary>
    /// Python's <c>platform.machine().lower()</c> for <paramref name="architecture"/> on <paramref name="os"/>:
    /// Windows <c>amd64</c>/<c>arm64</c>/<c>x86</c>; Linux <c>x86_64</c>/<c>aarch64</c>/<c>i686</c>;
    /// macOS <c>x86_64</c>/<c>arm64</c>.
    /// </summary>
    public static string MachineName(HostOs os, Architecture architecture) => (os, architecture) switch
    {
        (HostOs.Windows, Architecture.X64) => "amd64",
        (HostOs.Windows, Architecture.Arm64) => "arm64",
        (HostOs.Windows, Architecture.X86) => "x86",
        (HostOs.Linux, Architecture.Arm64) => "aarch64",
        (HostOs.Linux, Architecture.X86) => "i686",
        (HostOs.MacOS, Architecture.Arm64) => "arm64",
        (_, Architecture.X64) => "x86_64",
        (_, Architecture.Arm) => "armv7l",
        _ => architecture.ToString().ToLowerInvariant(),
    };

    /// <summary>builder.blender.org's <c>platform</c> value for <paramref name="os"/>: <c>windows</c>, <c>linux</c>, <c>darwin</c>.</summary>
    public static string BuilderPlatform(HostOs os) => os switch
    {
        HostOs.Windows => "windows",
        HostOs.Linux => "linux",
        HostOs.MacOS => "darwin",
        _ => throw new ArgumentOutOfRangeException(nameof(os), os, null),
    };

    /// <summary>
    /// Upstream's <c>regex_filter()</c>, unanchored and case-insensitive. The stable scraper uses it as Python's
    /// <c>re.search</c> (<see cref="Regex.IsMatch(string)"/>); the builder scraper uses
    /// <see cref="FileNameMatchesAtStart"/>, as Python's <c>re.match</c>.
    /// </summary>
    public static Regex FileNameFilter(HostOs os) => os switch
    {
        HostOs.Windows => WindowsFilter(),
        HostOs.MacOS => MacFilter(),
        _ => LinuxFilter(),
    };

    /// <summary><c>regex_filter().match(name)</c>: the filter must match from the first character.</summary>
    public static bool FileNameMatchesAtStart(HostOs os, string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var match = FileNameFilter(os).Match(fileName);

        // The leftmost match starts at 0 whenever a match at 0 exists, so this equals Python's re.match.
        return match.Success && match.Index == 0;
    }

    /// <summary>
    /// Pass 1 of <c>ScraperAutomated.scrape</c>: the platform is the builder's value for <paramref name="os"/>,
    /// the architecture equals <paramref name="machine"/> ignoring case, and the file name passes
    /// <see cref="FileNameMatchesAtStart"/>.
    /// </summary>
    public static bool BuilderEntryMatches(HostOs os, string machine, string? platform, string? architecture, string? fileName) =>
        BuilderEntryMatchesPlatform(os, platform, fileName) &&
        string.Equals(architecture?.ToLowerInvariant(), machine.ToLowerInvariant(), StringComparison.Ordinal);

    /// <summary>Pass 2 (the fallback): <see cref="BuilderEntryMatches"/> without the architecture check.</summary>
    public static bool BuilderEntryMatchesPlatform(HostOs os, string? platform, string? fileName) =>
        platform == BuilderPlatform(os) && fileName is not null && FileNameMatchesAtStart(os, fileName);

    /// <summary>
    /// Both passes: the entries for <paramref name="machine"/>, or, when there are none, every entry for the
    /// platform with <paramref name="architectureSpecific"/> false. Then the caller labels each build with its
    /// architecture (<see cref="FallbackArchitectureLabel"/>) and logs a warning, as upstream.
    /// </summary>
    public static IReadOnlyList<T> SelectBuilderEntries<T>(
        IEnumerable<T> entries,
        HostOs os,
        string machine,
        Func<T, string?> platform,
        Func<T, string?> architecture,
        Func<T, string?> fileName,
        out bool architectureSpecific)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var all = entries as IReadOnlyList<T> ?? [.. entries];
        var specific = all.Where(e => BuilderEntryMatches(os, machine, platform(e), architecture(e), fileName(e))).ToList();
        architectureSpecific = specific.Count > 0;
        return architectureSpecific ? specific : all.Where(e => BuilderEntryMatchesPlatform(os, platform(e), fileName(e))).ToList();
    }

    /// <summary>
    /// The architecture text upstream appends (<c>" | " + architecture</c>) to a fallback build's prerelease:
    /// <c>amd64</c> becomes <c>x86_64</c>, anything else is kept as given (case included).
    /// </summary>
    public static string FallbackArchitectureLabel(string architecture) => architecture == "amd64" ? "x86_64" : architecture;

    /// <summary>
    /// The post-filter applied to every scraped build (<c>scraper.py:123-145</c>). On Windows: for <c>arm64</c> the
    /// link must contain <c>arm64</c>; for <c>amd64</c> it must contain <c>x64</c>, <c>windows64</c> or <c>amd64</c>
    /// (case-sensitive), or its lowercased text <c>bforartists</c> or <c>upbge</c>; any other machine passes
    /// nothing. Other OSes pass everything.
    /// </summary>
    public static bool PassesPostFilter(HostOs os, string machine, string link) =>
        os != HostOs.Windows || PassesWindowsLinkFilter(machine, link);

    /// <summary>The Windows half of <see cref="PassesPostFilter"/>.</summary>
    public static bool PassesWindowsLinkFilter(string machine, string link)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(link);
        if (machine == "arm64" && link.Contains("arm64", StringComparison.Ordinal))
        {
            return true;
        }

        if (machine == "amd64")
        {
            if (link.Contains("x64", StringComparison.Ordinal) || link.Contains("windows64", StringComparison.Ordinal) ||
                link.Contains("amd64", StringComparison.Ordinal))
            {
                return true;
            }

            var lower = link.ToLowerInvariant();
            return lower.Contains("bforartists", StringComparison.Ordinal) || lower.Contains("upbge", StringComparison.Ordinal);
        }

        return false;
    }

    [GeneratedRegex("blender-.+win.+64.+zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsFilter();

    [GeneratedRegex("blender-.+(macOS|darwin).+dmg$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MacFilter();

    [GeneratedRegex("blender-.+lin.+64.+tar+(?!.*sha256).*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinuxFilter();
}
