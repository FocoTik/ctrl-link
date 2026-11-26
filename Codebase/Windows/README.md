# Ctrl Link for Windows

Tray app for Windows 10/11 (x64 and Arm64). Implements `../protocol/PROTOCOL.md` like the Mac and Android apps.
It is built entirely on the Mac: C# / .NET 10, Avalonia UI, packaged into a Windows installer with Velopack. Nothing is uploaded anywhere.

| Folder | What |
|---|---|
| `src/CtrlLink.Core` | Protocol, crypto, pairing, sessions, transfers, mDNS. Cross-platform, public API in `Api.cs` |
| `src/CtrlLink.App` | Tray icon, flyout UI, clipboard hook, notifications (Windows-only parts run only on Windows) |
| `tests/CtrlLink.Core.Tests` | Test vectors, in-process pairing/transfer, interop with `protocol/test-peer`, mDNS against macOS `dns-sd` |
| `scripts/build-installer.sh` | Builds `dist/CtrlLink-<version>-Setup-x64.exe` and `-arm64.exe`, unsigned for now |

Toolchain: .NET SDK in `~/.dotnet`, packages in `Windows/.packages` (see `../../docs/BUILDING.md`).

## Build

```sh
Windows/scripts/build-installer.sh     # → Windows/dist/*.exe + SHA256SUMS.txt
```

Tests:

```sh
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
dotnet test Windows/tests/CtrlLink.Core.Tests
```

## Install on the PC

1. Copy `CtrlLink-<version>-Setup-x64.exe` (Arm64 PCs: `-arm64.exe`) to the PC and run it. It installs for the current user
   into `%LocalAppData%\CtrlLink` with no admin prompt, and adds Start menu and desktop shortcuts. Remove it from Settings → Apps.
2. The installer is not signed yet, so Windows warns the first time. If SmartScreen says "Windows protected your PC",
   choose **More info → Run anyway**. If the browser flags the download, keep it (Edge: … → Keep → Show more → Keep anyway).
   Check the file against `SHA256SUMS.txt` (`certutil -hashfile CtrlLink-<version>-Setup-x64.exe SHA256`) if you want to be sure it's untouched.
3. On first launch Windows Firewall asks to allow Ctrl Link on **private networks**. Allow it, otherwise other devices can't reach the PC.
   The network must be set to Private (Settings → Network → your Wi-Fi → Private network).

## Or build and install it on the PC (fallback when the installer is blocked)

```sh
Windows/scripts/make-build-zip.sh      # → Windows/dist/CtrlLink-build.zip (~380 MB), released as CtrlLink-Windows-BuildKit.zip
```

The zip holds the source, the .NET SDK for Windows x64 and every library, so nothing is downloaded on the PC and no admin
rights are needed. On the PC: unblock the zip (Properties → Unblock), extract it to a short path like `C:\CtrlLink-build`, and
double-click `build.bat`. It builds Ctrl Link, installs it to `%LOCALAPPDATA%\Programs\Ctrl Link`, adds Start menu and desktop
shortcuts, registers it in Settings → Apps, and starts it. Because it is built locally, SmartScreen does not block it (x64 only).
Uninstall from Settings → Apps, or run `Uninstall Ctrl Link.cmd` in the install folder; pairings in `%APPDATA%\Ctrl Link` are kept.
The kit files live in `scripts/buildkit/` (`build.bat`, `uninstall.cmd`, `README.txt`).

Data: `%APPDATA%\Ctrl Link\state.json` (pairing keys encrypted with Windows DPAPI for your user), `history.json` (History list: 14 days,
at most 300 clips, text capped at 32 KB; Settings → Clear history deletes it) and `app.json` (app-only settings). Received files: `Downloads\Ctrl Link`.
Icons: `src/CtrlLink.App/Assets/make-icon.py` rebuilds `icon.ico` and the tray icons (Python stdlib + `sips`).
