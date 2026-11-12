using System.Diagnostics;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Processes;

namespace BlenderLauncher.Core.Tests.Builds;

public class LaunchArgsBuilderTests
{
    private static readonly Func<string, bool> NothingExists = _ => false;

    private static LaunchCommand Build(LaunchRequest request, Func<string, bool>? fileExists = null) =>
        LaunchArgsBuilder.Build(request, fileExists ?? NothingExists);

    // ---- test_build_parser.py test_get_args, Windows column (root C:\, info.link C:\blender) ----

    private static readonly LaunchRequest Info = new() { BuildFolder = @"C:\blender" };

    private static readonly LaunchRequest InfoC = new() { BuildFolder = @"C:\blender", CustomExecutable = "bforartists" };

    [Fact]
    public void Upstream_get_args_windows_column()
    {
        Assert.Equal([@"C:\blender\blender.exe"], Build(Info).Argv);

        // An explicit .exe runs through cmd.
        Assert.Equal(["cmd", "/C", @"C:\blender\bforartists.exe"], Build(Info with { Script = "bforartists.exe" }).Argv);
        Assert.Equal([@"C:\blender\bforartists"], Build(InfoC).Argv);
        Assert.Equal([@"C:\blender\blender.exe", "--open-last"], Build(Info with { Mode = LaunchMode.OpenLast }).Argv);
        Assert.Equal(
            [@"C:\blender\blender.exe", @"C:\file.blend"],
            Build(Info with { Mode = LaunchMode.WithBlendFile(@"C:\file.blend") }).Argv);
    }

    [Fact]
    public void Command_line_blender_args_come_last_and_never_reach_a_script()
    {
        var request = Info with
        {
            StartupArguments = "--debug",
            Mode = LaunchMode.WithBlendFile(@"C:\file.blend"),
            ExtraArguments = ["-b", "--python-expr", "import sys; sys.exit(3)"],
        };
        Assert.Equal(
            [@"C:\blender\blender.exe", "--debug", @"C:\file.blend", "-b", "--python-expr", "import sys; sys.exit(3)"],
            Build(request).Argv);
        Assert.Equal(["cmd", "/C", @"C:\blender\blender_debug_log.cmd"], Build(Info with { Script = "blender_debug_log.cmd", ExtraArguments = ["-b"] }).Argv);
    }

    // ---- executable resolution ----

    [Fact]
    public void Bforartists_exe_is_used_when_it_exists()
    {
        var asked = new List<string>();
        var cmd = Build(Info, p => { asked.Add(p); return true; });
        Assert.Equal([@"C:\blender\bforartists.exe"], cmd.Argv);
        Assert.Equal([@"C:\blender\bforartists.exe"], asked);
    }

    [Fact]
    public void Custom_executable_rules()
    {
        // It wins over bforartists.exe; empty counts as unset.
        Assert.Equal([@"C:\blender\bforartists"], Build(InfoC, _ => true).Argv);
        Assert.Equal([@"C:\blender\blender.exe"], Build(Info with { CustomExecutable = "" }).Argv);

        // Forward slashes and a subfolder get native separators; an absolute path replaces the folder.
        Assert.Equal([@"C:\blender\bin\blender.exe"], Build(Info with { CustomExecutable = "bin/./blender.exe" }).Argv);
        Assert.Equal([@"D:\tools\blender.exe"], Build(Info with { CustomExecutable = @"D:\tools\blender.exe" }).Argv);
    }

    // ---- startup arguments ----

    [Theory]
    [InlineData("-con --debug-python", new[] { "-con", "--debug-python" })]
    [InlineData("--python-expr import\\bpy -con", new[] { "--python-expr", "import\\bpy", "-con" })]
    public void Startup_arguments_without_quotes_split_as_upstream(string startup, string[] expected)
    {
        // Upstream: [exe, *startup.split(" ")].
        Assert.Equal(expected, startup.Split(' '));
        Assert.Equal([@"C:\blender\blender.exe", .. expected], Build(Info with { StartupArguments = startup }).Argv);
    }

