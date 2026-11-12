using System.Text;
using BlenderLauncher.Core.Logging;

namespace BlenderLauncher.Core.Tests.Logging;

public sealed class LogTests : IDisposable
{
    private const string Ghp = "ghp_abcdefghijklmnopqrstuvwxyz0123456789AB";
    private const string Pat = "github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyzABCDEFGHIJ0123456789";

    private readonly string _dir = Directory.CreateTempSubdirectory("bl-log-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // --- redaction ---

    [Theory]
    [InlineData("token is " + Ghp, "token is ghp_***REDACTED***")]
    [InlineData("pat " + Pat + " end", "pat github_pat_***REDACTED*** end")]
    [InlineData("Authorization: token " + Ghp, "Authorization: token ***REDACTED***")]
    [InlineData("{'Authorization': 'token " + Ghp + "', 'User-Agent': 'x'}", "{'Authorization': 'token ***REDACTED***', 'User-Agent': 'x'}")]
    [InlineData("{\"Authorization\": \"Bearer abc.def-ghi_jkl\"}", "{\"Authorization\": \"Bearer ***REDACTED***\"}")]
    [InlineData("authorization=Basic dXNlcjpwYXNz", "authorization=Basic ***REDACTED***")]
    [InlineData("Authorization: secretvalue123", "Authorization: ***REDACTED***")]
    [InlineData("headers: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig", "headers: Bearer ***REDACTED***")]
    [InlineData("sent token 0123456789abcdef0123456789", "sent token ***REDACTED***")]
    [InlineData("gho_abcdefghijklmnopqrstuvwxyz0123456789", "gho_***REDACTED***")]
    public void Redacts_secrets(string input, string expected) =>
        Assert.Equal(expected, LogRedactor.Redact(input));

    [Theory]
    [InlineData("Downloading Blender 4.2.1 from https://download.blender.org/release/")]
    [InlineData("The token expired")]
    [InlineData("bearer auth is not used")]
    [InlineData("ghp_short")]
    [InlineData("")]
    public void Leaves_ordinary_text_alone(string input) =>
        Assert.Equal(input, LogRedactor.Redact(input));

    [Fact]
    public void Redacts_in_the_file_including_exception_text()
    {
        using (var log = Log.Open(_dir))
        {
            log.Info("Using " + Ghp);
            log.Error("Request failed", new HttpRequestException("Authorization: token " + Ghp));
        }

        var text = ReadLog();
        Assert.DoesNotContain(Ghp, text, StringComparison.Ordinal);
        Assert.Contains("Using ghp_***REDACTED***", text, StringComparison.Ordinal);
        Assert.Contains("Authorization: token ***REDACTED***", text, StringComparison.Ordinal);
    }

    // --- format and levels ---

    [Fact]
    public void Formats_like_python_logging() =>
        Assert.Equal(
            "[2026-11-11 09:05:03,042:WARNING] hello",
            Log.Format(new DateTime(2026, 11, 11, 9, 5, 3, 42), LogLevel.Warning, "hello"));

    [Fact]
    public void Writes_formatted_lines_with_local_time()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 1, 2, 3, 4, 5, 6, TimeSpan.Zero));
        using (var log = Log.Open(_dir, LogLevel.Debug, new LogOptions { Time = clock }))
        {
            log.Debug("d");
            log.Info("i");
            log.Warning("w");
            log.Error("e");
        }

        var stamp = clock.GetLocalNow().DateTime.ToString("yyyy-MM-dd HH:mm:ss,fff", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(
            [$"[{stamp}:DEBUG] d", $"[{stamp}:INFO] i", $"[{stamp}:WARNING] w", $"[{stamp}:ERROR] e"],
            File.ReadAllLines(Path.Combine(_dir, Log.FileName)));
    }

    [Fact]
    public void Drops_records_below_the_minimum_level()
    {
        using (var log = Log.Open(_dir, LogLevel.Warning))
        {
            log.Debug("no");
            log.Info("no");
            log.Warning("yes");
            log.MinimumLevel = LogLevel.Debug;
            log.Debug("now yes");
        }

        var lines = File.ReadAllLines(Path.Combine(_dir, Log.FileName));
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("WARNING] yes", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("DEBUG] now yes", lines[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("INFO", LogLevel.Info)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("ERROR", LogLevel.Error)]
    [InlineData("CRITICAL", LogLevel.Error)]
    [InlineData("nonsense", LogLevel.Info)]
    [InlineData(null, LogLevel.Info)]
    public void Parses_level_names(string? name, LogLevel expected) => Assert.Equal(expected, Log.ParseLevel(name));

    [Fact]
    public void Logs_exception_with_stack_trace()
    {
        Exception caught;
        try
        {
            ThrowForTest();
            throw new UnreachableException();
        }
        catch (InvalidOperationException e)
        {
            caught = e;
        }

        using (var log = Log.Open(_dir))
        {
            log.Error("Windows - Blender Launcher 0.1.0", caught);
        }

        var text = ReadLog();
        Assert.Contains("ERROR] Windows - Blender Launcher 0.1.0" + Environment.NewLine + "System.InvalidOperationException: boom", text, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowForTest), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Appends_to_an_existing_file()
    {
        File.WriteAllText(Path.Combine(_dir, Log.FileName), "old line" + Environment.NewLine);
        using (var log = Log.Open(_dir))
        {
            log.Info("new line");
        }

        var lines = File.ReadAllLines(Path.Combine(_dir, Log.FileName));
        Assert.Equal("old line", lines[0]);
        Assert.EndsWith("new line", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flush_writes_queued_records_and_dispose_is_idempotent()
    {
        var log = Log.Open(_dir);
        log.Info("one");
        await log.FlushAsync();
        Assert.Contains("one", ReadLog(), StringComparison.Ordinal);
        await log.DisposeAsync();
        log.Dispose();
        log.Info("after dispose is ignored");
        await log.FlushAsync();
        Assert.DoesNotContain("after dispose", ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public void Listener_receives_redacted_lines()
    {
        var seen = new List<(LogLevel, string)>();
        using (var log = Log.Open(_dir, options: new LogOptions { Listener = (l, s) => seen.Add((l, s)) }))
        {
            log.Warning("key " + Ghp);
        }

        var (level, line) = Assert.Single(seen);
        Assert.Equal(LogLevel.Warning, level);
        Assert.EndsWith("WARNING] key ghp_***REDACTED***", line, StringComparison.Ordinal);
    }

    // --- rotation ---

    [Fact]
    public void Rotates_at_max_bytes_keeping_two_backups()
    {
        var options = new LogOptions { MaxBytes = 1000 };
        using (var log = Log.Open(_dir, options: options))
        {
            for (var i = 0; i < 100; i++)
            {
                log.Info($"line {i:D3} " + new string('x', 60));
            }
        }

        var main = Path.Combine(_dir, Log.FileName);
        Assert.True(File.Exists(main));
        Assert.True(File.Exists(main + ".1"));
        Assert.True(File.Exists(main + ".2"));
        Assert.False(File.Exists(main + ".3"));
        foreach (var file in new[] { main, main + ".1", main + ".2" })
        {
            Assert.InRange(new FileInfo(file).Length, 1, 1000);
        }

        // Newest records are in the main file, older ones in .1, older still in .2.
        Assert.Contains("line 099", File.ReadAllText(main), StringComparison.Ordinal);
        var lastOfBackup1 = File.ReadAllLines(main + ".1")[^1];
        var firstOfMain = File.ReadAllLines(main)[0];
        Assert.True(string.CompareOrdinal(lastOfBackup1[^64..], firstOfMain[^64..]) < 0);
        Assert.True(string.CompareOrdinal(File.ReadAllLines(main + ".2")[^1][^64..], File.ReadAllLines(main + ".1")[0][^64..]) < 0);
    }

    [Fact]
    public void Default_rotation_is_one_megabyte()
    {
        using (var log = Log.Open(_dir))
        {
            var line = new string('y', 1000);
            for (var i = 0; i < 1100; i++)
            {
                log.Info(line);
            }
        }

        var main = Path.Combine(_dir, Log.FileName);
        Assert.True(File.Exists(main + ".1"));
        Assert.InRange(new FileInfo(main + ".1").Length, 1024 * 1024 - 1100, 1024 * 1024);
    }

    [Fact]
    public void Falls_back_to_not_rotating_when_the_file_is_locked()
    {
        var main = Path.Combine(_dir, Log.FileName);
        using var log = Log.Open(_dir, options: new LogOptions { MaxBytes = 500 });

        // Another process (here another handle) with the file open but no delete sharing blocks the rename.
        using (new FileStream(main, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            for (var i = 0; i < 30; i++)
            {
                log.Info($"locked {i:D2} " + new string('z', 40));
            }

            log.Flush();
        }

        Assert.False(log.IsRotating);
        Assert.True(log.IsFileOpen);
        Assert.False(File.Exists(main + ".1"));
        log.Info("still logging");
        log.Flush();
        var text = ReadLog();
        Assert.Contains("locked 00", text, StringComparison.Ordinal);
        Assert.Contains("locked 29", text, StringComparison.Ordinal);
        Assert.Contains("still logging", text, StringComparison.Ordinal);
    }

    // --- concurrency ---

    [Fact]
    public void Concurrent_writers_lose_and_interleave_nothing()
    {
        const int Threads = 8;
        const int PerThread = 2000;
        using (var log = Log.Open(_dir, options: new LogOptions { QueueCapacity = 64, MaxBytes = 0 }))
        {
            Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, t =>
            {
                for (var i = 0; i < PerThread; i++)
                {
                    log.Info($"t{t} n{i}");
                }
            });
        }

        var lines = File.ReadAllLines(Path.Combine(_dir, Log.FileName));
        Assert.Equal(Threads * PerThread, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"^\[\d{4}-\d\d-\d\d \d\d:\d\d:\d\d,\d{3}:INFO\] t\d n\d+$", l));
        for (var t = 0; t < Threads; t++)
        {
            // Each thread's records stay in order.
            var mine = lines.Where(l => l.Contains($"] t{t} ", StringComparison.Ordinal))
                .Select(l => int.Parse(l[(l.LastIndexOf('n') + 1)..], System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            Assert.Equal(Enumerable.Range(0, PerThread), mine);
        }
    }

    [Fact]
    public void Concurrent_writers_with_rotation_keep_every_line_whole()
    {
        using (var log = Log.Open(_dir, options: new LogOptions { MaxBytes = 4096, BackupCount = 1000 }))
        {
            Parallel.For(0, 4000, i => log.Info($"record {i:D5} {new string('r', 30)}"));
        }

        var all = Directory.GetFiles(_dir).SelectMany(File.ReadAllLines).ToArray();
        Assert.Equal(4000, all.Length);
        Assert.Equal(4000, all.Select(l => l[(l.IndexOf("record", StringComparison.Ordinal) + 7)..][..5]).Distinct().Count());
    }

    private string ReadLog()
    {
        using var stream = new FileStream(Path.Combine(_dir, Log.FileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowForTest() => throw new InvalidOperationException("boom");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class UnreachableException : Exception;
}
