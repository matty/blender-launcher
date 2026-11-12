namespace BlenderLauncher.App.SelfTest;

/// <summary>
/// <c>--self-test [--out &lt;file&gt;] [--offline] [--only &lt;id,id,...&gt;] [--keep-temp]</c>.
/// Without <c>--out</c> the results go to <c>self-test-results.txt</c> in the current folder.
/// </summary>
internal sealed record SelfTestOptions(
    string OutputPath,
    bool Offline,
    IReadOnlySet<string>? Only,
    bool KeepTemp)
{
    public const string Switch = "--self-test";
    public const string DefaultOutputFile = "self-test-results.txt";

    public static bool IsRequested(string[] args) =>
        args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    public static SelfTestOptions Parse(string[] args, out string? error)
    {
        error = null;
        string? output = null;
        var offline = false;
        var keepTemp = false;
        HashSet<string>? only = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case Switch:
                    break;
                case "--offline":
                    offline = true;
                    break;
                case "--keep-temp":
                    keepTemp = true;
                    break;
                case "--out":
                    if (i + 1 >= args.Length)
                    {
                        error = "--out needs a file path";
                        break;
                    }

                    output = args[++i];
                    break;
                case "--only":
                    if (i + 1 >= args.Length)
                    {
                        error = "--only needs a comma-separated list of check IDs";
                        break;
                    }

                    only = new HashSet<string>(
                        args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        StringComparer.OrdinalIgnoreCase);
                    break;
                default:
                    error ??= $"unknown argument '{arg}'";
                    break;
            }
        }

        var fullOutput = Path.GetFullPath(output ?? DefaultOutputFile);
        return new SelfTestOptions(fullOutput, offline, only, keepTemp);
    }
}
