<p>
  <img src=".github/images/logo.svg" width="112" alt="Blender Launcher logo">
</p>

<h1>Blender Launcher</h1>

Blender Launcher keeps stable, LTS, daily and experimental builds of [Blender](https://www.blender.org/) side by side, plus [Bforartists](https://www.bforartists.de/) and [UPBGE](https://upbge.org/) if you want them. It tells you when new builds come out, installs them with one click and opens each `.blend` file with the version that made it. Try the newest features without risking the release your projects depend on.

## Highlights

**Find and install builds**
- Stable and LTS [releases](https://download.blender.org/release/), [daily builds](https://builder.blender.org/download/daily/) and [experimental and patch builds](https://builder.blender.org/download/branches/)
- Optional forks: Bforartists, and UPBGE stable and weekly
- Automatic checks for new builds, with notifications
- One-click download and install. **Replace on new build** keeps a branch up to date for you.

**Launch and organise**
- A **Default** build for the global hotkey, the tray icon and `.blend` files that no other build matches
- `.blend` files open with a matching build once the launcher is registered as their handler
- Favourites, filtering by version, branch or commit, and disk usage at a glance
- Running builds are marked, with a count of open instances
- Startup arguments, per-build portable config and template installation

**Fits into Windows**
- Minimise to the tray, or start minimised
- Proxy support and custom TLS certificates
- A command line for scripts and shortcuts
- A dark theme with a choice of accent colours and a compact layout
- Updates itself, keeping your settings and library
- Imports your Blender Launcher V2 settings and can share its library

## Getting started

1. **Download** one of these from the [latest release](https://github.com/matty/blender-launcher/releases/latest):

   | File | What it is |
   |---|---|
   | `Blender_Launcher_<version>_Windows_x64_Setup.exe` | Installer. Installs for your user only (no administrator rights), adds Start menu and Desktop shortcuts. |
   | `Blender_Launcher_<version>_Windows_x64_Portable.zip` | Portable copy. Extract it to a folder you can write to and run `Blender Launcher.exe`. |

2. **Run it.** If Windows SmartScreen warns you, choose **More info › Run anyway** (the app isn't code-signed yet).
3. **Set up.** Pick a library folder (builds are large, so choose a drive with space), the channels you want, and whether to add shortcuts and open `.blend` files with the launcher.
4. **Download a build** from the Download list and press **Launch**.

Requires Windows 10 (version 1809 or later) or Windows 11, 64-bit.

## Command line

```text
"Blender Launcher.exe" [options] [command]

Options:   -h, --help   -d, -debug   -set-library-folder PATH   -force-first-time   --offline   --instanced
Commands:  FILE                       open a .blend with a matching build
           launch [-f FILE | -ol] [-v QUERY] [-c] [-- ARGS...]
           register | unregister      add or remove the launcher in the .blend "Open with" list
           uninstall [-q]             remove registrations, shortcuts and data (never the library)
           update [TAG]               update the launcher to the newest release
```

Version queries pick a build: `4.2.^` is the newest 4.2, `^.^.^-lts` the newest LTS and `^.^.*-daily@^` the most recent daily. `launch -c` asks which build to use in the console, waits for Blender and exits with its exit code. Run `"Blender Launcher.exe" --help` for the full list.

## Uninstalling

Remove it from Settings › Apps › Installed apps in Windows, or run `"Blender Launcher.exe" uninstall`. This removes the file association, startup entry, shortcuts, stored secrets and settings. **Your library and the Blender builds in it are never deleted.**

## Building from source

You need the .NET 10 SDK (see `global.json`) and, for the native build, the Visual Studio 2022 or later C++ build tools ("Desktop development with C++").

```powershell
dotnet build
dotnet test
./scripts/publish.ps1                       # the native exe, in artifacts/publish
./scripts/self-test.ps1 -Offline            # runs the published exe's self-test
```

## Credits

Blender Launcher is an independent reimplementation. Its design and behaviour come from:

- [Blender Launcher V2](https://github.com/Victor-IX/Blender-Launcher-V2) by [Victor-IX](https://github.com/Victor-IX) and contributors
- [Blender Launcher](https://github.com/DotBow/Blender-Launcher) by [DotBow](https://github.com/DotBow), the original project, which followed DotBow's [Blender Version Manager](https://github.com/DotBow/Blender-Version-Manager)

Third-party components and fonts are listed with their licences in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) (also in Settings › About).

Not affiliated with the Blender Foundation. Blender is a registered trademark of the Blender Foundation.

## Licence

[GPL-3.0](LICENSE). Every release links to the source it was built from.
