using BlenderLauncher.Core.Localisation;

namespace BlenderLauncher.Core.Tests.Localisation;

public class TranslationFileLoaderTests
{
    [Fact]
    public void Upstream_act_en_loads_with_quoted_keys_and_block_scalars()
    {
        using var reader = new StreamReader(Fixtures.PathOf("act.en.yml"));
        var map = TranslationFileLoader.Load(reader);

        Assert.Equal("Library", map["tabs.library"]);
        Assert.Equal("Yes", map["yes"]);
        Assert.Equal("No", map["no"]);
        Assert.Equal(" Previous", map["lprev"]);
        Assert.Equal("%{title}: %{progress} of %{total} MB", map["prog.progress"]);
        Assert.Equal("Add to Favorites", map["a.fav.add"]);
        Assert.Equal(
            "This action opens the last file used in this Build.\n(Appends `--open-last` to the execution arguments)\nSHORTCUT: Shift + Launch or Doubleclick\n",
            map["a.prev_tooltip"]);
        Assert.Equal(
            "Check for new builds online\n(Hold SHIFT to force check stable and automated builds)\n",
            map["a.check_tooltip"]);
    }
}
