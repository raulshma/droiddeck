# Troubleshooting

Start with **Diagnostics**: it builds a local, redacted report with versions, paths,
toolchain and device state, recent errors, and plugin health. Error messages include
a diagnostics ID that points at the same information. Copy the report to the
clipboard or open the diagnostics folder from **Diagnostics** or **About**.

If the app starts but misbehaves, relaunch with `--safe-mode` (or `--no-plugins`) to
exclude third-party plugins without changing saved settings.

## Devices

**No devices appear.**

- Confirm USB debugging is on: **Settings → About phone → tap Build number seven
  times**, then **Developer options → USB debugging**.
- Accept the RSA fingerprint prompt on the device.
- Try another cable or USB port and set the USB mode to file transfer.
- Click **Restart adb** on the Devices page, or run `adb kill-server` and
  `adb start-server` in a terminal.
- Check `tools.adbPath`; an override that points at a missing or older adb hides
  devices.

**The device shows as unauthorized.** Unlock the device and approve the prompt.
Choose **Revoke USB debugging authorizations** in Developer options and reconnect if
the prompt does not appear.

**The device is offline.** Replug it, restart adb, or reboot the device. Wireless
devices that went to sleep usually reconnect through **Wireless** or
`wireless.autoReconnect`.

**Metadata or the app list is stale.** Click **Refresh metadata** on the device
panel or **Refresh list** on the Devices page.

## Toolchain

**A tool is missing.** Open **Settings → Tools**. Each row shows the detected path,
version, detection source, and fix guidance. Set an explicit override when the tool
lives in a non-standard place; clear the field to return to auto-detection.

**The wrong Java or Gradle is used.** Set `tools.javaHome` or `tools.gradlePath`.
Gradle builds use the project's wrapper when one exists; check that the wrapper file
is present and executable.

**Flutter, Node, Unity, or .NET is not detected.** Add the executable path override
for the stack, then reopen the project and run the doctor again.

**An APK's manifest cannot be read.** Set `tools.aapt2Path` to a compatible aapt2,
or attach a project instead of an APK.

## Projects

**A project is not detected.** Add the folder that contains the settings file
(`settings.gradle`, `pubspec.yaml`, `package.json`, `*.csproj`, or the APK) rather
than a parent folder. Repository scans stop at `projects.scanDepth` and skip folders
listed in `projects.ignoreFolders`.

**The detected application ID or module is wrong.** Set a manual override on the
project page. Overrides survive detection refreshes; clear the field to drop one.

**A monorepo subproject is missing.** Turn on `projects.scanMonorepos` and rescan, or
add the subproject folder directly.

## Runs and installs

**A build fails.** Open the session to see the exact command, working directory,
exit code, and the tail of the output. **Dry run** validates artifact resolution
without installing. Missing SDK components are reported by the toolchain doctor.

**An install fails.** Check device storage, that the APK matches the connected
device's ABI and API level, and that the package is not signed with a conflicting
key. Multi-device runs retry a failed install without rebuilding.

**A run never starts.** A run configuration needs a device and a valid artifact.
`run.maxConcurrentDevices` limits how many devices are mutated at once; wait for a
slot or raise the limit deliberately.

**Sessions disappeared.** Retention defaults to 14 days and 2 GB
(`logging.retentionDays`, `logging.maxSessionBytes`). Cleanup runs at startup.

## Mirroring

**Mirroring is unavailable.** Install scrcpy and make sure `tools.scrcpyPath` points
at a compatible version, then retry. The About page shows the detected scrcpy
version.

**The mirror is black or freezes.** Lower `screen.maxSize`, `screen.maxFps`, or
`screen.videoBitRate`; try a USB 3 port; and restart the mirror. Some devices block
screen capture on secure surfaces.

**No audio.** Audio capture needs a scrcpy and Android version that support it. Turn
on `screen.audio` and check the scrcpy output in the session log.

**Too many mirrors.** `screen.maxMirrors` and `screen.maxRecordings` bound
simultaneous capture. Close unused sessions or raise the limits.

**A docked mirror stopped when I left the page.** That is intentional. Use **Pop
out** for a mirror that stays open.

## Emulators and wireless

**No AVDs are listed.** Create one in Android Studio's Device Manager and check
`tools.emulatorPath` and `tools.androidSdkPath`.

**An emulator does not start.** Cold boot it, check virtualization support in the
BIOS, and make sure the AVD is not already running.

