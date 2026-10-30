# DroidDeck documentation

DroidDeck is a local Windows control deck for Android devices. It tracks devices
over adb, scans and runs Android projects, streams logs, mirrors screens with
scrcpy, and hosts in-process plugins. It has no accounts, telemetry, or cloud
sync: settings, logs, diagnostics, and plugin data stay on the computer.

## Start here

- [User guide](user-guide.md) — install, connect a device, add a project, run, mirror, and troubleshoot.
- [0.0.1-alpha release notes](releases/v0.0.1-alpha.md) — highlights, install, checksums, and known limitations.
- [Settings reference](settings.md) — every setting, its scope, and its default.
- [Accessibility and languages](accessibility.md) — keyboard, Narrator, scaling, contrast, motion, and language options.
- [Catalog schema](catalog.md) — how to publish a plugin catalog.
- [Plugin authoring](plugin-sdk/authoring.md), [API reference](plugin-sdk/api-reference.md), [packaging](plugin-sdk/packaging.md) — the plugin SDK guides.
- [Troubleshooting](troubleshooting.md) — safe mode, logs, diagnostics, and common failure modes.
- [Privacy](privacy.md) — exactly what stays local and when the app uses the network.
- [Security](security.md) — threat model, plugin trust, and how to report a vulnerability.
- [Licensing](licensing.md) — the DroidDeck license and third-party notices.
- [Release checklist](release.md) — size, startup, soak, security, accessibility, and clean-machine gates.
- [Release security review](security-review.md) — scope, controls, findings, and accepted risks.
- [Plugin SDK](plugin-sdk/index.md) — build, package, and install a plugin.

## Requirements

- Windows 10 1809 or newer (Windows 11 recommended)
- .NET 10 SDK when building from source
- Android SDK platform-tools (`adb`) for real devices
- Optional: scrcpy for mirroring, a JDK and Gradle/Flutter/Node/Unity/MAUI toolchains for the stacks you use

## Install

Portable builds are unpackaged and self-contained. Download
`DroidDeck-<version>-win-x64.zip` from the
[releases page](https://github.com/raulshma/droiddeck/releases), verify it against
`SHA256SUMS.txt`, extract the folder, and run `DroidDeck.exe`. To keep settings
and logs beside the executable, create an empty file named `portable.marker` in
that folder.

Release binaries are unsigned, so Windows SmartScreen may warn; verify the
checksum before running. The current
[0.0.1-alpha release notes](releases/v0.0.1-alpha.md) list the alpha limitations
and the bundled plugin SDK packages.

Build and test commands live in the [repository README](https://github.com/raulshma/droiddeck#build-and-run).

## Support

DroidDeck is local-first and collects nothing automatically. When reporting a
problem, attach a diagnostics report from **Diagnostics** or **About** and the
steps that reproduce it. The [security policy](security.md) explains private
disclosure for vulnerabilities.

Android is a trademark of Google LLC. DroidDeck is not affiliated with Google.
