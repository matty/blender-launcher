using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using BlenderLauncher.Platform.Windows.Interop;

namespace BlenderLauncher.Platform.Windows.Shell;

/// <summary>What a <c>.lnk</c> file points at.</summary>
public sealed record ShortcutDefinition(
    string TargetPath,
    string Arguments = "",
    string WorkingDirectory = "",
    string IconPath = "",
    int IconIndex = 0,
    string Description = "");

/// <summary>
/// Writes and reads Windows shortcuts through the Shell Link COM object, using source-generated COM
/// (no built-in COM interop, so it works under Native AOT). Thread-safe: each call initialises COM on
/// the calling thread if needed and balances it before returning. Works from STA and MTA threads,
/// because the Shell Link object is registered as ThreadingModel=Both.
/// </summary>
public static class ShortcutService
{
    private const int MaxPath = 32768;

    /// <summary>Creates or overwrites the shortcut at <paramref name="shortcutPath"/>.</summary>
    /// <exception cref="COMException">A Shell Link call failed; the HRESULT is in the exception.</exception>
    public static void Write(string shortcutPath, ShortcutDefinition definition)
    {
        ArgumentException.ThrowIfNullOrEmpty(shortcutPath);
        ArgumentNullException.ThrowIfNull(definition);
        var fullPath = Path.GetFullPath(shortcutPath);

        WithShellLink(link =>
        {
            Check(link.SetPath(definition.TargetPath), "SetPath");
            Check(link.SetArguments(definition.Arguments), "SetArguments");
            Check(link.SetWorkingDirectory(definition.WorkingDirectory), "SetWorkingDirectory");
            Check(link.SetDescription(definition.Description), "SetDescription");
            if (definition.IconPath.Length > 0)
            {
                Check(link.SetIconLocation(definition.IconPath, definition.IconIndex), "SetIconLocation");
            }

            Check(link.SetShowCmd(ShellLinkNative.SwShowNormal), "SetShowCmd");

            // The cast is a QueryInterface through the generated ComWrappers, not built-in COM.
            var persist = (IPersistFile)link;
            Check(persist.Save(fullPath, 1), "IPersistFile.Save");
            return 0;
        });
    }

    /// <summary>Reads a shortcut. Paths are returned as stored, without resolving or expanding them.</summary>
    public static ShortcutDefinition Read(string shortcutPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(shortcutPath);
        var fullPath = Path.GetFullPath(shortcutPath);

        return WithShellLink(link =>
        {
            var persist = (IPersistFile)link;
            Check(persist.Load(fullPath, ShellLinkNative.StgmRead), "IPersistFile.Load");
            return ReadFields(link);
        });
    }

    private static unsafe ShortcutDefinition ReadFields(IShellLinkW link)
    {
        var buffer = new char[MaxPath];
        fixed (char* p = buffer)
        {
            Check(link.GetPath(p, MaxPath, 0, ShellLinkNative.SlgpRawPath), "GetPath");
            var target = new string(p);
            Check(link.GetArguments(p, MaxPath), "GetArguments");
            var arguments = new string(p);
            Check(link.GetWorkingDirectory(p, MaxPath), "GetWorkingDirectory");
            var workingDirectory = new string(p);
            Check(link.GetDescription(p, MaxPath), "GetDescription");
            var description = new string(p);
            Check(link.GetIconLocation(p, MaxPath, out var iconIndex), "GetIconLocation");
            var iconPath = new string(p);
            return new ShortcutDefinition(target, arguments, workingDirectory, iconPath, iconIndex, description);
        }
    }

    private static T WithShellLink<T>(Func<IShellLinkW, T> action)
    {
        // S_OK or S_FALSE must be balanced by CoUninitialize. RPC_E_CHANGED_MODE means the thread is
        // already an STA (the UI thread), which is fine for this object and must not be uninitialised.
        var init = ShellLinkNative.CoInitializeEx(0, ShellLinkNative.CoinitMultithreaded);
        var mustUninitialise = init is ShellLinkNative.SOk or ShellLinkNative.SFalse;
        if (!mustUninitialise && init != ShellLinkNative.RpcEChangedMode)
        {
            throw new COMException("CoInitializeEx failed", init);
        }

        try
        {
            var hr = ShellLinkNative.CoCreateInstance(
                ShellLinkNative.ClsidShellLink, 0, ShellLinkNative.ClsctxInprocServer,
                ShellLinkNative.IidShellLinkW, out var link);
            if (hr < 0 || link is null)
            {
                throw new COMException("CoCreateInstance(ShellLink) failed", hr);
            }

            try
            {
                return action(link);
            }
            finally
            {
                // Release before CoUninitialize; the unique-instance RCW is not shared with anyone.
                ((ComObject)(object)link).FinalRelease();
            }
        }
        finally
        {
            if (mustUninitialise)
            {
                ShellLinkNative.CoUninitialize();
            }
        }
    }

    private static void Check(int hr, string call)
    {
        if (hr < 0)
        {
            throw new COMException($"IShellLinkW {call} failed (0x{hr:X8})", hr);
        }
    }
}
