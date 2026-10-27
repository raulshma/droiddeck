# POC-07: portable publish on a clean machine

**Status:** Pending — the portable artifact is produced locally; it has not yet been
run in Windows Sandbox or a clean VM.

## Local verification

- `.github/workflows/ci.yml` publishes the self-contained win-x64 folder and uploads
  it as a CI artifact.
- A local Release publish completes and produces `DroidDeck.exe` in
  `artifacts/DroidDeck-portable`.
- Extracted size is about 229 MB (budget: under 250 MB).
- The app supports `%LOCALAPPDATA%\DroidDeck`, portable `data` beside the executable
  via `portable.marker`, and explicit `--data-dir` overrides.
- The toolchain doctor explains missing Android SDK/adb entries without crashing.

## Clean-machine checklist

Run this on Windows Sandbox or a clean Windows 11 VM:

1. Copy the published folder into the VM and launch `DroidDeck.exe`.
2. Confirm no .NET, Windows App SDK, or Visual C++ runtime prompt appears.
3. Confirm data is written under `%LOCALAPPDATA%\DroidDeck` by default.
4. Open Settings and run the toolchain doctor; confirm missing SDK/adb guidance is
   readable and the app stays responsive.
5. Add `portable.marker` beside `DroidDeck.exe`; relaunch and confirm a local `data`
   folder is used instead.
6. Connect a device if USB passthrough exists; otherwise verify the no-device state.
7. Close the app and confirm no `DroidDeck` or `adb` child process remains.
8. Delete the folder and confirm no administrator rights were required.

## Open items

- Clean-machine result and startup/shutdown timings.
- Zip size (budget: under 120 MB).
- The toolchain doctor detects scrcpy when it is installed, but scrcpy is not bundled
  and screen control lands with screen support in M2.
