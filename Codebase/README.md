# Codebase

| Folder | What |
|---|---|
| `protocol/` | `PROTOCOL.md` (the wire contract), `test-vectors.json`, `test-peer/` (Node reference peer) |
| `Mac/` | Swift / SwiftUI menu-bar app (SwiftPM) |
| `Android/` | Kotlin / Jetpack Compose app (Gradle) |
| `Windows/` | C# / .NET 10 / Avalonia tray app and installer scripts — see [`Windows/README.md`](Windows/README.md) |
| `design/` | `DESIGN.md` (UI direction and tokens), logo, Newsreader font (OFL) |
| `tools/` | Android toolchain installer and the copy-from-any-app helper |

The apps reference `../protocol` and `../design` by relative path, so keep these folders side by side.
Build and test commands: [`../docs/BUILDING.md`](../docs/BUILDING.md).
