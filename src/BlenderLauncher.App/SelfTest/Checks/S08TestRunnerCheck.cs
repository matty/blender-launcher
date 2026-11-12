namespace BlenderLauncher.App.SelfTest.Checks;

/// <summary>
/// Spike S8 is proven by <c>dotnet test</c> on tests/BlenderLauncher.App.Tests, not inside the exe.
/// Kept registered so the result file lists every spike.
/// </summary>
internal sealed class S08TestRunnerCheck : ISelfTestCheck
{
    public string Id => "S08";
    public string Name => "Test runner (xunit.v3 + Avalonia.Headless.XUnit)";
    public bool NeedsUiThread => false;
    public bool NeedsNetwork => false;

    public Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct) =>
        Task.FromResult(SelfTestResult.Skip("verified by dotnet test, not in exe"));
}
