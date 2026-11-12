using BlenderLauncher.App.SelfTest;

namespace BlenderLauncher.App.Tests;

public class SelfTestFormatTests
{
    [Fact]
    public void Result_line_is_tab_separated_and_flattened()
    {
        var line = SelfTestRunner.FormatLine(SelfTestStatus.Fail, "S01", "Name", 12, "a\tb\r\nc");

        Assert.Equal("FAIL\tS01\tName\t12\ta b c", line);
    }

    [Fact]
    public void Registry_lists_every_spike_once()
    {
        var ids = SelfTestRegistry.CreateChecks().Select(c => c.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        for (var i = 1; i <= 12; i++)
        {
            Assert.Contains($"S{i:00}", ids);
        }

        // S13 (the in-place file swap) was retired when Velopack took over updates.
        Assert.DoesNotContain("S13", ids);
    }
}