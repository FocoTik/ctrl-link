<p align="center">
  <img src="docs/images/app-icon.png" width="112" alt="Ctrl Link icon">
</p>

<h1 align="center">Ctrl Link</h1>

<p align="center">
  Copy on one device, paste on another.<br>
  Text, links, images, videos and any file — over your local network, end-to-end encrypted, no cloud.
</p>

<p align="center">
  <img alt="macOS 14+" src="https://img.shields.io/badge/macOS-14%2B-000?logo=apple">
  <img alt="Android 8+" src="https://img.shields.io/badge/Android-8.0%2B-000?logo=android">
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-000?logo=windows">
  <img alt="Protocol v1" src="https://img.shields.io/badge/protocol-v1-000">
  <img alt="MIT License" src="https://img.shields.io/badge/license-MIT-000">
</p>

<p align="center">
  <a href="../../releases/latest/download/CtrlLink-Windows-x64-Setup.exe"><img alt="Download for Windows" src="https://img.shields.io/badge/Download-Windows-0078D4?style=for-the-badge&logo=windows&logoColor=white"></a>
  <a href="../../releases/latest/download/CtrlLink-macOS.zip"><img alt="Download for macOS" src="https://img.shields.io/badge/Download-macOS-111111?style=for-the-badge&logo=apple&logoColor=white"></a>
  <a href="../../releases/latest/download/CtrlLink-Android.apk"><img alt="Download for Android" src="https://img.shields.io/badge/Download-Android-3DDC84?style=for-the-badge&logo=android&logoColor=white"></a>
</p>

<p align="center">
  <img src="docs/images/screenshots/dark-devices-two.png" width="250" alt="Devices">
  <img src="docs/images/screenshots/dark-pair-host.png" width="250" alt="Pairing">
  <img src="docs/images/screenshots/dark-transfer.png" width="250" alt="File transfer">
</p>

## Download

| Platform | Download | Notes |
|---|---|---|
| **Windows 10/11** | [Installer (x64)](../../releases/latest/download/CtrlLink-Windows-x64-Setup.exe) · [Arm64](../../releases/latest/download/CtrlLink-Windows-arm64-Setup.exe) | No admin needed. If SmartScreen warns: **More info → Run anyway**. Allow Ctrl Link on **private networks** |
| **macOS 14+** (Apple silicon) | [CtrlLink-macOS.zip](../../releases/latest/download/CtrlLink-macOS.zip) | Unzip, move to Applications, right-click → **Open** the first time |
| **Android 8.0+** | [CtrlLink-Android.apk](../../releases/latest/download/CtrlLink-Android.apk) | Open on the phone and allow installing from this source |

All versions and checksums: **[Releases](../../releases)**. Install on two devices on the same Wi-Fi, then pair with the QR code or 6-digit code.

## Features

- **Clipboard sync** — copy text or a link on one device, it's on the clipboard of your other devices.
- **Files of any size** — images, videos and documents stream device to device with live progress.
- **Pair once** — scan a QR code or type a 6-digit code. Paired devices find each other on the network (mDNS) and reconnect on their own.
- **Private by design** — X25519 key exchange, AES-256-GCM sessions, nothing leaves your network. No accounts, no servers.
- **History** — recent clips, filterable by type, kept locally and clearable any time.
- **Native on every platform** — a menu-bar app on Mac, a tray app on Windows, a Compose app (with Share target and Quick Settings tile) on Android.

## Platforms

| Platform | Stack | Folder |
|---|---|---|
| macOS 14+ | Swift / SwiftUI, Swift Package Manager | [`Codebase/Mac`](Codebase/Mac) |
| Android 8.0+ (API 26) | Kotlin / Jetpack Compose, Gradle | [`Codebase/Android`](Codebase/Android) |
| Windows 10/11 (x64, Arm64) | C# / .NET 10, Avalonia, Velopack installer | [`Codebase/Windows`](Codebase/Windows) |
| Protocol | Spec, test vectors, Node.js reference peer | [`Codebase/protocol`](Codebase/protocol) |

All three apps implement the same wire contract, [`PROTOCOL.md`](Codebase/protocol/PROTOCOL.md), and are tested byte-for-byte against shared [test vectors](Codebase/protocol/test-vectors.json) and against the Node reference peer.

## Repository layout

```
.
├── Codebase/
│   ├── Mac/          macOS menu-bar app (SwiftPM)
│   ├── Android/      Android app (Gradle)
│   ├── Windows/      Windows tray app (.NET solution)
│   ├── protocol/     PROTOCOL.md, test-vectors.json, test-peer/ (Node)
│   ├── design/       DESIGN.md, logo, Newsreader font (OFL)
│   └── tools/        Android toolchain + copy-sync helper scripts
├── docs/
│   ├── BUILDING.md   toolchains, build and test commands
│   └── images/       icon, logo, screenshots
└── .github/          CI workflow, issue and PR templates
```

## Build from source

```bash
git clone <this-repo> ctrl-link && cd ctrl-link/Codebase
```

**Mac**

```bash
cd Mac && ./scripts/build-app.sh && open "build/Ctrl Link.app"
```

**Android**

```bash
cd Android && ./gradlew assembleDebug
```

APK: `Android/app/build/outputs/apk/debug/app-debug.apk`

**Windows** (built from macOS or Linux with the .NET 10 SDK)

```bash
Windows/scripts/build-installer.sh
```

Installers: `Windows/dist/CtrlLink-<version>-Setup-x64.exe` and `-arm64.exe`

Full toolchain setup, tests and signing notes: **[docs/BUILDING.md](docs/BUILDING.md)**.

## How it works

1. **Discover** — each app advertises `_ctrllink._tcp` over mDNS on the local network.
2. **Pair** — one device shows a QR code / 6-digit code; the other scans or types it. Both derive a long-term pair key (X25519 + HKDF) and confirm with a short authentication string.
3. **Connect** — paired devices open a TCP session, authenticate with the pair key and switch to AES-256-GCM framed messages.
4. **Sync** — clipboard changes and file offers flow as encrypted frames; large files are chunked and streamed.

Details in [`Codebase/protocol/PROTOCOL.md`](Codebase/protocol/PROTOCOL.md).

## Screenshots

| More devices | History | Settings |
|---|---|---|
| <img src="docs/images/screenshots/dark-devices-four.png" width="240"> | <img src="docs/images/screenshots/dark-history.png" width="240"> | <img src="docs/images/screenshots/dark-settings.png" width="240"> |

## Contributing

Pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). For security issues, please follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## Credits

Display type is [Newsreader](https://fonts.google.com/specimen/Newsreader), licensed under the [SIL Open Font License](Codebase/design/fonts/OFL.txt).

## License

[MIT](LICENSE) © 2026 Focotik agency. The Newsreader font keeps its own [OFL license](Codebase/design/fonts/OFL.txt).
