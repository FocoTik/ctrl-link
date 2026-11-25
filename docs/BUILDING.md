# Building Ctrl Link

All commands run from `Codebase/`. Nothing needs `sudo`.

## Toolchains

| For | Needs |
|---|---|
| Mac app | macOS 14+, Xcode or just the Command Line Tools (Swift 5.9+) |
| Android app | JDK 21, Android SDK (platform 35, build-tools 35.0.0). Gradle comes from the wrapper |
| Windows app | .NET SDK 10 (macOS, Linux or Windows). Installer packaging uses the Velopack `vpk` local tool |
| Protocol peer / interop tests | Node.js 24+ |

Optional helper: `tools/install-android-toolchain.sh` installs JDK 21, Gradle and the Android SDK into your home folder without touching your shell profile.
If you already use Android Studio, point Gradle at your SDK with `Android/local.properties` (`sdk.dir=/path/to/sdk`).

## Mac

```bash
cd Mac
./scripts/build-app.sh                 # → build/Ctrl Link.app (ad-hoc signed)
open "build/Ctrl Link.app"
swift test                             # unit + vector tests
CTRLLINK_INTEROP=1 swift test          # also runs against the Node reference peer
```

The package also opens directly in Xcode (`open Package.swift`).

## Android

```bash
cd Android
./gradlew assembleDebug                # → app/build/outputs/apk/debug/app-debug.apk
./gradlew testDebugUnitTest
```

### Copy from any app

Android 10+ only lets the foreground app read the clipboard. Ctrl Link sends what you copied when you open it, via Share → Ctrl Link, or the Quick Settings tile.
To sync copies from any app automatically, plug the phone in (USB debugging on), run once and tap **Allow** on the phone:

```bash
./tools/android-enable-copy-sync.sh
```

## Windows

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH   # if the SDK isn't on PATH
dotnet test Windows/tests/CtrlLink.Core.Tests
Windows/scripts/build-installer.sh     # → Windows/dist/*.exe + SHA256SUMS.txt
```

- NuGet packages are restored into `Windows/.packages` (gitignored), not `~/.nuget`.
- Installers are Authenticode-signed by `scripts/sign.sh` only when a local `Windows/.signing/` keystore exists. It is never committed; without it the installers are unsigned.
- `Windows/scripts/make-build-zip.sh` produces an offline build kit (source + .NET SDK) for building directly on a PC. See `Windows/README.md`.
- Headless screenshots of every screen: `dotnet run --project Windows/src/CtrlLink.App -- --screenshots <dir>`.

## Protocol reference peer

```bash
cd protocol/test-peer
npm install
node --test                            # vectors, peer and mDNS tests
node gen-vectors.mjs                   # regenerate ../test-vectors.json
```

`PROTOCOL.md` is the source of truth: change it first, regenerate vectors, then update the three apps.

## Releasing

1. Bump the version: `Windows/Directory.Build.props` (`<Version>`), `Android/app/build.gradle.kts` (`versionCode`, `versionName`), and add a `CHANGELOG.md` entry.
2. Commit, then tag and push:

   ```bash
   git tag v1.1.0 && git push origin v1.1.0
   ```

3. `.github/workflows/release.yml` builds the Mac zip, Android APK and both Windows installers, writes `SHA256SUMS.txt`, and publishes a GitHub Release using `.github/RELEASE_TEMPLATE.md`.

Asset names have no version in them (`CtrlLink-Windows-x64-Setup.exe`, `CtrlLink-Windows-arm64-Setup.exe`, `CtrlLink-macOS.zip`, `CtrlLink-Android.apk`), so the README's `releases/latest/download/...` links always point at the newest build.

CI builds are unsigned: the Android APK is a debug build and the Windows installers show SmartScreen's "Unknown publisher" warning. To ship signed builds, sign locally and upload to the release, or add the keys as repository secrets and extend the workflow.