    [Theory]
    [InlineData("--python \"C:\\My Scripts\\start.py\" -con", new[] { "--python", @"C:\My Scripts\start.py", "-con" })]
    [InlineData("--env-system-scripts \"C:\\a b\\\\\"", new[] { "--env-system-scripts", @"C:\a b\" })]
    [InlineData("-a \\\"x\\\"", new[] { "-a", "\"x\"" })]
    [InlineData("  -con   --factory-startup ", new[] { "-con", "--factory-startup" })]
    [InlineData("-con\t-d", new[] { "-con", "-d" })]
    [InlineData("\"\"", new[] { "" })]
    public void Startup_arguments_follow_windows_rules(string startup, string[] expected) =>
        Assert.Equal([@"C:\blender\blender.exe", .. expected], Build(Info with { StartupArguments = startup }).Argv);

    [Fact]
    public void Startup_arguments_come_before_the_blend_file() =>
        Assert.Equal(
            [@"C:\blender\blender.exe", "-con", @"C:\work\scene.blend"],
            Build(Info with { StartupArguments = "-con", Mode = LaunchMode.WithBlendFile(@"C:\work\scene.blend") }).Argv);

    // ---- paths ----

    [Fact]
    public void Paths_with_spaces_stay_one_argument_and_are_quoted_in_the_command_line()
    {
        var cmd = Build(new LaunchRequest
        {
            BuildFolder = @"C:\Program Files\Blender Builds\stable\blender-4.2.0",
            Mode = LaunchMode.WithBlendFile(@"C:\My Projects\scene one.blend"),
        });
        Assert.Equal([@"C:\Program Files\Blender Builds\stable\blender-4.2.0\blender.exe", @"C:\My Projects\scene one.blend"], cmd.Argv);
        Assert.Equal(
            "\"C:\\Program Files\\Blender Builds\\stable\\blender-4.2.0\\blender.exe\" \"C:\\My Projects\\scene one.blend\"",
            cmd.CommandLine);
        Assert.False(cmd.UsesCmd);
        Assert.Equal(cmd.Argv[0], cmd.ApplicationName);
    }

    [Theory]
    [InlineData(@"\\NAS\project\file.blend", @"\\NAS\project\file.blend")]
    [InlineData("//NAS/project/file.blend", @"\\NAS\project\file.blend")]
    [InlineData(@"\\NAS\project with space\a\\b\file.blend", @"\\NAS\project with space\a\b\file.blend")]
    [InlineData("C:/work/file.blend", @"C:\work\file.blend")]
    public void Blend_paths_keep_native_separators_and_unc(string path, string expected) =>
        Assert.Equal(expected, Build(Info with { Mode = LaunchMode.WithBlendFile(path) }).Argv[^1]);

    [Fact]
    public void Unc_build_folder_is_kept() =>
        Assert.Equal([@"\\NAS\builds\blender-4.2.0\blender.exe"], Build(Info with { BuildFolder = @"\\NAS\builds\blender-4.2.0\" }).Argv);

    // ---- batch files ----

    [Fact]
    public void Batch_custom_executable_runs_through_cmd_with_startup_arguments()
    {
        var cmd = Build(Info with { CustomExecutable = "run.BAT", StartupArguments = "-con", Mode = LaunchMode.OpenLast });
        Assert.Equal(["cmd", "/C", @"C:\blender\run.BAT", "-con", "--open-last"], cmd.Argv);
        Assert.True(cmd.UsesCmd);
        Assert.Equal("cmd", cmd.ApplicationName);
        Assert.Equal(@"cmd /S /C ""C:\blender\run.BAT -con --open-last""", cmd.CommandLine);
        Assert.False(cmd.RequiresConsole);
    }

    [Fact]
    public void Batch_with_spaces_ampersand_and_caret_is_quoted_for_cmd()
    {
        var cmd = Build(new LaunchRequest
        {
            BuildFolder = @"C:\My Builds\a&b^c",
            CustomExecutable = "run me.cmd",
            StartupArguments = "-con \"x & y\"",
            Mode = LaunchMode.WithBlendFile(@"C:\work\R&D (v2)\scene.blend"),
        });
        Assert.Equal(["cmd", "/C", @"C:\My Builds\a&b^c\run me.cmd", "-con", "x & y", @"C:\work\R&D (v2)\scene.blend"], cmd.Argv);
        Assert.Equal(
            @"cmd /S /C """"C:\My Builds\a&b^c\run me.cmd"" -con ""x & y"" ""C:\work\R&D (v2)\scene.blend""""",
            cmd.CommandLine);
    }

    [Fact]
    public void Batch_through_real_cmd_receives_each_argument_intact()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Runs cmd.exe.");
        var dir = Path.Combine(Path.GetTempPath(), "bl-launch " + Guid.NewGuid().ToString("N")[..8], "a&b ^c (x)");
        Directory.CreateDirectory(dir);
        try
        {
            // Writes each parameter, without its surrounding quotes, to out.txt beside the script.
            File.WriteAllText(Path.Combine(dir, "run me.cmd"),
                "@echo off\r\nsetlocal EnableDelayedExpansion\r\n" +
                "set \"out=%~dp0out.txt\"\r\n" +
                "type nul > \"!out!\"\r\n" +
                ":loop\r\nif \"%~1\"==\"\" goto done\r\n" +
                "set \"a=%~1\"\r\n(echo(!a!)>> \"!out!\"\r\nshift\r\ngoto loop\r\n:done\r\n");

            var command = Build(new LaunchRequest
            {
                BuildFolder = dir,
                CustomExecutable = "run me.cmd",
                StartupArguments = "-con \"x & y\" a^b \"semi;colon=eq,comma\"",
                Mode = LaunchMode.WithBlendFile(@"C:\work\R&D (v2)\scene.blend"),
            });

            var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
            {
                Arguments = command.CommandLine["cmd ".Length..],
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi)!)
            {
                Assert.True(p.WaitForExit(30_000));
            }

            var lines = File.ReadAllLines(Path.Combine(dir, "out.txt"));
            Assert.Equal(["-con", "x & y", "a^b", "semi;colon=eq,comma", @"C:\work\R&D (v2)\scene.blend"], lines);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
        }
    }

    // ---- debug scripts and the console ----

    [Fact]
    public void Debug_scripts_always_get_a_visible_console()
    {
        Assert.Equal(
            ["blender_debug_log.cmd", "blender_factory_startup.cmd", "blender_debug_gpu.cmd", "blender_debug_gpu_glitchworkaround.cmd"],
            LaunchArgsBuilder.DebugScripts);
        foreach (var script in LaunchArgsBuilder.DebugScripts)
        {
            var cmd = Build(Info with { Script = script, HideConsole = true, StartupArguments = "-con" });
            Assert.Equal(["cmd", "/C", @"C:\blender\" + script], cmd.Argv);
            Assert.True(cmd.RequiresConsole);
            Assert.False(cmd.HideConsole);
        }
    }

    [Fact]
    public void Hide_console_is_passed_through()
    {
        Assert.True(Build(Info with { HideConsole = true }).HideConsole);
        Assert.False(Build(Info with { HideConsole = false }).HideConsole);
    }

    [Fact]
    public void Request_for_a_build_uses_its_link_and_custom_executable()
    {
        var build = new BuildInfo
        {
            Link = @"C:\lib\custom\upbge",
            Subversion = "0.45.0",
            CommitTime = IsoTimestamp.Epoch,
            Branch = "upbge-stable",
            CustomExecutable = "blenderplayer.exe",
        };
        Assert.Equal([@"C:\lib\custom\upbge\blenderplayer.exe"], Build(LaunchRequest.For(build)).Argv);
    }

    // ---- command line round trip ----

    public static TheoryData<string, string, string> RoundTripRequests => new()
    {
        { @"C:\blender", "", "" },
        { @"C:\Program Files\b", "--python \"C:\\a b\\c.py\" -con", @"C:\x y\z.blend" },
        { @"C:\b", "--python-expr \"print(\\\"hi\\\")\" \"C:\\trailing slash\\\\\"", @"\\NAS\share\f.blend" },
        { @"C:\b", "\"\" \"a\tb\"", "" },
    };

    [Theory]
    [MemberData(nameof(RoundTripRequests))]
    public void Command_line_parses_back_to_argv(string folder, string startup, string blend)
    {
        var cmd = Build(new LaunchRequest
        {
            BuildFolder = folder,
            StartupArguments = startup,
            Mode = blend.Length > 0 ? LaunchMode.WithBlendFile(blend) : LaunchMode.Plain,
        });
        Assert.Equal(cmd.Argv, WindowsCommandLine.Parse(cmd.CommandLine));
    }

    [Fact]
    public void Native_path_rules()
    {
        Assert.Equal(@"C:\", LaunchArgsBuilder.NativePath("C:/"));
        Assert.Equal(@"C:\a\..\b", LaunchArgsBuilder.NativePath(@"C:\a\..\b\"));
        Assert.Equal(@"C:\x.exe", LaunchArgsBuilder.Join(@"C:\blender", @"\x.exe"));
        Assert.Equal(@"\\srv\share\x.exe", LaunchArgsBuilder.Join(@"\\srv\share\blender", @"\x.exe"));
    }
}
