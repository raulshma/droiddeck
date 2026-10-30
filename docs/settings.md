# Settings reference

Settings describe both preferences and remembered state (window placement, last
project, onboarding progress). Every setting is validated, written atomically, and
kept in a layered configuration so teams can share project settings without
overwriting personal ones.

## Where settings live

| Layer | File | Shared |
| --- | --- | --- |
| Built-in defaults | `defaults.json` inside the app | No |
| Global | `<data>\settings.json` | No |
| Active profile | `<data>\profiles\<id>.json` | No |
| Team project | `<repo>\.droiddeck\project.json` | Yes, commit it |
| Local project | `<data>\projects\<id>.json` | No |
| Run configuration | Stored with the project's run configurations | Per configuration |
| Device | `<data>\devices\<serial>.json` | No |
| Session | `--set`, `--profile`, `DROIDDECK__*` environment variables | No |

Later layers override earlier ones. The app writes a `.bak` copy beside
`settings.json` so an interrupted write or a hand-editing mistake can be recovered.
Settings health in **Settings** detects invalid values, unknown keys, and stale
overrides and offers fixes.

`<data>` is `%LocalAppData%\DroidDeck` by default, the `data` folder beside the
executable in portable mode, or the `--data-dir` path.

## Editing settings

Use **Settings → Import, export, and reset** to edit any layer as JSON, import a
settings file, or export the current layer. A diff is shown before anything is
written, and secrets are never exported or imported.

Hand-editing `settings.json` is supported while DroidDeck is not running. Keep the
JSON valid and use the same paths shown below. Unknown or invalid values are
reported rather than applied, and a corrupt file falls back to defaults with a
recovery notice.

Levels in the table below:

- **Basic** — everyday options shown by default.
- **Advanced** — useful tuning for specific workflows.
- **Expert** — state and diagnostics that most users should not need to change.

## All settings

### Appearance

| Setting | Summary | Default | Type | Level |
| --- | --- | --- | --- | --- |
| `appearance.theme` | Follows the operating system or forces one appearance. | "system" | Enum | Basic |
| `appearance.themeId` | Selects a built-in or custom theme with color tokens, fonts, and log colors. | null | Text | Advanced |
| `appearance.accentColor` | Overrides the theme accent after contrast validation. | null | Text | Advanced |
| `appearance.fontFamily` | Overrides the theme font family. | null | Text | Advanced |
| `appearance.density` | Spacing between rows and cards across the app. | "comfortable" | Enum | Basic |
| `appearance.motion` | Controls animated transitions in the shell. | "full" | Enum | Basic |
| `appearance.language` | Selects the interface language. | "system" | Enum | Basic |

`appearance.theme` accepts `system`, `light`, or `dark`. Theme files placed in the
themes directory appear in the theme picker. Accent colors are rejected when the
resulting contrast is too low. `appearance.language` accepts `system`, `english`,
or `pseudo`; see [accessibility and languages](accessibility.md).

### Tools

| Setting | Summary | Default | Type | Level |
| --- | --- | --- | --- | --- |
| `tools.adbPath` | Overrides the automatically detected adb executable. | null | Path | Advanced |
| `tools.androidSdkPath` | Overrides the Android SDK root used by build tools. | null | Path | Advanced |
| `tools.aapt2Path` | Overrides the aapt2 executable used to read APK manifests. | null | Path | Advanced |
| `tools.emulatorPath` | Overrides the Android emulator executable. | null | Path | Advanced |
| `tools.javaHome` | Overrides the Java runtime directory. | null | Path | Advanced |
| `tools.gradlePath` | Overrides the Gradle wrapper or executable. | null | Path | Advanced |
| `tools.flutterPath` | Overrides the Flutter SDK executable. | null | Path | Advanced |
| `tools.dartPath` | Overrides the Dart executable. | null | Path | Advanced |
| `tools.nodePath` | Overrides the Node.js executable. | null | Path | Advanced |
| `tools.scrcpyPath` | Overrides the scrcpy executable used for screen mirroring. | null | Path | Advanced |
| `tools.unityPath` | Overrides the Unity editor executable. | null | Path | Advanced |
| `tools.dotNetPath` | Overrides the .NET SDK executable. | null | Path | Advanced |

An empty path means automatic detection. The toolchain doctor shows the detected
path, version, detection source, and fix guidance for each tool.

### Logging

