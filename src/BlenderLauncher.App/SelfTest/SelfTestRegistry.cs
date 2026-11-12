using BlenderLauncher.App.SelfTest.Checks;

namespace BlenderLauncher.App.SelfTest;

/// <summary>
/// Every self-test check, in run order. Hand-written on purpose: no reflection, so nothing is trimmed away.
/// To add a check, add its class under Checks/ and one line here.
/// </summary>
internal static class SelfTestRegistry
{
    public static IReadOnlyList<ISelfTestCheck> CreateChecks() =>
    [
        new SmokeCheck(),
        new S01JsonCheck(),
        new S02AngleSharpCheck(),
        new S03YamlCheck(),
        new S04SharpCompressCheck(),
        new S05TitleBarCheck(),
        new S06TrayCheck(),
        new S07FontsCheck(),
        new S08TestRunnerCheck(),
        new S09ShortcutCheck(),
        new S10ChildProcessCheck(),
        new S11InteropCheck(),
        new S12NetworkCheck(),
        new MainWindowCheck(),
        new SettingsWindowCheck(),
        new WizardAndDialogsCheck(),
        new IntegrationCheck(),
    ];
}
