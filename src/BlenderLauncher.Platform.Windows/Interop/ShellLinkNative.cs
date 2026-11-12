using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace BlenderLauncher.Platform.Windows.Interop;

// Source-generated COM declarations for the Shell Link object. Every vtable slot is declared in order,
// because the generator numbers slots by declaration order (base interface slots first).

/// <summary>IPersist, the base of IPersistFile. One slot after IUnknown.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0000010c-0000-0000-C000-000000000046")]
internal partial interface IPersist
{
    [PreserveSig]
    int GetClassID(out Guid pClassID);
}

/// <summary>IPersistFile. Inherits IPersist, so its own slots follow GetClassID.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0000010b-0000-0000-C000-000000000046")]
internal partial interface IPersistFile : IPersist
{
    [PreserveSig]
    int IsDirty();

    [PreserveSig]
    int Load(string pszFileName, uint dwMode);

    [PreserveSig]
    int Save(string? pszFileName, int fRemember);

    [PreserveSig]
    int SaveCompleted(string? pszFileName);

    /// <summary>Returns a CoTaskMem string that the caller frees.</summary>
    [PreserveSig]
    int GetCurFile(out nint ppszFileName);
}

/// <summary>IShellLinkW: all 18 slots after IUnknown, in shobjidl_core.h order.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal unsafe partial interface IShellLinkW
{
    [PreserveSig]
    int GetPath(char* pszFile, int cch, nint pfd, uint fFlags);

    [PreserveSig]
    int GetIDList(out nint ppidl);

    [PreserveSig]
    int SetIDList(nint pidl);

    [PreserveSig]
    int GetDescription(char* pszName, int cch);

    [PreserveSig]
    int SetDescription(string pszName);

    [PreserveSig]
    int GetWorkingDirectory(char* pszDir, int cch);

    [PreserveSig]
    int SetWorkingDirectory(string pszDir);

    [PreserveSig]
    int GetArguments(char* pszArgs, int cch);

    [PreserveSig]
    int SetArguments(string pszArgs);

    [PreserveSig]
    int GetHotkey(out ushort pwHotkey);

    [PreserveSig]
    int SetHotkey(ushort wHotkey);

    [PreserveSig]
    int GetShowCmd(out int piShowCmd);

    [PreserveSig]
    int SetShowCmd(int iShowCmd);

    [PreserveSig]
    int GetIconLocation(char* pszIconPath, int cch, out int piIcon);

    [PreserveSig]
    int SetIconLocation(string pszIconPath, int iIcon);

    [PreserveSig]
    int SetRelativePath(string pszPathRel, uint dwReserved);

    [PreserveSig]
    int Resolve(nint hwnd, uint fFlags);

    [PreserveSig]
    int SetPath(string pszFile);
}

/// <summary>ole32 entry points used by the shortcut service.</summary>
internal static partial class ShellLinkNative
{
    public static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    public static readonly Guid IidShellLinkW = new("000214F9-0000-0000-C000-000000000046");

    public const uint ClsctxInprocServer = 0x1;
    public const uint CoinitMultithreaded = 0x0;
    public const int SOk = 0;
    public const int SFalse = 1;
    public const int RpcEChangedMode = unchecked((int)0x80010106);

    /// <summary>GetPath flag: return the path as stored, without expanding environment strings.</summary>
    public const uint SlgpRawPath = 0x4;

    public const uint StgmRead = 0x0;
    public const int SwShowNormal = 1;

    /// <summary>
    /// Creates the Shell Link object. The unique-instance marshaller returns an RCW that is not cached, so
    /// <see cref="ComObject.FinalRelease"/> releases it deterministically.
    /// </summary>
    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(
        in Guid rclsid,
        nint pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IShellLinkW>))] out IShellLinkW? ppv);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint pv);
}