| Setting | Summary | Default | Type | Level |
| --- | --- | --- | --- | --- |
| `logging.level` | Minimum severity written to the activity log. | "information" | Enum | Basic |
| `logging.maxUiLines` | How many log lines the activity view keeps in memory. | 20000 | WholeNumber | Advanced |
| `logging.retentionDays` | How long captured sessions are kept before cleanup. | 14 | WholeNumber | Advanced |
| `logging.maxSessionBytes` | Total bytes of captured sessions kept on disk. | 2147483648 | WholeNumber | Advanced |

Levels are `trace`, `debug`, `information`, `warning`, `error`, and `critical`.
Retention cleanup runs at startup and removes the oldest sessions first.

### Diagnostics

| Setting | Summary | Default | Type | Level |
| --- | --- | --- | --- | --- |
| `diagnostics.inMemoryLogEntries` | How many log entries diagnostics retain in memory. | 2000 | WholeNumber | Expert |
| `diagnostics.recentErrorCount` | How many recent errors a diagnostics report includes. | 100 | WholeNumber | Expert |

### Features

| Setting | Summary | Default | Type | Level |
| --- | --- | --- | --- | --- |
| `features.developerMode` | Shows developer tools such as the UI inspector. | false | Boolean | Advanced |
| `features.safeMode` | Starts DroidDeck without third-party plugins. | false | Boolean | Advanced |
| `features.hidden` | Navigation, toolbox, home, and stack features hidden in Customize mode. | [] | List | Advanced |

`features.safeMode` is a saved preference; `--safe-mode` and `--no-plugins` apply
only to one launch. Prefer the command-line switch when diagnosing a plugin
problem so the saved setting does not surprise the next launch.

### Window

| Setting | Summary | Default | Type | Level |
| --- | --- | --- | --- | --- |
| `window.width` | Restored window width in effective pixels. | 1280 | Number | Expert |
| `window.height` | Restored window height in effective pixels. | 800 | Number | Expert |
| `window.x` | Restored horizontal window position. | null | Number | Expert |
| `window.y` | Restored vertical window position. | null | Number | Expert |
| `window.isMaximized` | Whether the main window was maximized. | false | Boolean | Expert |

Placement is validated against the visible desktop before it is restored, so a
window moved off-screen by a monitor change returns to the default position.

### Projects

| Setting | Summary | Default | Type | Level |
| --- | --- | --- | --- | --- |
| `projects.scanDepth` | How many folder levels a monorepo scan visits. | 3 | WholeNumber | Advanced |
| `projects.maxScanFolders` | Maximum number of folders a project scan visits. | 2000 | WholeNumber | Advanced |
| `projects.scanTimeoutSeconds` | Maximum time a project scan may run. | 2 | WholeNumber | Advanced |
| `projects.ignoreFolders` | Folder names that project discovery never scans. | [".git",".gradle",".idea","build","node_modules","dist","out","Pods",".dart_tool",".vs","bin","obj","packages"] | List | Advanced |
| `projects.scanMonorepos` | Whether repository scans offer nested projects. | true | Boolean | Advanced |

### Notifications

| Setting | Summary | Default | Type | Level |
| --- | --- | --- | --- | --- |
| `notifications.enabled` | Master switch for in-app notifications. | true | Boolean | Basic |
| `notifications.runSucceeded` | Notify when a run succeeds. | true | Boolean | Basic |
| `notifications.runFailed` | Notify when a run fails. | true | Boolean | Basic |
| `notifications.runCancelled` | Notify when a run is cancelled. | false | Boolean | Advanced |
| `notifications.crash` | Notify when an app crash is captured. | true | Boolean | Basic |
| `notifications.installFailed` | Notify when an install fails. | true | Boolean | Advanced |

Notifications are in-app only. DroidDeck does not send system notifications or
network messages.

### Home

| Setting | Summary | Default | Type | Level |
| --- | --- | --- | --- | --- |
| `home.lastProjectId` | Project restored by the home cockpit. | null | Text | Expert |
| `home.lastConfigurationId` | Run configuration restored by the home cockpit. | null | Text | Expert |
| `home.lastDeviceSerial` | Device restored by the home cockpit. | null | Text | Expert |

### Onboarding

| Setting | Summary | Default | Type | Level |
| --- | --- | --- | --- | --- |
| `onboarding.completed` | Whether the first-run flow finished. | false | Boolean | Expert |
| `onboarding.step` | The last first-run step reached. | "tools" | Text | Expert |

### Updates

| Setting | Summary | Default | Type | Level |
| --- | --- | --- | --- | --- |
| `updates.manualCheckEnabled` | Allows checking for a newer release from the About page. DroidDeck never checks automatically. | false | Boolean | Basic |

