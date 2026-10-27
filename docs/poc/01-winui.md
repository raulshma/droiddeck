# POC-01: WinUI 3 shell on .NET 10 and Windows App SDK 2.5.x

**Status:** Partial — development-machine build, launch, and portable publish verified;
clean-machine and manual scaling checks are still open.

## Environment

- .NET SDK 10.0.403 (`global.json`, `rollForward: latestFeature`)
- Windows App SDK 2.5.1
- Target `net10.0-windows10.0.26100.0`, minimum `10.0.17763.0`, x64
- Unpackaged (`WindowsPackageType=None`) with `WindowsAppSDKSelfContained=true`

## Evidence

- Debug and Release builds succeed with zero warnings (warnings are errors).
- The portable self-contained publish completes with the documented command.
- The shell implements the custom title bar, `NavigationView`, theme switching,
  unknown-page fallback, single-instance activation, DI, structured logging, and
  window placement persistence.
- The opt-in UI automation test launches the real shell against FakeAdb, navigates,
  hotplugs a fake device, and closes cleanly with no orphan processes or temp files.
- A graceful close now saves window width, height, position, and maximized state
  before services are disposed; the UI test asserts the saved settings.

## Not yet verified

- Windows Sandbox / clean VM launch (POC-07).
- Mica fallback and title-bar drag/maximize/restore at 100% and 150% scaling by hand.
- Cold start p50/p95 against the 2 s / 3 s budget. No startup trace has been captured.

## Publish command

```powershell
dotnet publish src/DroidDeck.App/DroidDeck.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:WindowsPackageType=None -o artifacts/DroidDeck-portable
```

The extracted output is about 229 MB, inside the 250 MB budget. Zip size and
clean-machine behavior are measured by the POC-07 checklist.
