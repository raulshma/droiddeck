# User guide

DroidDeck is a local Windows control deck for Android devices. It watches adb,
scans projects, runs builds and installs, streams logs, mirrors screens, and hosts
plugins. Everything stays on the computer unless you explicitly use a network
feature described in [Privacy](privacy.md).

## Install and start

Releases are unpackaged, self-contained `win-x64` folders. Unzip the folder and run
`DroidDeck.exe`. No installer or Windows App SDK runtime is required.

By default DroidDeck stores data in `%LocalAppData%\DroidDeck`. Two portable options
move it:

- Create an empty file named `portable.marker` next to `DroidDeck.exe` to store data
  in a `data` folder beside the app.
- Pass `--data-dir <path>` to choose any directory for this launch.

Command-line options:

| Option | Effect |
| --- | --- |
| `--data-dir <path>` | Use a custom data directory for this launch. |
| `--safe-mode` | Disable plugins and experimental features for this launch. |
| `--no-plugins` | Disable plugins for this launch without changing saved settings. |
| `--diagnostics` | Open the Diagnostics page at startup. |
| `--set <path>=<value>` | Override one setting for this session only. |
| `--profile <id>` | Activate a settings profile for this session only. |
| `--version` | Print the version and exit. |
| `--help` | Print the command-line help and exit. |

Settings can also be overridden with `DROIDDECK__<SECTION>__<KEY>` environment
variables, for example `DROIDDECK__LOGGING__LEVEL=debug`.

## First run

The first-run page checks the tools DroidDeck can use, offers to add a project, and
shows the steps for enabling USB debugging on the device. Every step can be skipped
and finished later; all of it is also reachable from **Settings** and **Home**.

The tool check is the same doctor used by **Settings → Tools**. Each tool shows its
version, path, how it was found, and fix guidance when it is missing. Missing tools
never block startup; only the features that need them are disabled.

## Home

**Home** is the cockpit:

- **Quick run** repeats the last project, run configuration, and device with one click.
- **Active sessions** lists running builds, installs, and launches with their state
  and current step.
- **Recent projects** reopens a project page.
- **Devices** shows connected devices and their state.
- **Environment** summarizes toolchain health.
- **Plugin widgets** mount home widgets contributed by plugins.

## Devices

**Devices** tracks USB and network devices through adb and keeps a per-device record
with notes, tags, and a small screenshot preview.

- **Refresh list** re-enumerates devices; **Restart adb** restarts the adb server.
- Selecting a device shows state, model, Android version, API level, battery,
  storage, and connection details.
- **Toolbox** opens device tools; **Mirror** starts scrcpy; **Emulators** and
  **Wireless** open their pages.
- Notes and tags are saved per serial and survive reconnects.

Unauthorized devices show an authorization prompt on the device. Offline devices
usually mean the cable, USB mode, or adb server needs attention.

### Device tools

The toolbox runs against the selected device and shows the exact adb command for
every action:

- **Apps** — list installed and system packages, filter, install an APK, launch,
  force stop, clear data, and uninstall. Destructive actions ask first.
- **Permissions** — inspect runtime permissions and grant or revoke them.
- **Shell** — run any `adb shell` command with history.
- **Ports** — list, add, and remove `adb forward` and `adb reverse` mappings.
- **Files** — browse `/sdcard` and other paths, then pull or push files.
- **Intents** — start an activity with action, data, MIME type, package, component,
  categories, flags, and extras.
- **Broadcasts** — send a broadcast with action, package, component, and extras.
- **Reboot** — reboot, reboot to bootloader, recovery, or sideload.
- **Plugins** — device panels and toolbar actions from plugins.
- **Tools** — performance sampling and the UI inspector for the device.

### Mirror and capture

**Mirror** starts scrcpy docked over the pane or in an external window. Docked
mirrors stop when you leave the page; external windows stay open.

- **Start/Stop**, **Pop out**, **Focus window**, and **Toggle fullscreen** control the
  session.
- **Screenshot** saves a PNG; **Record** saves a video and **Stop recording** ends
  it. **Open file** and **Open folder** reveal the output.
- **Device keys** send Back, Home, Recents, volume, power, wake, and menu.
- **Mirror settings** opens the scrcpy-related settings: size, frame rate, bit rate,
  audio, clipboard sync, stay awake, and the maximum number of simultaneous mirrors
  and recordings.

Screenshots and recordings go to the app data directory unless you configure
`screen.screenshotsDirectory` or `screen.recordingsDirectory`.

### Emulators

**Emulators** lists Android virtual devices and can launch, cold boot, save or load
snapshots, wipe data, and stop a running emulator. Create AVDs in Android Studio's
Device Manager; DroidDeck uses the detected emulator and SDK.

### Wireless adb

**Wireless** pairs Android 11+ devices with a QR code or a pairing code, discovers
mDNS services, and remembers connections. Remembered devices reconnect after adb or
the network restarts when `wireless.autoReconnect` is on.

## Projects

**Projects** supports Gradle, Flutter, React Native, Kotlin Multiplatform, prebuilt
APK, and custom script projects. Detection reads files only; it never invokes
Gradle, Flutter, or Metro.

