using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.Core.Tests.Builds;

public class ChannelTests
{
    [Theory]
    [InlineData("lts", "stable")]
    [InlineData("npr-prototype", "experimental")]
    [InlineData("upbge", "experimental")]
    public void Download_destination_matches_upstream(string branch, string folder) =>
        Assert.Equal(folder, Channels.DownloadFolderForBranch(branch));

    [Theory]
    [InlineData("lts", "stable", Channel.Lts)]
    [InlineData("pr99", null, Channel.Patch)]
    [InlineData("npr-prototype", "experimental", Channel.Experimental)]
    [InlineData("stable", "custom", Channel.Custom)]
    public void Channel_from_branch_and_folder(string branch, string? folder, Channel expected) =>
        Assert.Equal(expected, Channels.FromBranch(branch, folder));

    [Fact]
    public void Every_channel_round_trips_through_its_branch_and_lands_in_its_folder()
    {
        foreach (var channel in Enum.GetValues<Channel>())
        {
            var folder = Channels.LibraryFolderOf(channel);
            Assert.Contains(folder, Channels.LibraryFolders);

            // Lts shares stable's folder and Patch experimental's; the folder alone gives the base channel.
            if (channel is not (Channel.Lts or Channel.Patch))
            {
                Assert.Equal(channel, Channels.FromLibraryFolder(folder));
            }

            if (Channels.BranchOf(channel) is { } branch)
            {
                Assert.Equal(channel, Channels.FromBranch(branch));
                Assert.Equal(folder, Channels.DownloadFolderForBranch(branch));
            }
            else
            {
                Assert.Equal(Channel.Custom, channel);
            }
        }
    }

    [Theory]
    [InlineData("template", null)]
    [InlineData("Stable", null)]
    public void Channel_from_library_folder(string folder, Channel? expected) =>
        Assert.Equal(expected, Channels.FromLibraryFolder(folder));

    [Fact]
    public void Stable_and_lts_share_a_folder_and_patch_builds_live_in_experimental()
    {
        Assert.Equal(Channels.LibraryFolderOf(Channel.Stable), Channels.LibraryFolderOf(Channel.Lts));
        Assert.Equal("experimental", Channels.LibraryFolderOf(Channel.Patch));
        Assert.True(Channels.IsUpbge(Channel.UpbgeWeekly));
        Assert.False(Channels.IsUpbge(Channel.Bforartists));
    }
}
