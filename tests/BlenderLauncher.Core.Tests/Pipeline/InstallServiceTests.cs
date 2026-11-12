using System.Collections.Concurrent;
using System.Net;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Pipeline;
using BlenderLauncher.Core.Scraping;

namespace BlenderLauncher.Core.Tests.Pipeline;

public sealed class InstallServiceTests : IAsyncDisposable
{
    private const string Link = "https://download.blender.org/release/Blender4.2/blender-4.2.0-windows-x64.zip";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly TempDir _temp = new();
    private readonly FakeRecycleBin _bin = new();
    private readonly TaskQueue _queue = new(2);
    private readonly ConcurrentQueue<ReadBuildInfoRequest> _infoRequests = new();
    private bool _template;

    private static byte[] BuildZip() => TestArchives.Zip(
        ("blender-4.2.0-windows-x64/blender.exe", "exe"),
        ("blender-4.2.0-windows-x64/4.2/scripts/startup.py", "print(1)"));

    private InstallService NewService(StubHandler handler, Func<ReadBuildInfoRequest, CancellationToken, Task<BuildInfo>>? read = null)
    {
        var retry = new RetryPolicy(3, TimeSpan.FromMilliseconds(10));
        var downloader = new Downloader(handler.Client, new DownloaderOptions
        {
            FirstByteTimeout = TimeSpan.FromSeconds(5),
            ProgressInterval = TimeSpan.Zero,
            FreeSpace = _ => long.MaxValue,
        });
        return new InstallService(
            new InstallServiceOptions
            {
                Paths = () => InstallPaths.For(_temp.Path),
                ReadBuildInfo = read ?? ((request, _) =>
                {
                    _infoRequests.Enqueue(request);
                    return Task.FromResult(request.Seed with { BuildHash = "abc123def456" });
                }),
                InstallTemplate = () => _template,
            },
            _queue, downloader, new Extractor(_bin, retry, progressInterval: TimeSpan.Zero), new Renamer(_bin, retry));
    }

    private static AvailableBuild Available(string link = Link, string branch = "stable", string subversion = "4.2.0") =>
        new(TestArchives.Build(link, subversion, branch)) { SizeBytes = 1000 };