**Wireless pairing fails.** Both devices must be on the same network, the pairing
port differs from the connect port, and the pairing code expires quickly. Use the QR
flow when possible, or enter `host:port` and the six-digit code manually.

**A remembered wireless device does not reconnect.** Wake the device, confirm it is
on the same network, and reconnect from **Wireless**. Increase
`wireless.reconnectDelaySeconds` if adb needs more time to start.

## Plugins

**Plugins are disabled.** The launch used `--safe-mode`, `--no-plugins`, or
`features.safeMode` is on. Extensions shows the reason.

**A plugin says Trust required.** Review the plugin folder, then choose **Trust**.
Trust is pinned to the plugin's content hash; changing its files requires a new
decision.

**A plugin is incompatible.** Its manifest API version must match the host's major
API version. Update the plugin or remove it. A minor mismatch loads with a warning.

**A plugin failed to load.** Extensions lists the error and the plugin is isolated
from the rest of the shell. Open **Logs** for details, then disable it, uninstall it,
or start with `--safe-mode`.

**Enable or disable did not take effect.** Both require a restart to fully load or
unload assemblies.

**An update broke a plugin.** Use **Rollback** in Extensions to restore the previous
version. Updates keep one backup per plugin.

**An install was rejected.** Check the catalog hash, the dependency minimum versions,
and the trust decision. Rejected downloads are removed from quarantine; when cleanup
fails, the reported path can be deleted by hand.

## Catalog

**Refresh fails.** The source must be an absolute HTTPS URL or an existing local file
or folder. The error shows the HTTP status or validation message. The last cached
copy remains browsable offline.

**The catalog is invalid.** Read the reported error code in the
[Catalog schema](catalog.md#validation) table. Common causes are a missing
`schemaVersion`, a non-semantic version, a download URL without HTTPS, or a hash that
is not 64 hexadecimal characters.

**Install fails after download.** The SHA-256 did not match the catalog, the package
exceeded the 8 MiB download or 64 MiB extracted limit, or the archive contains an
unsafe path. Fix the package and republish the catalog with the new hash.

## Settings

**DroidDeck starts with defaults.** The settings file was unreadable or invalid; a
recovery notice is written to the activity log and Diagnostics. Restore `settings.json`
from `settings.json.bak`, or reset the layer in **Settings → Import, export, and
reset**.

**A setting will not stick.** A higher layer overrides it. Check the profile, team
project file, project overrides, run configuration, device record, and any `--set` or
`DROIDDECK__*` override. **Settings → Settings health** reports stale or invalid
overrides.

**The window is off-screen.** DroidDeck validates saved placement against the
visible desktop and falls back to the default position. Delete `window.x` and
`window.y` if a stale value persists.

## Performance

**High CPU or memory during mirroring.** Lower mirror size, frame rate, and bit rate;
reduce `screen.maxMirrors`; and close unused sessions. The performance guard warns at
`screen.warnAtPercent` of a budget.

**The activity log grows large.** Lower `logging.maxUiLines`, raise `logging.level`
to `warning`, or shorten `logging.retentionDays`.

## Updates

**Check for updates is unavailable.** Manual update checking is off by default. Turn
on the manual update check on **About**, or set `updates.manualCheckEnabled` to
`true`.

**The check fails.** DroidDeck needs an HTTPS connection to the GitHub releases API.
A firewall, proxy, or offline machine produces a clear failure message. DroidDeck
never downloads or installs updates; open the release page and update manually.

## Logs and data

| What | Where |
| --- | --- |
| Settings | `<data>\settings.json` and `settings.json.bak` |
| Activity and plugin logs | `<data>\logs` |
| Session history | `<data>\sessions` |
| Device records | `<data>\devices` |
| Diagnostics exports | `<data>\diagnostics` |
| Plugins | `<data>\plugins` and `plugin-backups` |
| Catalog sources and cache | `<data>\catalog` |
| Quarantined files | `<data>\quarantine` |

Reports and exported sessions run through the redactor, which masks private keys,
URL credentials, bearer tokens, JWTs, and secret-looking arguments. Logs written by
third-party plugins are still third-party content; review them before sharing.

## Reporting a problem

Open a GitHub issue with the diagnostics ID, the copied report, the steps to
reproduce, and whether safe mode changes the behavior. Do not attach secrets, signing
keys, or private project files. Security issues should follow the
[security policy](security.md).
