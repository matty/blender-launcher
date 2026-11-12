using Avalonia.Headless.XUnit;
using BlenderLauncher.App.ViewModels;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;

namespace BlenderLauncher.App.Tests.Cli;

public class LaunchPickerViewModelTests
{
    internal sealed class FakeHost(int timer, string? defaultFolder = null) : ILaunchPickerHost
    {
        public static readonly IReadOnlyList<LibraryEntry> Builds =
        [
            Entry("lts-4.2.3", "4.2.3", "lts", "aaa111", 2024, 10),
            Entry("lts-4.2.1", "4.2.1", "lts", "bbb222", 2024, 8),
            Entry("stable-4.3.0", "4.3.0", "stable", "ccc333", 2024, 11),
            Entry("daily-4.4.0", "4.4.0-alpha", "daily", "ddd444", 2024, 12),
        ];

        public Dictionary<string, string> Saved { get; } = [];

        public List<(BuildInfo Build, LaunchMode Mode, IReadOnlyList<string> Args)> Launched { get; } = [];

        public Task<LaunchPickerLibrary> LoadAsync(CancellationToken ct) =>
            Task.FromResult(new LaunchPickerLibrary(Builds, defaultFolder, Saved, timer));

        public void SaveQuery(string versionKey, string queryText) => Saved[versionKey] = queryText;

        public void Launch(BuildInfo build, LaunchMode mode, IReadOnlyList<string> blenderArgs) => Launched.Add((build, mode, blenderArgs));

        private static LibraryEntry Entry(string folder, string version, string branch, string hash, int year, int month)
        {
            var path = Path.Combine(@"C:\lib", branch == "daily" ? "daily" : "stable", folder);
            var build = new BuildInfo
            {
                Link = path,
                Subversion = version,
                Branch = branch,
                BuildHash = hash,
                CommitTime = IsoTimestamp.Aware(new DateTimeOffset(year, month, 1, 12, 0, 0, TimeSpan.Zero)),
            };
            return new LibraryEntry(path, Path.GetDirectoryName(path)!, LibraryEntryKind.Build, build);
        }
    }

    [AvaloniaFact]
    public async Task An_explicit_query_matches_sorts_first_and_edits_stop_the_timer()
    {
        var host = new FakeHost(timer: 3);
        using var vm = new LaunchPickerViewModel(host, new LaunchPickerRequest(Query: "4.2.^-lts", BlenderArgs: ["--factory-startup"]));
        await vm.LoadAsync();

        Assert.Equal("4.2.^", vm.VersionText);
        Assert.Equal("lts", vm.BranchText);
        Assert.Equal(1, vm.MatchCount);
        Assert.Equal("4.2.3", vm.Rows[0].Version);
        Assert.True(vm.Rows[0].IsMatch);
        Assert.False(vm.Rows[1].IsMatch);
        Assert.Equal(3, vm.SecondsLeft);

        vm.BranchText = "lts,stable";
        Assert.Null(vm.SecondsLeft);
        vm.VersionText = "4.*";
        Assert.Equal(3, vm.MatchCount);
        Assert.Equal("4.2.3", vm.Target!.Version); // three match: the selected one launches
        vm.VersionText = "4.x";
        Assert.True(vm.VersionInvalid);
        Assert.Equal(0, vm.MatchCount);

        vm.VersionText = "4.3.^";
        Assert.True(vm.CanLaunch);
        Assert.True(vm.Launch());
        var launched = Assert.Single(host.Launched);
        Assert.Equal("4.3.0", launched.Build.Subversion);
        Assert.Equal(LaunchKind.Plain, launched.Mode.Kind);
        Assert.Equal(["--factory-startup"], launched.Args);
    }

    [AvaloniaFact]
    public async Task Without_a_query_the_default_build_is_chosen_and_timer_zero_launches_at_once()
    {
        var host = new FakeHost(timer: 0, defaultFolder: FakeHost.Builds[1].Path);
        using var vm = new LaunchPickerViewModel(host, new LaunchPickerRequest(OpenLast: true));
        var finished = false;
        vm.Finished += (_, launched) => finished = launched;
        await vm.LoadAsync();

        Assert.True(finished);
        var launched = Assert.Single(host.Launched);
        Assert.Equal("4.2.1", launched.Build.Subversion);
        Assert.Equal(LaunchKind.OpenLast, launched.Mode.Kind);
    }
}
