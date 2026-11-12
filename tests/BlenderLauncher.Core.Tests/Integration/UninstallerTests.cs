using BlenderLauncher.Core.Integration;
using BlenderLauncher.Core.PlatformServices;
using BlenderLauncher.Core.Settings;

namespace BlenderLauncher.Core.Tests.Integration;

/// <summary>
/// The uninstall's safety rules: the library is never deleted, wherever it is; registrations
/// and shortcuts are removed only when they belong to this copy; the exe folder goes only when it holds only our files.
/// </summary>
public sealed class UninstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bl-uninstall-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void Data_folder_is_deleted_but_a_library_inside_it_is_kept()
    {
        var build = Touch("data", "library", "stable", "blender-4.2", "blender.exe");
        Touch("data", "logs", "a.log");
        Touch("data", "library-sibling.txt");

        var kept = Uninstaller.DeleteTreeExcept(Path.Combine(_root, "data"), Path.Combine(_root, "data", "library"));

        Assert.True(kept);
        Assert.True(File.Exists(build));
        Assert.False(Directory.Exists(Path.Combine(_root, "data", "logs")));
        Assert.False(File.Exists(Path.Combine(_root, "data", "library-sibling.txt")));
    }

    [Fact]
    public void Data_folder_inside_the_library_is_left_alone()
    {
        var file = Touch("lib", "data", "settings.json");
        Assert.True(Uninstaller.DeleteTreeExcept(Path.Combine(_root, "lib", "data"), Path.Combine(_root, "lib")));
        Assert.True(File.Exists(file));
    }

    [Theory]
    [InlineData(new[] { "Blender Launcher.exe", "libSkiaSharp.dll" }, true)]
    [InlineData(new[] { "Blender Launcher.exe", "my notes.txt" }, false)]
    [InlineData(new[] { "Blender Launcher.exe", "Blender Launcher.exe.old" }, false)]
    public void Exe_folder_goes_only_when_it_holds_only_shipped_files(string[] files, bool removable)
    {
        foreach (var f in files)
        {
            Touch("app", f);
        }

        var (list, reason) = Uninstaller.PlanExeFolder(Context(Path.Combine(_root, "app", "Blender Launcher.exe")), library: null);

        Assert.Equal(removable, list is not null);
        Assert.Equal(removable, reason is null);
    }

    [Fact]
    public void Exe_folder_holding_the_library_or_protected_is_kept()
    {
        Touch("app", "Blender Launcher.exe");
        var exe = Path.Combine(_root, "app", "Blender Launcher.exe");
        Assert.Equal("the library is in it", Uninstaller.PlanExeFolder(Context(exe), Path.Combine(_root, "app", "lib")).Reason);
        Assert.NotNull(Uninstaller.PlanExeFolder(Context(exe) with { ProtectedFolders = [Path.Combine(_root, "app")] }, null).Reason);
        Assert.NotNull(Uninstaller.PlanExeFolder(Context(exe) with { ProtectedFolders = [Path.Combine(_root, "app", "Desktop")] }, null).Reason);
    }

    [Fact]
    public void Run_removes_only_what_belongs_to_this_copy()
    {
        var exe = Touch("app", "Blender Launcher.exe");
        var association = new FakeAssociation { AssociatedExe = exe + ".other" };
        var fake = new FakeIntegration { StartupExe = exe, Registered = true };
        var credentials = new InMemoryCredentialStore();
        credentials.Set(CredentialKeys.GitHubToken, "t");
        var build = Touch("library", "daily", "b", "blender.exe");
        Touch("data", "settings.json");

        var report = Uninstaller.Run(Context(exe) with
        {
            Platform = new PlatformServiceBundle { FileAssociation = association, Startup = fake, AppRegistration = fake, Credentials = credentials },
            Directories = new DataDirectories(Path.Combine(_root, "data")),
            LibraryRoot = Path.Combine(_root, "library"),
        });

        Assert.True(report.Succeeded);
        Assert.False(association.Unregistered); // another copy's association is kept
        Assert.NotNull(association.AssociatedExe);
        Assert.Null(fake.StartupExe);
        Assert.False(fake.Registered);
        Assert.Null(credentials.Get(CredentialKeys.GitHubToken));
        Assert.True(File.Exists(build));
        Assert.False(Directory.Exists(Path.Combine(_root, "data")));
        Assert.True(report.RemoveExeFolder);
    }

    private UninstallContext Context(string exe) => new()
    {
        Platform = PlatformServiceBundle.None,
        Directories = new DataDirectories(Path.Combine(_root, "nodata")),
        ExePath = exe,
        ShippedFileNames = ["Blender Launcher.exe", "libSkiaSharp.dll"],
    };

    private sealed class FakeAssociation : IFileAssociation
    {
        public string? AssociatedExe { get; set; }

        public bool Unregistered { get; private set; }

        public bool IsAvailable => true;

        public FileAssociationState GetState(string exePath) => AssociatedExe is null ? FileAssociationState.NotRegistered
            : AssociatedExe == exePath ? FileAssociationState.ThisExecutable : FileAssociationState.OtherExecutable;

        public void Register(string exePath, string? iconPath = null) => AssociatedExe = exePath;

        public void Unregister()
        {
            Unregistered = true;
            AssociatedExe = null;
        }
    }

    private sealed class FakeIntegration : IStartupRegistration, IAppRegistration
    {
        public string? StartupExe { get; set; }

        public bool Registered { get; set; }

        public bool IsAvailable => true;

        public bool Exists => StartupExe is not null;

        public bool IsEnabled(string exePath) => StartupCommand.Matches(StartupExe is null ? null : StartupCommand.Format(StartupExe), exePath);

        public void Enable(string exePath) => StartupExe = exePath;

        public void Disable() => StartupExe = null;

        public bool IsRegistered() => Registered;

        public void Register(AppRegistrationInfo info) => Registered = true;

        public void Unregister() => Registered = false;
    }
}

