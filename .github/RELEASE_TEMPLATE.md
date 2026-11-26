## Download

| Platform | File |
|---|---|
| **Windows 10/11** (most PCs) | `CtrlLink-Windows-x64-Setup.exe` |
| **Windows on Arm** (Surface Pro X, Snapdragon laptops) | `CtrlLink-Windows-arm64-Setup.exe` |
| **Windows fallback** (if the installer is blocked) | `CtrlLink-Windows-BuildKit.zip`: build and install on your PC with one click, see below |
| **macOS 14+** (Apple silicon) | `CtrlLink-macOS.zip` |
| **Android 8.0+** | `CtrlLink-Android.apk` |

## Install

- **Windows:** run the installer. No admin rights needed.
- **macOS:** unzip and move **Ctrl Link** to Applications.
- **Android:** open the APK on your phone and allow installing from this source when asked.

Then open Ctrl Link on two devices on the same Wi-Fi and pair them with the QR code or 6-digit code.

## Security warnings on first launch

Windows and macOS will warn you the first time you open Ctrl Link. **This is expected.** Ctrl Link is a free, open-source app and the builds are not yet signed with a paid Microsoft code-signing certificate or notarized by Apple, so the operating system can't confirm who made them. The warning is about the missing signature, not about anything the app does. The full source code is in this repository and the release builds are made by GitHub Actions straight from it.

**Windows — "Windows protected your PC" (Microsoft Defender SmartScreen)**
1. Click **More info**.
2. Check that the app is `CtrlLink-Windows-…-Setup.exe`, then click **Run anyway**.
3. If your browser flags the download, choose **Keep** (Edge: **⋯ → Keep → Show more → Keep anyway**).
4. When Windows Firewall asks, allow Ctrl Link on **Private networks** so your other devices can reach it.

**Installer still won't run? Build it on your PC instead (one click)**

Some PCs block unsigned installers completely (company policy, Smart App Control, antivirus). Use the **Windows build kit** instead: download `CtrlLink-Windows-BuildKit.zip` (about 380 MB) from the release. It holds the source, the .NET SDK and every library, so nothing else is downloaded and no admin rights are needed. Because the app is built on your own PC, Windows does not block it.

1. Right-click the zip → **Properties** → tick **Unblock** → **OK**.
2. Extract it to a short path, for example `C:\CtrlLink-build`.
3. Double-click **`build.bat`**. It builds Ctrl Link (a minute or two), installs it to `%LOCALAPPDATA%\Programs\Ctrl Link`, adds Start menu and desktop shortcuts, and starts it.

To uninstall: **Settings → Apps → Installed apps → Ctrl Link → Uninstall** (or run `Uninstall Ctrl Link.cmd` in the install folder). Your pairings in `%APPDATA%\Ctrl Link` are kept; delete that folder too for a clean removal. The build kit is x64 only.

**macOS — "Apple could not verify "Ctrl Link" is free of malware"**
1. Unzip and move **Ctrl Link** to **Applications**, then open it once. When the warning appears, click **Done** (not *Move to Trash*).
2. Open **System Settings → Privacy & Security**, scroll down to the message about Ctrl Link, and click **Open Anyway**. Confirm with your password or Touch ID.
3. Open Ctrl Link again and click **Open**. You only do this once.

On macOS 14 you can instead right-click the app → **Open** → **Open**.

**Make sure your download is genuine**
- Download only from this repository's Releases page.
- Compare the file with `SHA256SUMS.txt` from the same release:
  - macOS: `shasum -a 256 CtrlLink-macOS.zip`
  - Windows (PowerShell): `Get-FileHash .\CtrlLink-Windows-x64-Setup.exe`
  The result must match the line for that file.

These warnings go away once the builds are code-signed (Windows) and notarized (macOS).

See CHANGELOG.md in the repository for what changed.

— Built by **Focotik agency**