The check is manual and off by default. When enabled, **About → Check for updates**
queries the GitHub releases API over HTTPS with a version user agent, a size limit,
and a timeout. It only compares versions and offers a link to the release page; it
never downloads or installs anything and stores no result beyond the page's
in-memory state.

### Run

| Setting | Summary | Default | Type | Level |
| --- | --- | --- | --- | --- |
| `run.maxConcurrentDevices` | How many devices a multi-device run mutates at once. | 3 | WholeNumber | Advanced |
| `run.buildOnce` | Builds one artifact and reuses it for every device. | true | Boolean | Advanced |

### Screen

| Setting | Summary | Default | Type | Level |
| --- | --- | --- | --- | --- |
| `screen.hostingMode` | Where mirrored devices are hosted: auto, docked, external, or popout. | "auto" | Enum | Advanced |
| `screen.maxMirrors` | The maximum number of mirrored devices running at once. | 3 | WholeNumber | Advanced |
| `screen.maxRecordings` | The maximum number of screen recordings running at once. | 2 | WholeNumber | Advanced |
| `screen.maxConcurrentBuilds` | The maximum number of builds counted by the resource guard. | 1 | WholeNumber | Advanced |
| `screen.warnAtPercent` | Percent of a resource budget at which a warning appears. | 80 | WholeNumber | Advanced |
| `screen.maxSize` | Caps the mirrored video width at this many pixels. | 1024 | WholeNumber | Advanced |
| `screen.maxFps` | Caps the mirrored video frame rate. | 60 | WholeNumber | Advanced |
| `screen.videoBitRate` | Sets the mirrored video bit rate, such as 8M. | "8M" | Text | Advanced |
| `screen.stayAwake` | Keeps the device awake during mirroring. | false | Boolean | Advanced |
| `screen.clipboardSync` | Shares the clipboard between the host and the device. | true | Boolean | Advanced |
| `screen.audio` | Captures device audio while mirroring. | false | Boolean | Advanced |
| `screen.screenshotsDirectory` | Where screenshots are saved, or empty for the default. | null | Path | Advanced |
| `screen.recordingsDirectory` | Where recordings are saved, or empty for the default. | null | Path | Advanced |
| `screen.thumbnailsEnabled` | Captures small device previews for the device list. | true | Boolean | Advanced |
| `screen.thumbnailFps` | Thumbnail captures per second for the device list. | 1 | Number | Advanced |

Mirror options are passed to scrcpy as arguments. Lowering size, frame rate, or bit
rate reduces CPU and USB bandwidth on slower machines.

### Wireless

| Setting | Summary | Default | Type | Level |
| --- | --- | --- | --- | --- |
| `wireless.autoReconnect` | Reconnects remembered wireless devices automatically. | true | Boolean | Advanced |
| `wireless.reconnectDelaySeconds` | How long to wait between wireless reconnect attempts. | 10 | WholeNumber | Advanced |

### Performance

| Setting | Summary | Default | Type | Level |
| --- | --- | --- | --- | --- |
| `performance.sampleIntervalMs` | How often CPU, memory, and frame samples are captured. | 1000 | WholeNumber | Basic |
| `performance.maxSamples` | How many performance samples charts keep. | 60 | WholeNumber | Advanced |

### Inspector

| Setting | Summary | Default | Type | Level |
| --- | --- | --- | --- | --- |
| `inspector.maxDepth` | Maximum node depth shown in the UI hierarchy tree. | 12 | WholeNumber | Advanced |
| `inspector.includeInvisible` | Includes invisible nodes in the UI hierarchy tree. | false | Boolean | Advanced |

### Profile

| Setting | Summary | Default | Type | Level |
| --- | --- | --- | --- | --- |
| `profile.activeId` | The profile whose settings layer is applied. | (unset) | Text | Advanced |

## Profiles

A profile is a named settings layer stored in `profiles/<id>.json`. The active
profile is normal state, so it is saved like any other setting. `--profile <id>`
activates a profile for one launch without changing the saved choice.

Profiles are useful for switching between machines, teams, or workflow presets
without duplicating the global file.

## Reset and recovery

**Settings → Import, export, and reset** can reset a chosen layer with an immediate
undo. If a reset or a bad import makes the app misbehave:

1. Close DroidDeck.
2. Rename `settings.json`; DroidDeck recreates it from defaults and leaves the
   `.bak` file in place.
3. Or launch with `--safe-mode` to bypass plugins while you fix settings.
4. Check **Settings → Settings health** after restarting; it reports stale or
   invalid overrides and offers fixes.

Secrets referenced by settings are stored separately in `secrets.json` protected by
Windows DPAPI and are never exported.
