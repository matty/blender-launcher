using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.Core.Tests.Builds;

public class IsoTimestampTests
{
    [Theory]
    [InlineData("2024-12-17T08:41:00", "2024-12-17T08:41:00", true)]
    [InlineData("2024-12-17T03:40:55-05:00", "2024-12-17T03:40:55-05:00", false)]
    [InlineData("2024-07-30T12:34:00Z", "2024-07-30T12:34:00+00:00", false)]
    [InlineData("2024-07-30 12:34", "2024-07-30T12:34:00", true)]
    [InlineData("2024-07-30", "2024-07-30T00:00:00", true)]
    [InlineData("2024-07-30T12", "2024-07-30T12:00:00", true)]
    [InlineData("2024-07-30T12:34:56.5", "2024-07-30T12:34:56.500000", true)]
    [InlineData("2024-07-30T12:34:56.123456789+01:00", "2024-07-30T12:34:56.123456+01:00", false)]
    [InlineData("2024-07-30T12:34:00+0530", "2024-07-30T12:34:00+05:30", false)]
    [InlineData("2024-07-30T12:34:00+05:30:15", "2024-07-30T12:34:00+05:30:15", false)]
    public void Parses_like_fromisoformat_and_writes_like_isoformat(string text, string expected, bool naive)
    {
        var t = IsoTimestamp.Parse(text);

        Assert.Equal(expected, t.ToString());
        Assert.Equal(naive, t.IsNaive);
        Assert.Equal(t, IsoTimestamp.Parse(t.ToString()));
    }

    [Theory]
    [InlineData("30-Jul-24-12:34")]
    [InlineData("2024-13-01")]
    [InlineData("2024-07-30T25:00")]
    [InlineData("2024-07-30T12:34+05")]
    [InlineData("2024-07-30T12:34:00+24:00")]
    [InlineData(null)]
    public void Rejects_what_fromisoformat_rejects(string? text) => Assert.False(IsoTimestamp.TryParse(text, out _));

    [Fact]
    public void Aware_values_are_equal_at_the_same_instant_and_keep_their_offset()
    {
        var newYork = IsoTimestamp.Parse("2024-12-17T03:40:55-05:00");
        var utc = IsoTimestamp.Parse("2024-12-17T08:40:55+00:00");

        Assert.Equal(newYork, utc);
        Assert.Equal(newYork.GetHashCode(), utc.GetHashCode());
        Assert.Equal(TimeSpan.FromHours(-5), newYork.Offset);
        Assert.Equal(new DateTimeOffset(2024, 12, 17, 3, 40, 55, TimeSpan.FromHours(-5)), newYork.ToDateTimeOffset());
    }

    [Fact]
    public void Naive_and_aware_never_equal_and_cannot_be_ordered()
    {
        var naive = IsoTimestamp.Parse("2024-12-17T08:40:55");
        var aware = IsoTimestamp.Parse("2024-12-17T08:40:55+00:00");

        Assert.NotEqual(naive, aware);
        Assert.False(naive.TryCompareTo(aware, out _));
        Assert.Throws<InvalidOperationException>(() => naive < aware);
        Assert.True(naive < IsoTimestamp.Parse("2024-12-17T08:40:56"));
        Assert.True(aware > IsoTimestamp.Parse("2024-12-17T08:40:55+01:00"));
    }

    [Fact]
    public void Conversions_to_local_time()
    {
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

        Assert.Equal("2024-07-30T12:34:00+02:00", IsoTimestamp.AssumeLocal(new DateTime(2024, 7, 30, 12, 34, 0), plusTwo).ToString());
        Assert.Equal("2024-07-30T14:34:00+02:00", IsoTimestamp.Parse("2024-07-30T12:34:00Z").ToZone(plusTwo).ToString());
        Assert.Equal("2024-07-30T12:34:00+02:00", IsoTimestamp.Parse("2024-07-30T12:34:00").ToZone(plusTwo).ToString());
        Assert.Equal(TimeSpan.FromHours(2), IsoTimestamp.Parse("2024-07-30T12:34:00").ToDateTimeOffset(plusTwo).Offset);
        Assert.Equal("1970-01-01T00:00:00+00:00", IsoTimestamp.Epoch.ToString());
    }
}
