namespace BlenderLauncher.App.SelfTest;

public enum SelfTestStatus
{
    Pass,
    Fail,
    Skip,
}

/// <summary>The outcome of one check. <see cref="Detail"/> is a single line; tabs and line breaks are flattened when written.</summary>
public readonly record struct SelfTestResult(SelfTestStatus Status, string Detail)
{
    public static SelfTestResult Pass(string detail = "") => new(SelfTestStatus.Pass, detail);

    public static SelfTestResult Fail(string detail) => new(SelfTestStatus.Fail, detail);

    public static SelfTestResult Skip(string detail) => new(SelfTestStatus.Skip, detail);

    public static SelfTestResult NotImplemented() => Skip("not implemented");
}