- Add a project folder or an APK. Repository scans can offer nested projects when
  `projects.scanMonorepos` is on.
- The project page shows application ID, namespace, launcher activity, module,
  Gradle wrapper, AGP/Kotlin versions, capabilities, detection evidence, modules,
  variants, and attached APK files.
- **Manual overrides** for name, application ID, namespace, launcher activity, and
  module survive detection refreshes. Clear a field to drop the override.
- **Run configurations** define the build, install, and launch steps. Built-in
  presets live in the presets directory and can be extended with custom steps.
- **Run target** selects a configuration and device. **Run** builds, installs, and
  launches; **dry run** validates the plan without installing.

## Multi-device runs

**Multi-device run** builds once and installs the artifact on several devices. Each
device gets an independent session, log, and status; a failed install retries
without rebuilding. `run.maxConcurrentDevices` bounds how many devices are mutated
at once, and `run.buildOnce` controls artifact reuse.

## Tests, sessions, history, and activity

- **Tests** runs Gradle, Flutter, Jest, .NET, and custom commands, parses JUnit XML,
  shows suites, cases, stack traces, and attachments, and can rerun failing tests on
  a device.
- **Session** shows the live log and status of a run, including the diagnosis when a
  step fails.
- **History** lists saved sessions. Open one read-only, export it with redaction, or
  delete it. Retention defaults to 14 days and 2 GB.
- **Activity** is the application log for the current launch, with filters and
  level selection.

## Performance and UI inspector

- **Performance** samples launch time, FPS and jank, CPU, memory, and battery for a
  running package, charts them live, and exports samples as CSV.
- **UI inspector** dumps the current hierarchy with `uiautomator`, draws node bounds
  on a screenshot, taps elements, and copies selectors. Commands are shown before
  they run.

Both are hidden unless `features.developerMode` is on.

## Extensions and catalog

**Extensions** lists installed plugins and their health.

- **Open plugins folder** opens the plugin directory. Each plugin is a folder with
  a manifest and its assemblies.
- A newly copied plugin appears as untrusted. Review its files, then choose
  **Trust**. **Enable** and **Disable** take full effect after a restart.
- Plugin tools expose **Logs**, **Settings**, **Update**, **Rollback**, and
  **Uninstall** when the plugin provides them.
- Plugin faults are isolated and reported under plugin status; a faulty plugin does
  not take down the shell.

**Catalog** browses plugin catalogs that you add.

- **Add source** accepts an HTTPS catalog URL or a local file or folder.
- **Refresh** fetches enabled sources. Cached catalogs remain browsable offline.
- Filter by source, tag, or text, and opt into pre-releases per source.
- **Install** or **Update** always shows a trust decision before any file is
  extracted. Packages are downloaded over HTTPS, verified against the catalog's
  SHA-256 hash, installed with the previous version kept for rollback, and audited.

Nothing installs or updates by itself. See [Catalog schema](catalog.md) for how to
publish a source, and the [plugin SDK](plugin-sdk/index.md) for how to build a
plugin.

## Keyboard and command palette

Press **Ctrl+K** to open the command palette; it searches navigation, actions, and
plugin commands. Shortcuts can be remapped in **Settings → Keyboard shortcuts**, and
multi-step chords such as `Ctrl+K Ctrl+C` are supported.

| Shortcut | Command |
| --- | --- |
| `Ctrl+K` | Show command palette |
| `F5` | Run on selected devices |
| `Shift+F5` | Stop running sessions |
| `Ctrl+Shift+M` | Start mirror |
| `Ctrl+Shift+S` | Take screenshot |
| `Ctrl+Shift+R` | Toggle screen recording |
| `Ctrl+B` | Toggle navigation pane |

## Settings and appearance

**Settings** searches every option and groups them into appearance, profiles,
customize, tools, data and diagnostics, notifications, runs, sampling, inspector,
session retention, advanced, keyboard shortcuts, plugin sections, and About. See the
[settings reference](settings.md) for every key, default, and scope.

Appearance supports light, dark, and system themes, custom theme files, accent
overrides (contrast checked), a font family override, comfortable or compact
density, and full or reduced motion.

**Customize** hides navigation, toolbox, home, and stack features you do not use.
Hidden features remain reachable from the command palette unless the feature itself
is disabled.

Profiles store named settings layers. `--profile` activates one for a single launch,
and the Settings page can switch the active profile.

## Diagnostics and About

**Diagnostics** builds a local, redacted report with versions, paths, toolchain
state, device state, recent errors, and plugin health. Copy it to the clipboard or
open the diagnostics folder. Reports never leave the machine unless you share them.

**About** shows the DroidDeck, .NET runtime, Windows App SDK, scrcpy, and Windows
versions; data locations; the diagnostics ID; license and privacy links; and the
manual update check. Manual update checking is off by default and never runs
automatically. When enabled, it queries the GitHub releases API over HTTPS and only
shows a link to the release page; it never downloads or installs an update.

## Troubleshooting

See [Troubleshooting](troubleshooting.md) for device, toolchain, plugin, catalog,
settings-recovery, and performance problems.

Android is a trademark of Google LLC. DroidDeck is not affiliated with Google.
