using System.Runtime.InteropServices;
using BlenderLauncher.Core.Scraping;

namespace BlenderLauncher.Core.Tests.Scraping;

/// <summary>Upstream's <c>scraper.py:123-145</c> post-filter and <c>base.py</c>/<c>automated.py</c> platform rules.</summary>
public class BuildPlatformTests
{
    [Theory]
    // amd64: x64, windows64 or amd64 anywhere (case-sensitive) ...
    [InlineData("amd64", "https://download.blender.org/release/Blender4.2/blender-4.2.3-windows-x64.zip", true)]
    [InlineData("amd64", "https://download.blender.org/release/Blender2.79/blender-2.79b-windows64.zip", true)]
    [InlineData("amd64", "https://cdn.builder.blender.org/download/daily/blender-4.4.0-alpha+main.abc-windows.amd64-release.zip", true)]
    [InlineData("amd64", "https://example.org/blender-4.2-windows-X64.zip", false)]
    // ... or bforartists / upbge in the lowercased link
    [InlineData("amd64", "https://example.org/BFORARTISTS-dev.zip", true)]
    [InlineData("amd64", "https://github.com/UPBGE/upbge/releases/download/v0.36/upbge-0.36-windows-x86_64.zip", true)]
    [InlineData("amd64", "https://cdn.builder.blender.org/download/daily/blender-4.4.0-alpha+main.abc-windows.arm64-release.zip", false)]
    // arm64: only "arm64" in the link (Bforartists and UPBGE included)
    [InlineData("arm64", "https://cdn.builder.blender.org/download/daily/blender-4.4.0-alpha+main.abc-windows.arm64-release.zip", true)]
    [InlineData("arm64", "https://www.bforartists.de/data/binaries/Bforartists-4.2.0-Windows.zip", false)]
    [InlineData("arm64", "https://example.org/blender-windows-ARM64.zip", false)]
    // any other machine name: nothing passes
    [InlineData("AMD64", "https://download.blender.org/release/Blender4.2/blender-4.2.3-windows-x64.zip", false)]
    public void Windows_link_post_filter(string machine, string link, bool passes)
    {
        Assert.Equal(passes, BuildPlatform.PassesWindowsLinkFilter(machine, link));
        Assert.Equal(passes, BuildPlatform.PassesPostFilter(HostOs.Windows, machine, link));
    }

    [Theory]
    [InlineData(HostOs.Linux)]
    public void Other_oses_have_no_post_filter(HostOs os)
    {
        Assert.True(BuildPlatform.PassesPostFilter(os, "x86_64", "https://example.org/anything-windows-arm64.zip"));
    }

    [Theory]
    [InlineData(HostOs.Windows, Architecture.X64, "amd64")]
    [InlineData(HostOs.Windows, Architecture.Arm64, "arm64")]
    [InlineData(HostOs.Windows, Architecture.X86, "x86")]
    [InlineData(HostOs.Linux, Architecture.X64, "x86_64")]
    [InlineData(HostOs.Linux, Architecture.Arm64, "aarch64")]
    [InlineData(HostOs.MacOS, Architecture.Arm64, "arm64")]
    public void Machine_names_follow_python_platform_machine(HostOs os, Architecture architecture, string expected)
    {
        Assert.Equal(expected, BuildPlatform.MachineName(os, architecture));
    }