public sealed class StartupCommandAndShortcutTests
{
    [Theory]
    [InlineData("\"C:\\Apps\\Blender Launcher\\Blender Launcher.exe\"", true)]
    [InlineData("\"c:\\apps\\blender launcher\\blender launcher.exe\" --hidden", true)]
    [InlineData("C:\\Apps\\Blender Launcher\\Blender Launcher.exe", true)] // upstream's unquoted form
    [InlineData("\"C:\\Other\\Blender Launcher.exe\"", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Run_value_matches_this_exe_only(string? value, bool matches) =>
        Assert.Equal(matches, StartupCommand.Matches(value, @"C:\Apps\Blender Launcher\Blender Launcher.exe"));

    [Fact]
    public async Task Launcher_shortcut_asks_before_replacing_a_foreign_one_and_not_its_own()
    {
        var folder = Path.Combine(Path.GetTempPath(), "bl-lnk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var shortcuts = new FakeShortcuts(folder);
            var exe = @"C:\Apps\Blender Launcher.exe";
            var asked = 0;
            Task<bool> Ask(string _)
            {
                asked++;
                return Task.FromResult(false);
            }

            Assert.Equal(ShortcutOutcome.Created, await LauncherShortcut.CreateAsync(shortcuts, ShortcutLocation.Desktop, exe, Ask));
            Assert.Equal(Path.Combine(folder, "Desktop", "Blender Launcher.lnk"), shortcuts.Written.Single());
            Assert.Equal(ShortcutOutcome.Replaced, await LauncherShortcut.CreateAsync(shortcuts, ShortcutLocation.Desktop, exe, Ask));
            Assert.Equal(0, asked);

            shortcuts.Write(shortcuts.Written[0], new ShortcutSpec(@"C:\Upstream\Blender Launcher.exe"));
            Assert.Equal(ShortcutOutcome.Kept, await LauncherShortcut.CreateAsync(shortcuts, ShortcutLocation.Desktop, exe, Ask));
            Assert.Equal(1, asked);
            Assert.False(LauncherShortcut.RemoveIfOurs(shortcuts, ShortcutLocation.Desktop, exe));
            Assert.True(LauncherShortcut.RemoveIfOurs(shortcuts, ShortcutLocation.Desktop, @"C:\Upstream\Blender Launcher.exe"));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private sealed class FakeShortcuts(string folder) : IShortcutService
    {
        private readonly Dictionary<string, ShortcutSpec> _files = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Written { get; } = [];

        public bool IsAvailable => true;

        public string? StartMenuFolder => Path.Combine(folder, "Start");

        public string? DesktopFolder => Path.Combine(folder, "Desktop");

        public void Write(string path, ShortcutSpec spec)
        {
            File.WriteAllText(path, spec.TargetPath);
            _files[path] = spec;
            Written.Add(path);
        }

        public ShortcutSpec? TryRead(string path) => File.Exists(path) ? _files.GetValueOrDefault(path) : null;
    }
}