    [Fact]
    public async Task Full_chain_installs_renames_and_cleans_up()
    {
        _template = true;
        Directory.CreateDirectory(_temp.Combine("template", "config"));
        await File.WriteAllTextAsync(_temp.Combine("template", "config", "startup.blend"), "t", TestContext.Current.CancellationToken);
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Bytes(BuildZip())));
        var service = NewService(handler);
        var states = new ConcurrentQueue<InstallState>();
        var job = service.Install(Available());
        job.StateChanged += j => states.Enqueue(j.State);

        Assert.Equal(InstallState.Done, await job.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken));

        var expected = _temp.Combine("stable", "blender-4.2.0-stable.abc123def456");
        Assert.Equal(expected, job.InstalledFolder);
        Assert.Equal(expected, job.InstalledBuild!.Link);
        Assert.True(File.Exists(Path.Combine(expected, "blender.exe")));
        Assert.Equal("t", File.ReadAllText(Path.Combine(expected, "4.2", "config", "startup.blend")));
        Assert.False(Directory.Exists(_temp.Combine("stable", "blender-4.2.0-windows-x64")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Staging));
        Assert.False(job.ReplacedExisting);
        Assert.Empty(service.Jobs);

        var request = Assert.Single(_infoRequests);
        Assert.Equal(_temp.Combine("stable", "blender-4.2.0-windows-x64"), request.Folder);
        Assert.Equal(request.Folder, request.Seed.Link);
        Assert.Equal("4.2.0", request.Seed.Subversion);
        Assert.Null(request.Seed.BuildHash);
        Assert.Equal(Link, request.Scraped.Link);
        Assert.Equal("blender-4.2.0-windows-x64", request.ArchiveName);

        Assert.Contains(InstallState.Extracting, states);
        Assert.Equal(InstallState.Done, states.Last());
    }

    [Fact]
    public async Task Reinstalling_replaces_the_existing_build_through_the_bin()
    {
        Directory.CreateDirectory(_temp.Combine("stable", "blender-4.2.0-stable.abc123def456"));
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Bytes(BuildZip())));
        var job = NewService(handler).Install(Available());
        Assert.Equal(InstallState.Done, await job.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.True(job.ReplacedExisting);
        Assert.Single(_bin.Binned);
    }

    [Fact]
    public async Task A_second_request_attaches_to_the_running_job()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (_, _, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return StubHandler.Bytes(BuildZip());
        });
        var service = NewService(handler);
        var added = 0;
        service.JobsChanged += _ => Interlocked.Increment(ref added);

        var first = service.Install(Available());
        var second = service.Install(Available());
        var sameVersionOtherMirror = service.Install(Available("https://mirror.example.org/blender-4.2.0-windows-x64.zip"));

        Assert.Same(first, second);
        Assert.Same(first, sameVersionOtherMirror);
        Assert.Single(service.Jobs);
        Assert.Same(first, service.Find(TestArchives.Build(Link)));

        release.SetResult();
        Assert.Equal(InstallState.Done, await first.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Requests);
        Assert.Equal(2, Volatile.Read(ref added)); // added, then removed when done

        var third = service.Install(Available());
        Assert.NotSame(first, third);
        Assert.Equal(InstallState.Done, await third.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Different_builds_get_different_jobs()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (_, _, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return StubHandler.Bytes([], "text/html", status: HttpStatusCode.NotFound);
        });
        var service = NewService(handler);
        var a = service.Install(Available());
        var b = service.Install(Available("https://download.blender.org/daily/blender-4.3.0-alpha+main.abc-windows.amd64-release.zip", "daily", "4.3.0-alpha"));
        Assert.NotSame(a, b);
        Assert.Equal(2, service.Jobs.Count);
        release.SetResult();
        await Task.WhenAll(a.Completion, b.Completion).WaitAsync(Wait, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cancel_during_download_cleans_staging()
    {
        var stalling = new StallingStream();
        var handler = new StubHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalling) }));
        var job = NewService(handler).Install(Available());
        await stalling.FirstChunkSent.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        await TaskQueueTests.WaitUntil(() => job.State == InstallState.Downloading);

        Assert.True(job.CanCancel);
        Assert.True(job.Cancel());
        Assert.Equal(InstallState.Cancelled, await job.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Staging));
        Assert.False(Directory.Exists(_temp.Combine("stable")));
        Assert.False(job.Cancel());
    }

    [Fact]
    public async Task Cancel_while_queued_never_starts()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new TaskQueue(1);
        var blocker = queue.Enqueue("blocker", _ => release.Task);
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Bytes(BuildZip())));
        var service = new InstallService(
            new InstallServiceOptions { Paths = () => InstallPaths.For(_temp.Path), ReadBuildInfo = (r, _) => Task.FromResult(r.Seed) },
            queue, new Downloader(handler.Client), new Extractor(_bin), new Renamer(_bin));

        var job = service.Install(Available());
        Assert.Equal(InstallState.Idle, job.State);
        Assert.True(job.Cancel());
        Assert.Equal(InstallState.Cancelled, await job.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken));
        release.SetResult();
        await blocker.Completion;
        Assert.Equal(0, handler.Requests);
        Assert.Empty(service.Jobs);
    }

    [Fact]
    public async Task Download_failure_fails_the_job_with_the_pipeline_error()
    {
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Bytes([], "text/html", status: HttpStatusCode.NotFound)));
        var service = NewService(handler);
        var job = service.Install(Available());
        Assert.Equal(InstallState.Failed, await job.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Equal(PipelineErrorKind.HttpStatus, Assert.IsType<PipelineException>(job.Error).Kind);
        Assert.Empty(service.Jobs);
    }

    [Fact]
    public async Task Build_info_failure_leaves_the_build_and_reports_it()
    {
        var handler = new StubHandler((_, _, _) => Task.FromResult(StubHandler.Bytes(BuildZip())));
        var job = NewService(handler, (_, _) => throw new InvalidOperationException("blender -v timed out")).Install(Available());
        Assert.Equal(InstallState.Failed, await job.Completion.WaitAsync(Wait, TestContext.Current.CancellationToken));
        var error = Assert.IsType<PipelineException>(job.Error);
        Assert.Equal(PipelineErrorKind.BuildInfo, error.Kind);
        Assert.Equal(_temp.Combine("stable", "blender-4.2.0-windows-x64"), error.Folder);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp.Staging));
    }

    [Fact]
    public async Task Upbge_and_experimental_builds_go_to_their_folders()
    {
        var upbgeZip = TestArchives.Zip(("bin/Release/blender.exe", "exe"), ("bin/Release/0.50/s.py", "s"));
        var handler = new StubHandler((request, _, _) => Task.FromResult(StubHandler.Bytes(
            request.RequestUri!.AbsolutePath.Contains("upbge", StringComparison.Ordinal) ? upbgeZip : BuildZip())));
        var service = NewService(handler);

        var upbge = service.Install(Available("https://github.com/UPBGE/upbge/releases/download/v0.50/upbge-0.50-windows-x64.zip", "upbge-stable", "0.50"));
        var experimental = service.Install(Available("https://builder.example.org/blender-4.2.0-windows-x64.zip", "experimental"));
        await Task.WhenAll(upbge.Completion, experimental.Completion).WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(InstallState.Done, upbge.State);
        Assert.StartsWith(_temp.Combine("upbge-stable") + Path.DirectorySeparatorChar, upbge.InstalledFolder, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(upbge.InstalledFolder!, "blender.exe")));
        Assert.Equal(InstallState.Done, experimental.State);
        Assert.StartsWith(_temp.Combine("experimental") + Path.DirectorySeparatorChar, experimental.InstalledFolder, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stable", "4.2.0", "blender-4.2.0-windows-x64", "4.2.0")]
    [InlineData("daily", "4.3.0-alpha", "blender-4.3.0-alpha+main.ddc9f92777cd-windows.amd64-release", "4.3.0-alpha")]
    [InlineData("bforartists", "4.3.0-dev", "Bforartists-4.3.0-Windows", "4.3.0-dev")]
    [InlineData("upbge-weekly", "0.53-alpha", "upbge-0.53-alpha-windows-x86_64-2026-11-08", "0.53.0-alpha")]
    [InlineData("stable", "4.2.0", "no-version-here", "4.2.0")]
    public void Seed_info_follows_upstream(string branch, string scrapedVersion, string folder, string expected)
    {
        var seed = InstallService.SeedInfo(_temp.Combine(folder), TestArchives.Build("https://x/y.zip", scrapedVersion, branch) with { BuildHash = "h" });
        Assert.Equal(expected, seed.Subversion);
        Assert.Null(seed.BuildHash);
        Assert.Equal(_temp.Combine(folder), seed.Link);
        Assert.Equal(branch, seed.Branch);
    }

    public async ValueTask DisposeAsync()
    {
        await _queue.DisposeAsync();
        _temp.Dispose();
    }
}