    [Fact]
    public void Current_machine_on_this_windows_x64_box()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            Assert.Equal(HostOs.Windows, BuildPlatform.CurrentOs);
            Assert.Equal("amd64", BuildPlatform.CurrentMachine);
        }
    }

    [Theory]
    [InlineData(HostOs.Windows, "windows")]
    [InlineData(HostOs.Linux, "linux")]
    [InlineData(HostOs.MacOS, "darwin")]
    public void Builder_platform_values(HostOs os, string expected) => Assert.Equal(expected, BuildPlatform.BuilderPlatform(os));

    [Theory]
    [InlineData(HostOs.Windows, "blender-4.4.0-alpha+main.ddc9f92777cd-windows.amd64-release.zip", true)]
    [InlineData(HostOs.Windows, "BLENDER-4.4.0-alpha+main.ddc9f92777cd-WINDOWS.ARM64-release.ZIP", true)]
    [InlineData(HostOs.Windows, "blender-4.4.0-alpha+main.ddc9f92777cd-windows.amd64-release.zip.sha256", false)]
    [InlineData(HostOs.Windows, "blender-2.79b-windows32.zip", false)]
    [InlineData(HostOs.MacOS, "blender-4.4.0-alpha+main.abc-darwin.arm64-release.dmg", true)]
    [InlineData(HostOs.MacOS, "blender-4.2.0-macOS-x64.dmg", true)]
    [InlineData(HostOs.Linux, "blender-4.4.0-alpha+main.abc-linux.x86_64-release.tar.xz", true)]
    [InlineData(HostOs.Linux, "blender-4.4.0-alpha+main.abc-linux.x86_64-release.tar.xz.sha256", false)]
    public void File_name_filter(HostOs os, string fileName, bool matches)
    {
        Assert.Equal(matches, BuildPlatform.FileNameMatchesAtStart(os, fileName));
    }

    [Fact]
    public void Match_is_anchored_at_the_start_but_search_is_not()
    {
        const string name = "old-blender-4.2.3-windows-x64.zip";
        Assert.False(BuildPlatform.FileNameMatchesAtStart(HostOs.Windows, name));
        Assert.Matches(BuildPlatform.FileNameFilter(HostOs.Windows), name);
    }

    private sealed record Entry(string? Platform, string? Architecture, string? FileName);

    private static readonly Entry[] Daily =
    [
        new("windows", "amd64", "blender-4.4.0-alpha+main.abc-windows.amd64-release.zip"),
        new("windows", "arm64", "blender-4.4.0-alpha+main.abc-windows.arm64-release.zip"),
        new("windows", "amd64", "blender-4.4.0-alpha+main.abc-windows.amd64-release.msi"),
        new("linux", "x86_64", "blender-4.4.0-alpha+main.abc-linux.x86_64-release.tar.xz"),
        new("darwin", "arm64", "blender-4.4.0-alpha+main.abc-darwin.arm64-release.dmg"),
    ];

    [Fact]
    public void Builder_entries_for_this_architecture()
    {
        var picked = Select(Daily, HostOs.Windows, "amd64", out var specific);
        Assert.True(specific);
        Assert.Equal([Daily[0]], picked);

        picked = Select(Daily, HostOs.Windows, "arm64", out specific);
        Assert.True(specific);
        Assert.Equal([Daily[1]], picked);
    }

    [Fact]
    public void Architecture_matching_ignores_case()
    {
        Entry[] entries = [new("windows", "AMD64", "blender-4.4.0-windows.amd64-release.zip")];
        Assert.Single(Select(entries, HostOs.Windows, "amd64", out var specific));
        Assert.True(specific);
    }

    [Fact]
    public void Falls_back_to_any_architecture_when_none_match()
    {
        // Experimental branches often build for one architecture only.
        Entry[] entries =
        [
            new("windows", "amd64", "blender-4.4.0-alpha+npr.abc-windows.amd64-release.zip"),
            new("linux", "x86_64", "blender-4.4.0-alpha+npr.abc-linux.x86_64-release.tar.xz"),
            new("windows", null, "blender-4.4.0-alpha+npr.def-windows.amd64-release.zip"),
        ];
        var picked = Select(entries, HostOs.Windows, "arm64", out var specific);
        Assert.False(specific);
        Assert.Equal([entries[0], entries[2]], picked);
    }

    [Fact]
    public void Nothing_for_the_platform_gives_nothing()
    {
        Assert.Empty(Select([Daily[3]], HostOs.Windows, "amd64", out var specific));
        Assert.False(specific);
    }

    [Theory]
    [InlineData("amd64", "x86_64")]
    [InlineData("AMD64", "AMD64")]
    public void Fallback_architecture_label(string architecture, string expected) =>
        Assert.Equal(expected, BuildPlatform.FallbackArchitectureLabel(architecture));

    private static IReadOnlyList<Entry> Select(IEnumerable<Entry> entries, HostOs os, string machine, out bool specific) =>
        BuildPlatform.SelectBuilderEntries(entries, os, machine, e => e.Platform, e => e.Architecture, e => e.FileName, out specific);
}
