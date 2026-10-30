# DroidDeck

A local Windows control deck for Android devices: live ADB device tracking, toolchain
diagnostics, settings, and an extensible plugin host. DroidDeck never uploads data;
diagnostics reports are generated locally and redacted.

## Documentation

- [User guide](docs/user-guide.md)
- [Settings reference](docs/settings.md)
- [Plugin SDK](docs/plugin-sdk/index.md)
- [Catalog schema](docs/catalog.md)
- [Troubleshooting](docs/troubleshooting.md)
- [Privacy](docs/privacy.md) and [security](docs/security.md)
- [Licensing](docs/licensing.md)

The same pages build as a GitHub Pages site from `docs/` (see
`.github/workflows/pages.yml`).

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
dotnet test DroidDeck.slnx --filter "Category!=UI&Category!=Template"
```

This covers Core, ADB, Screen, Projects, Plugins, architecture rules, and the fake
ADB command-line and socket fixtures. Template tests install the SDK packages and
templates into a temporary feed, generate a plugin with each template, and build
and load it; they are slower and run separately:

```powershell
dotnet test tests/DroidDeck.Plugins.Tests/DroidDeck.Plugins.Tests.csproj --filter "Category=Template"
```

UI automation tests launch the WinUI shell and need an interactive desktop;
enable them explicitly:

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
override — Android SDK, adb, aapt2, emulator, JDK folder, Gradle, .NET SDK, Flutter,
Node.js, scrcpy, and Unity — and an empty field falls back to auto-detection. “Copy fix
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
- `src/DroidDeck.Plugin.UI` — host-approved component kit and renderers for plugin UI
- `templates/DroidDeck.Plugin.Templates` — `dotnet new` templates for plugin projects
- `tools/DroidDeck.PluginPack` — `droiddeck-plugin-pack`, which writes `.ddplugin` archives
- `src/DroidDeck.Core` — configuration, process runner, toolchain, diagnostics
- `tests/` — unit, architecture, fake ADB, and UI automation coverage

Dependency direction is enforced by `DroidDeck.Architecture.Tests`: lower layers
never reference the shell or UI projects, and plugin contracts stay on plain .NET.

## CI

GitHub Actions builds Debug and Release on Windows, runs the non-UI unit suite
in both configurations, builds and loads a plugin from every template, packs the
plugin SDK packages and packaging tool, and uploads the portable `win-x64`
artifact. A separate workflow publishes the documentation to GitHub Pages.

## License

DroidDeck is released under the MIT License. See [LICENSE](LICENSE) and
[third-party notices](Licenses/THIRD-PARTY-NOTICES.md). Android is a trademark of
Google LLC; DroidDeck is not affiliated with Google.
