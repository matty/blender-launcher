using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Threading;
using BlenderLauncher.Core;

namespace BlenderLauncher.App.SelfTest.Checks;

/// <summary>
/// Smoke check: reports the runtime, confirms the published exe really is Native AOT, and
/// confirms the harness can reach the Avalonia UI thread.
/// </summary>
internal sealed class SmokeCheck : ISelfTestCheck
{
    public string Id => "Smoke";
    public string Name => "Runtime and harness smoke test";
    public bool NeedsUiThread => false;
    public bool NeedsNetwork => false;

    public async Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct)
    {
        var info = $"v{AppIdentity.Version}; {RuntimeInformation.FrameworkDescription}; " +
                   $"{RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}; " +
                   $"aot={ctx.IsNativeAot}; dynamicCode={ctx.IsDynamicCodeSupported}";
        ctx.Log(info);

        var onUiThread = await ctx.RunOnUiThreadAsync(
            () => Dispatcher.UIThread.CheckAccess() && Application.Current is not null);
        if (!onUiThread)
        {
            return SelfTestResult.Fail($"UI thread not reachable or Application.Current is null; {info}");
        }

        if (!ctx.IsNativeAot)
        {
            return SelfTestResult.Skip($"JIT build, not the published exe; {info}");
        }

        return ctx.IsDynamicCodeSupported
            ? SelfTestResult.Fail($"dynamic code is supported in the published exe; {info}")
            : SelfTestResult.Pass(info);
    }
}
