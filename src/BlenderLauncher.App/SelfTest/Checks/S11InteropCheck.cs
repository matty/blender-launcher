using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using BlenderLauncher.Core;
using BlenderLauncher.Platform.Windows.Cli;
using BlenderLauncher.Platform.Windows.Credentials;
using BlenderLauncher.Platform.Windows.FileSystem;
using BlenderLauncher.Platform.Windows.Hotkeys;
using BlenderLauncher.Platform.Windows.Interop;
using BlenderLauncher.Platform.Windows.Ipc;
using BlenderLauncher.Platform.Windows.RegistryAccess;

namespace BlenderLauncher.App.SelfTest.Checks;

/// <summary>
/// Spike S11: named-pipe round trip, Recycle Bin delete, Credential Manager write/read/delete, global
/// hot key register/unregister, throwaway HKCU key, AttachConsole, and (informational) a junction
/// made with FSCTL_SET_REPARSE_POINT. Each sub-item is reported separately; every one cleans up after itself.
/// </summary>
internal sealed class S11InteropCheck : ISelfTestCheck
{
    public string Id => "S11";
    public string Name => "Interop set (pipe, recycle bin, credentials, hotkey, registry, console)";
    public bool NeedsUiThread => false;
    public bool NeedsNetwork => false;
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public async Task<SelfTestResult> RunAsync(SelfTestContext ctx, CancellationToken ct)
    {
        var items = new List<(string Name, bool Required, bool Ok, string Detail)>();

        async Task Run(string name, bool required, Func<Task<(bool Ok, string Detail)>> item)
        {
            (bool Ok, string Detail) result;
            try
            {
                result = await item();
            }
            catch (Exception ex)
            {
                result = (false, $"{ex.GetType().Name}: {ex.Message}");
            }

            ctx.Log($"{name}: {(result.Ok ? "PASS" : "FAIL")} {result.Detail}");
            items.Add((name, required, result.Ok, result.Detail));
        }

        await Run("pipe", true, () => PipeAsync(ct));
        await Run("recycle", true, () => Task.FromResult(RecycleBinDelete(ctx)));
        await Run("cred", true, () => Task.FromResult(Credentials()));
        await Run("hotkey", true, HotkeyAsync);
        await Run("reg", true, () => Task.FromResult(RegistryKey()));
        await Run("console", true, () => Task.FromResult(ConsoleAttach()));
        await Run("junction", false, () => Task.FromResult(JunctionItem(ctx)));

        var detail = string.Join("; ", items.Select(i =>
            $"{i.Name}={(i.Ok ? "PASS" : i.Required ? "FAIL" : "INFO-FAIL")} ({i.Detail})"));
        return items.All(i => i.Ok || !i.Required) ? SelfTestResult.Pass(detail) : SelfTestResult.Fail(detail);
    }

    private static async Task<(bool, string)> PipeAsync(CancellationToken ct)
    {
        var name = InstancePipeServer.PipeNameFor($"-selftest-{Environment.ProcessId}");
        await using var server = InstancePipeServer.TryStart(name, (request, _) => Task.FromResult("ack:" + request));
        if (server is null)
        {
            return (false, $"could not create {name}");
        }

        // A second owner must be refused: this is how a second launcher instance knows it is second.
        await using (var second = InstancePipeServer.TryStart(name, (r, _) => Task.FromResult(r)))
        {
            if (second is not null)
            {
                return (false, "a second server was allowed to own the pipe");
            }
        }

        var first = await InstancePipeClient.SendAsync(name, "show", TimeSpan.FromSeconds(5), ct);
        var again = await InstancePipeClient.SendAsync(name, "launch ünïcødé", TimeSpan.FromSeconds(5), ct);
        if (first != "ack:show" || again != "ack:launch ünïcødé")
        {
            return (false, $"replies '{first}', '{again}'");
        }

        var noServer = await InstancePipeClient.SendAsync(name + "-absent", "x", TimeSpan.FromMilliseconds(300), ct);
        return noServer is null
            ? (true, $"{name}: 2 round trips, second owner refused, absent server times out")
            : (false, "a reply came from a pipe that does not exist");
    }

