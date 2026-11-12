namespace BlenderLauncher.Core.Tests;

/// <summary>Files under Fixtures/, copied beside the test assembly.</summary>
internal static class Fixtures
{
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string ReadText(string name) => File.ReadAllText(PathOf(name));
}
