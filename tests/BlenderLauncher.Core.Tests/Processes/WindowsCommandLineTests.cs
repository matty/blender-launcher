using System.Runtime.InteropServices;
using System.Text;
using BlenderLauncher.Core.Processes;

namespace BlenderLauncher.Core.Tests.Processes;

public class WindowsCommandLineTests
{
    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData(@"C:\dir\", @"C:\dir\")]
    [InlineData(@"C:\my dir\", @"""C:\my dir\\""")]
    [InlineData("say \"hi\"", @"""say \""hi\""""")]
    [InlineData(@"a\""b", @"""a\\\""b""")]
    public void Quote_follows_crt_rules(string argument, string expected) =>
        Assert.Equal(expected, WindowsCommandLine.Quote(argument));

    // Microsoft's "Parsing C++ command-line arguments" examples that shell32 and the CRT agree on.
    [Theory]
    [InlineData("\"abc\" d e", new[] { "abc", "d", "e" })]
    [InlineData(@"a\\b d""e f""g h", new[] { @"a\\b", "de fg", "h" })]
    [InlineData(@"a\\\""b c d", new[] { @"a\""b", "c", "d" })]
    [InlineData(@"a\\\\""b c"" d e", new[] { @"a\\b c", "d", "e" })]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("a  b ", new[] { "a", "b" })]
    public void Split_arguments_examples(string text, string[] expected) =>
        Assert.Equal(expected, WindowsCommandLine.SplitArguments(text));

    [Fact]
    public void Parse_reads_the_program_name_without_escapes() =>
        Assert.Equal([@"C:\dir\", "x"], WindowsCommandLine.Parse(@"""C:\dir\"" x"));

    [Fact]
    public void Cmd_argument_quotes_only_what_needs_it()
    {
        var expected = "\"" + string.Join(' ',
            @"C:\x.bat", "plain", "\"\"", "\"a b\"", "\"a&b\"", "\"a^b\"", "\"a|b\"", "\"(x)\"", "\"a,b\"",
            "\"say \"\"hi\"\"\"") + "\"";
        Assert.Equal(
            expected,
            WindowsCommandLine.BuildCmdArgument([@"C:\x.bat", "plain", "", "a b", "a&b", "a^b", "a|b", "(x)", "a,b", "say \"hi\""]));
    }

    // ---- against the real CommandLineToArgvW ----

    [Fact]
    public void Split_matches_CommandLineToArgvW_on_random_input()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Calls shell32.");
        var random = new Random(20261111);
        const string alphabet = "a\\\" \t";
        for (var i = 0; i < 500; i++)
        {
            var sb = new StringBuilder();
            var length = random.Next(0, 14);
            for (var j = 0; j < length; j++)
            {
                sb.Append(alphabet[random.Next(alphabet.Length)]);
            }

            var text = sb.ToString();
            var expected = Native.CommandLineToArgv("x " + text).Skip(1).ToArray();
            Assert.True(expected.SequenceEqual(WindowsCommandLine.SplitArguments(text)), $"Input: [{text}]");
            Assert.True(Native.CommandLineToArgv(text.Length == 0 ? "x" : text).SequenceEqual(WindowsCommandLine.Parse(text.Length == 0 ? "x" : text)), $"Parse input: [{text}]");
        }
    }

    [Fact]
    public void Build_round_trips_through_CommandLineToArgvW()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Calls shell32.");
        var random = new Random(4242);
        const string alphabet = "ab\\\" \t&^%";
        for (var i = 0; i < 300; i++)
        {
            var args = new string[random.Next(0, 5)];
            for (var k = 0; k < args.Length; k++)
            {
                var sb = new StringBuilder();
                var length = random.Next(0, 8);
                for (var j = 0; j < length; j++)
                {
                    sb.Append(alphabet[random.Next(alphabet.Length)]);
                }

                args[k] = sb.ToString();
            }

            const string program = @"C:\Program Files\Blender\blender.exe";
            var line = WindowsCommandLine.Build(program, args);
            Assert.Equal([program, .. args], Native.CommandLineToArgv(line));
            Assert.Equal([program, .. args], WindowsCommandLine.Parse(line));
        }
    }

    private static class Native
    {
        public static string[] CommandLineToArgv(string commandLine)
        {
            var argv = CommandLineToArgvW(commandLine, out var count);
            if (argv == IntPtr.Zero)
            {
                throw new InvalidOperationException("CommandLineToArgvW failed.");
            }

            try
            {
                var result = new string[count];
                for (var i = 0; i < count; i++)
                {
                    result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
                }

                return result;
            }
            finally
            {
                LocalFree(argv);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);
    }
}