    private static (bool, string) RecycleBinDelete(SelfTestContext ctx)
    {
        var path = Path.Combine(ctx.WorkDirectory, $"{AppIdentity.TechnicalId}-selftest-recycle-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "Blender Launcher self-test file; safe to delete.");
        var error = RecycleBin.TrySend([path]);
        if (error is not null)
        {
            return (false, error);
        }

        if (File.Exists(path))
        {
            return (false, "SHFileOperationW succeeded but the file is still there");
        }

        var purge = PurgeFromRecycleBin(path);
        return (true, $"file gone; {purge}");
    }

    /// <summary>
    /// Self-test clean-up only: finds the item's $I record in this user's $Recycle.Bin folder on the same
    /// drive and deletes it with its $R data file, so the run leaves nothing in the bin.
    /// </summary>
    private static string PurgeFromRecycleBin(string originalPath)
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            var root = Path.GetPathRoot(originalPath);
            if (sid is null || root is null)
            {
                return "left in bin (no SID or root)";
            }

            var bin = Path.Combine(root, "$Recycle.Bin", sid);
            foreach (var info in Directory.EnumerateFiles(bin, "$I*"))
            {
                var bytes = File.ReadAllBytes(info);
                if (bytes.Length < 28)
                {
                    continue;
                }

                var version = BitConverter.ToInt64(bytes, 0);
                string stored;
                if (version == 2)
                {
                    var chars = BitConverter.ToInt32(bytes, 24);
                    stored = Encoding.Unicode.GetString(bytes, 28, Math.Max(0, Math.Min((chars - 1) * 2, bytes.Length - 28)));
                }
                else
                {
                    stored = Encoding.Unicode.GetString(bytes, 24, bytes.Length - 24).TrimEnd('\0');
                }

                if (!string.Equals(stored, originalPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = Path.GetFileName(info);
                var data = Path.Combine(bin, "$R" + name[2..]);
                File.Delete(data);
                File.Delete(info);
                return $"purged from bin ({name}, $I v{version})";
            }

            return $"left in bin, not found under {bin}";
        }
        catch (Exception ex)
        {
            return $"left in bin as {Path.GetFileName(originalPath)} ({ex.GetType().Name})";
        }
    }

    private static (bool, string) Credentials()
    {
        var target = CredentialStore.TargetFor("selftest");
        var secret = "s3cret-ünï-" + Guid.NewGuid().ToString("N");
        try
        {
            CredentialStore.Write(target, "selftest", secret);
            var read = CredentialStore.Read(target);
            if (read is null || read.Secret != secret || read.UserName != "selftest")
            {
                return (false, $"read back {(read is null ? "nothing" : $"user '{read.UserName}', secret length {read.Secret.Length}")}");
            }

            if (!CredentialStore.Delete(target))
            {
                return (false, "delete reported nothing to delete");
            }

            var after = CredentialStore.Read(target);
            var deleteAgain = CredentialStore.Delete(target);
            return after is null && !deleteAgain
                ? (true, $"{target}: write, read (UTF-16 blob), delete; missing reads as null")
                : (false, "credential still present after delete");
        }
        finally
        {
            CredentialStore.Delete(target);
        }
    }

    private static async Task<(bool, string)> HotkeyAsync()
    {
        const uint vkF24 = 0x87;
        const HotkeyModifiers modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.NoRepeat;
        using var hotkeys = new GlobalHotkeyThread();

        var first = await hotkeys.RegisterAsync(modifiers, vkF24);
        if (!first.Success)
        {
            // Somebody else owns the combination: the conflict path is what is being exercised, so report it.
            return first.Win32Error == HotkeyNative.ErrorHotkeyAlreadyRegistered
                ? (true, $"Ctrl+Alt+Shift+F24 already taken elsewhere, reported as: {first.Error}")
                : (false, $"register failed: {first.Win32Error} {first.Error}");
        }

        var conflict = await hotkeys.RegisterAsync(modifiers, vkF24);
        if (conflict.Success)
        {
            return (false, "the same combination registered twice");
        }

        var unregistered = await hotkeys.UnregisterAsync(first.Id);
        var again = await hotkeys.RegisterAsync(modifiers, vkF24);
        var unregisteredAgain = again.Success && await hotkeys.UnregisterAsync(again.Id);
        return unregistered && unregisteredAgain
            ? (true, $"Ctrl+Alt+Shift+F24 registered, duplicate refused with {conflict.Win32Error} \"{conflict.Error}\", unregistered, re-registered, unregistered")
            : (false, $"unregister={unregistered}, re-register={again.Success} ({again.Error})");
    }

    private static (bool, string) RegistryKey()
    {
        var parent = $@"Software\{AppIdentity.TechnicalId}";
        var key = parent + @"\SelfTest";
        var parentExisted = UserRegistry.KeyExists(parent);
        var value = Guid.NewGuid().ToString("N");
        try
        {
            UserRegistry.SetString(key, "Probe", value);
            var read = UserRegistry.GetString(key, "Probe");
            UserRegistry.DeleteTree(key);
            if (read != value)
            {
                return (false, $"read back '{read}'");
            }

            return UserRegistry.KeyExists(key)
                ? (false, "key still present after delete")
                : (true, $@"HKCU\{key} write, read, delete{(parentExisted ? "" : "; parent removed")}");
        }
        finally
        {
            UserRegistry.DeleteTree(key);
            if (!parentExisted)
            {
                UserRegistry.DeleteIfEmpty(parent);
            }
        }
    }

    private static (bool, string) ConsoleAttach()
    {
        var outcome = ParentConsole.TryAttach(out var error);
        switch (outcome)
        {
            case ConsoleAttachOutcome.Redirected:
                return (true, "stdout redirected, attach skipped by design");
            case ConsoleAttachOutcome.NoParentConsole:
                return (true, "AttachConsole works; parent has no console (ERROR_INVALID_HANDLE)");
            case ConsoleAttachOutcome.AlreadyAttached:
            case ConsoleAttachOutcome.Failed:
                return (false, $"{outcome}, error {error}");
        }

        try
        {
            var marker = $"{AppIdentity.DisplayName} self-test: attached to the parent console ({Environment.ProcessId})";
            var write = WriteAndReadBack(marker);
            return (write.Written, $"attached to parent console; {write.Detail}");
        }
        finally
        {
            ParentConsole.Detach();
        }
    }

    private static unsafe (bool Written, string Detail) WriteAndReadBack(string marker)
    {
        var handle = ConsoleNative.GetStdHandle(ConsoleNative.StdOutputHandle);
        var line = marker + "\r\n";
        uint written;
        fixed (char* p = line)
        {
            if (!ConsoleNative.WriteConsole(handle, p, (uint)line.Length, out written, 0))
            {
                return (false, $"WriteConsoleW error {Marshal.GetLastPInvokeError()}");
            }
        }

        if (!ConsoleNative.GetConsoleScreenBufferInfo(handle, out var info) || info.CursorY == 0)
        {
            return (true, $"wrote {written} chars; read-back not possible");
        }

        var buffer = new char[marker.Length];
        uint read;
        fixed (char* p = buffer)
        {
            var coord = (uint)(ushort)(info.CursorY - 1) << 16;
            if (!ConsoleNative.ReadConsoleOutputCharacter(handle, p, (uint)buffer.Length, coord, out read))
            {
                return (true, $"wrote {written} chars; read-back error {Marshal.GetLastPInvokeError()}");
            }
        }

        var back = new string(buffer, 0, (int)read);
        return (true, back == marker
            ? $"wrote {written} chars and read the line back from the screen buffer"
            : $"wrote {written} chars; read-back differs ('{back}')");
    }

    private static (bool, string) JunctionItem(SelfTestContext ctx)
    {
        var target = Path.Combine(ctx.WorkDirectory, "junction target");
        var link = Path.Combine(ctx.WorkDirectory, "junction link");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "probe.txt"), "probe");
        try
        {
            Junction.Create(link, target);
            var info = new DirectoryInfo(link);
            var viaLink = File.ReadAllText(Path.Combine(link, "probe.txt"));
            var linkTarget = info.LinkTarget;
            var isReparse = info.Attributes.HasFlag(FileAttributes.ReparsePoint);
            Directory.Delete(link);
            var targetSurvived = File.Exists(Path.Combine(target, "probe.txt"));
            var ok = viaLink == "probe" && isReparse && targetSurvived && !Directory.Exists(link)
                     && linkTarget is not null && linkTarget.EndsWith("junction target", StringComparison.OrdinalIgnoreCase);
            return (ok, $"FSCTL_SET_REPARSE_POINT, no admin; LinkTarget '{linkTarget}', read through link, link deleted, target intact");
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }
}
