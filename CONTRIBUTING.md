# Contributing

Thanks for helping with Ctrl Link.

## Ground rules

- **Protocol first.** Anything that changes bytes on the wire starts in `Codebase/protocol/PROTOCOL.md`. Update the spec, regenerate `test-vectors.json` (`node gen-vectors.mjs`), then update Mac, Android and Windows so all three keep passing the vector tests.
- **Keep the three apps in step.** A user-facing feature should land on every platform, or the PR should say which platforms follow later.
- **Design tokens** live in `Codebase/design/DESIGN.md`. Use them instead of new colors or sizes.
- **Never commit** signing keys, keystores, `local.properties` or build output — `.gitignore` covers them.

## Workflow

1. Fork and create a branch: `feature/<short-name>` or `fix/<short-name>`.
2. Build and run the tests for every platform you touched (see [docs/BUILDING.md](docs/BUILDING.md)).
3. Open a pull request using the template; include screenshots for UI changes.

## Tests to run

| Touched | Run |
|---|---|
| `protocol/` | `cd Codebase/protocol/test-peer && node --test` + every app's vector tests |
| `Mac/` | `cd Codebase/Mac && swift test` |
| `Android/` | `cd Codebase/Android && ./gradlew testDebugUnitTest` |
| `Windows/` | `dotnet test Codebase/Windows/tests/CtrlLink.Core.Tests` |

CI runs all of these on every pull request.
