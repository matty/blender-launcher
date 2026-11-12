using BlenderLauncher.Core;

namespace BlenderLauncher.Core.Tests;

public class AppIdentityTests
{
    [Fact]
    public void Version_comes_from_the_build()
    {
        Assert.Equal("1.0.0-beta.1", AppIdentity.Version);
    }

    [Fact]
    public void Technical_id_differs_from_upstream()
    {
        Assert.Equal("BlenderLauncherNet", AppIdentity.TechnicalId);
        Assert.Equal("Blender Launcher.exe", AppIdentity.ExecutableName);
    }
}