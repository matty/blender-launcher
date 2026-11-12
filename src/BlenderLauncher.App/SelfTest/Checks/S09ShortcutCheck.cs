using BlenderLauncher.Core;
using BlenderLauncher.Platform.Windows.Shell;

namespace BlenderLauncher.App.SelfTest.Checks;

/// <summary>
/// Spike S09: writes a <c>.lnk</c> through the source-generated IShellLinkW / IPersistFile and reads it
/// back, once from this thread-pool thread (MTA) and once from the Avalonia UI thread (STA).
/// </summary>
internal sealed class S09ShortcutCheck : ISelfTestCheck
{
    public string Id => "S09";
    public string Name => "Shortcut via generated COM";
    public bool NeedsUiThread => false;
    public bool NeedsNetwork => false;

    public async Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct)
    {
        var exe = Environment.ProcessPath ?? Path.Combine(ctx.AppDirectory, AppIdentity.ExecutableName);
        var expected = new ShortcutDefinition(
            TargetPath: exe,
            Arguments: "--self-test-shortcut \"C:\\Some Folder\\file name.blend\" ünïcødé",
            WorkingDirectory: ctx.AppDirectory.TrimEnd('\\'),
            IconPath: exe,
            IconIndex: 0,
            Description: AppIdentity.DisplayName + " self-test shortcut");

        var poolApartment = Thread.CurrentThread.GetApartmentState();
        var mtaPath = Path.Combine(ctx.WorkDirectory, AppIdentity.DisplayName + " (MTA).lnk");
        var mtaError = RoundTrip(mtaPath, expected, ctx);
        if (mtaError is not null)
        {
            return SelfTestResult.Fail($"thread pool: {mtaError}");
        }

        var staPath = Path.Combine(ctx.WorkDirectory, AppIdentity.DisplayName + " (STA).lnk");
        var staError = await ctx.RunOnUiThreadAsync(() => RoundTrip(staPath, expected, ctx));
        if (staError is not null)
        {
            return SelfTestResult.Fail($"UI thread: {staError}");
        }

        var size = new FileInfo(mtaPath).Length;
        File.Delete(mtaPath);
        File.Delete(staPath);
        return SelfTestResult.Pass(
            $"written and read back on thread pool ({poolApartment}) and UI thread (STA); " +
            $"target, args, working dir, icon, description match; {size} bytes");
    }

    private static string? RoundTrip(string path, ShortcutDefinition expected, SelfTestContext ctx)
    {
        try
        {
            ShortcutService.Write(path, expected);
            if (!File.Exists(path))
            {
                return "Save reported success but no file was written";
            }

            var actual = ShortcutService.Read(path);
            ctx.Log($"{Path.GetFileName(path)}: {actual}");
            var mismatches = new List<string>();
            Compare("target", expected.TargetPath, actual.TargetPath, ignoreCase: true, mismatches);
            Compare("args", expected.Arguments, actual.Arguments, ignoreCase: false, mismatches);
            Compare("workdir", expected.WorkingDirectory, actual.WorkingDirectory, ignoreCase: true, mismatches);
            Compare("icon", expected.IconPath, actual.IconPath, ignoreCase: true, mismatches);
            Compare("description", expected.Description, actual.Description, ignoreCase: false, mismatches);
            if (expected.IconIndex != actual.IconIndex)
            {
                mismatches.Add($"icon index {actual.IconIndex}");
            }

            return mismatches.Count == 0 ? null : "mismatch: " + string.Join(", ", mismatches);
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static void Compare(string field, string expected, string actual, bool ignoreCase, List<string> mismatches)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(expected, actual, comparison))
        {
            mismatches.Add($"{field} '{actual}' (expected '{expected}')");
        }
    }
}
