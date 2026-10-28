# DroidDeck

A local Windows control deck for Android devices: live ADB device tracking, toolchain
diagnostics, settings, and an extensible plugin host. DroidDeck never uploads data;
diagnostics reports are generated locally and redacted.

## Requirements

- Windows 11 (or Windows 10 1809+)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) 10.0.403 or newer
- Android SDK platform-tools (`adb`) for real devices — optional for tests

## Build and run

```powershell
dotnet build DroidDeck.slnx
dotnet run --project src/DroidDeck.App/DroidDeck.App.csproj
```

The solution treats warnings as errors and runs analyzers on every build.

## Test

```powershell
dotnet test DroidDeck.slnx --filter "Category!=UI"
```

This covers Core, ADB, Screen, Projects, Plugins, architecture rules, and the fake
ADB command-line and socket fixtures. UI automation tests launch the WinUI shell
and need an interactive desktop; enable them explicitly:

```powershell
$env:DROIDDECK_UI_TESTS = "1"
dotnet test tests/DroidDeck.UiAutomation.Tests/DroidDeck.UiAutomation.Tests.csproj
```

The UI harness starts `FakeAdb` from `tests/DroidDeck.Testing.FakeAdb`, seeds a
temporary data directory, and drives the app through FlaUI. It verifies hotplug,
device state, window placement persistence, and the Settings toolchain doctor. No
physical device is required and processes/temp files are cleaned up afterwards.

## Toolchain doctor

Settings lists every detected tool with its version, path, detection source
(automatic or override), and fix guidance. Each tool accepts an explicit path
override — Android SDK, adb, aapt2, emulator, JDK folder, Gradle, Flutter, Node.js,
scrcpy, and Unity — and an empty field falls back to auto-detection. “Copy fix
guidance” copies the missing tool advice so it can be pasted into a terminal or
issue. Missing tools never block the shell or device refresh.

## Milestone 0 verification

- `docs/poc/01-winui.md` — shell build, launch, and portable publish
- `docs/poc/02-adb.md` — socket tracking, fallback, and metadata
- `docs/poc/07-portable.md` — clean-machine checklist
- `docs/adr/0001-unpackaged-self-contained-winui.md` — portable shell decision

## Portable publish

```powershell
dotnet publish src/DroidDeck.App/DroidDeck.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:WindowsPackageType=None -o artifacts/DroidDeck-portable
```

The output is an unpackaged, self-contained folder: run `DroidDeck.exe` directly.
Add a `portable.marker` file next to the executable to store settings and logs in a
`data` folder beside the app instead of `%LOCALAPPDATA%\DroidDeck`.

## Layout

- `src/DroidDeck.App` — WinUI 3 shell and view models (highest layer)
- `src/DroidDeck.Adb` — ADB socket tracker, CLI fallback, device metadata
- `src/DroidDeck.Projects`, `src/DroidDeck.Screen` — project scanning and runs, scrcpy mirroring and recording
- `src/DroidDeck.Plugins`, `src/DroidDeck.Plugin.Abstractions` — plugin host and contracts
- `src/DroidDeck.Plugin.UI` — scaffold for plugin-supplied UI in a later milestone
- `src/DroidDeck.Core` — configuration, process runner, toolchain, diagnostics
- `tests/` — unit, architecture, fake ADB, and UI automation coverage

Dependency direction is enforced by `DroidDeck.Architecture.Tests`: lower layers
never reference the shell or UI projects, and plugin contracts stay on plain .NET.

## CI

GitHub Actions builds Debug and Release on Windows, runs the non-UI test suite in
both configurations, and uploads the portable `win-x64` artifact.
