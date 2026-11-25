# Changelog

Ctrl Link is developed by Focotik agency. All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.0.0] — 2026-11-25

### Added
- macOS menu-bar app (Swift / SwiftUI).
- Android app (Kotlin / Jetpack Compose) with Share target, Quick Settings tile and boot start.
- Windows tray app (C# / .NET 10 / Avalonia) with x64 and Arm64 installers.
- Ctrl Link Protocol v1: mDNS discovery, QR / 6-digit pairing, encrypted sessions, clipboard sync and file transfer.
- Shared test vectors and a Node.js reference peer used by every platform's tests.
- Android: incoming files over the auto-accept limit can be accepted or declined from the app, not only from the notification.

### Fixed
- Windows: buttons respond reliably in windows shown without focus (tray flyout, toasts).
