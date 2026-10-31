# Changelog

All notable changes are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Prereleases sort below
their release: `0.0.1-alpha` precedes `0.0.1`.

## [Unreleased]

## [0.1.0] - 2026-10-31

Project-detection and shell-consistency release. Internals continue the
deepening work: Gradle plugin resolution, project detection, shell dialogs, run
finalization, settings traversal, toolchain probes, and plugin package handling
each moved behind one seam.

### Added

- Gradle project detection resolves plugins declared through version catalogs,
  `apply false` declarations, convention plugins from `buildSrc` and included
  builds, and plugin markers, then exposes the effective plugin set.
- Variant detection composes application IDs from flavor and build-type
  suffixes, honours `defaultFlavor`, and qualifies names when several
  application modules exist. Run configurations seed the module, package name,
  task, and artifact glob from the selected variant.
- One shell dialog service serializes content dialogs and initializes file
  pickers for the main window.

### Changed

- Project scans and explicit project adds share one detection policy and one
  Android host probe.
- Run sessions finalize through one context and finalizer, and a multi-device
  start returns the started group handle before any device runs.
- Settings read, write, flatten, and merge traverse one document and reuse the
  casing stored in the file; merge performs one lookup per key.
- Toolchain probes come from one spec table and run concurrently.
- adb failure classification and `df` output parsing are single-sited and
  shared by the executor, device commands, and metadata source.
- Plugin package acquisition owns redirects, size caps, hash checks, path
  escapes, reserved device names, duplicate entries, extraction caps, and
  cleanup; the installer keeps orchestration and auditing.
- Manifest validation separates file-free metadata rules from full validation,
  and the loader reads and validates in one call.
- Failure notices and device target selection are shared shapes used by pages
  and tests.

### Fixed

- Plugin secret names no longer fail for long valid id and pair combinations; a
  stable hashed name is used past the store limit, and legacy names are migrated
  on read and cleared on write. Removal deletes both names.

## [0.0.2-alpha] - 2026-10-31

Architecture hardening release. No user-facing features were added; the
internals were consolidated into deeper modules so device, plugin, run, and
project behaviour shares one truth per concern.

### Changed

- Device tracking publishes the single `DeviceInfo` record; the duplicate
  `AdbDevice` type and its hand-copied transport fields are gone.
- All adb callers share one command executor for device arguments, timeouts, and
  failure classification, and the toolbox now reports text-detected failures as
  failures even when adb exits zero.
- The validator, catalog, installer, and loader share one plugin compatibility
  and dependency policy. Catalog installs now reject an incompatible API or host
  version before downloading.
- Plugin settings are created by one factory that owns schema parsing, secret
  store checks, and per-file store reuse; a missing secret store falls back to
  the raw store with a surfaced warning instead of failing.
- Install state and plugin status are computed once, outside the Extensions page,
  so updates, rollbacks, trust, and version reconciliation cannot drift.
- Flutter and Metro runs compose one supervised child-process session for line
  events, stderr tails, graceful stop, and process-tree kill.
- Run sessions finalize through one method and take the session lock for every
  state transition.
- Settings health checks and editors read one value policy table; the run
  concurrency clamp now honours the documented maximum.
- Capacitor and Cordova share one web-hybrid adapter, detector regex helpers are
  single-sited, and project refresh drives compare and apply from one field table.
- Screenshots and thumbnails use one remote-capture sequence for the temporary
  path, pull, cleanup, and failure handling.

### Fixed

- Emulator console snapshot failures reported as `KO` at exit zero stay failures.
- Loaded and failed plugins keep the loader's validation warnings in their
  status, and plugin settings schema warnings are logged.
- The Extensions page reconciles its statuses with the installed versions so an
  update is not offered again after a catalog install or rollback.
- Catalog entries with invalid host-range boundaries are reported as
  incompatible instead of compatible.

## [0.0.1-alpha] - 2026-10-31

First public alpha. Portable `win-x64` build; no installer and no auto-update.

### Added

- ADB device tracking with hotplug updates, per-device notes and tags, device
  toolbox (apps, permissions, shell, ports, files, intents, broadcasts, reboot),
  emulator management, and wireless pairing.
- scrcpy mirroring with docked and external windows, screenshots, recording,
  device keys, and limits on concurrent sessions.
- Project scanning and runs for Android Gradle, Flutter, React Native and Expo,
  MAUI, Capacitor and Cordova, and Unity, including multi-device run groups and
  build-once install.
- Live logcat streaming and filtering, run sessions, custom runs, crash
  reporting, and startup log retention.
- Performance sampling with charts, summaries, and CSV export; UI hierarchy
  inspector with bounds overlay and selectors; test runner with result parsers.
- Plugin host with isolated load contexts, fault boundaries, capabilities, trust
  prompts, and an audit log.
- Plugin extension points for commands, detectors, run steps, device actions and
  tabs, navigation pages, panels, home widgets, settings sections, status bar
  items, dialogs, renderers, log parsers, highlight rules, and test runners.
- Plugin UI component kit, schema-driven plugin settings with DPAPI-backed
  secrets, and catalog browsing, install, update, rollback, and uninstall.
- Plugin SDK: `DroidDeck.Plugin.Abstractions`, `DroidDeck.Plugin.UI`, four
  `dotnet new` templates, and the `droiddeck-plugin-pack` tool.
- Settings hub with search, profiles, themes, chord keybindings, JSON editing,
  configuration source maps, script trust, and a toolchain doctor.
- Command palette, menu bar command registry, first-run onboarding, Home cockpit,
  About page, diagnostics with redaction, and a manual update check.
- DroidDeck app icon in the portable executable and window chrome.
- Accessibility and localization groundwork: keyboard navigation, Narrator
  names, high-contrast and reduced-motion handling, scaling support, and
  pseudo-locale coverage.
- Release gates: tag-driven release workflow, portable size budget, startup
  measurement, SPDX SBOM, SHA-256 checksums, and a release checklist.

### Known limitations

- Alpha quality: plugin APIs, settings schemas, and UI may change before 1.0.0.
- Binaries are unsigned and may trigger a SmartScreen warning; verify the
  published SHA-256 checksum before running.
- No installer or auto-update. Upgrade by replacing the portable folder.
- The in-app update check reads the latest stable release, so it ignores
  prereleases.
- The eight-hour soak, clean-machine portable pass, and hands-on accessibility
  pass are deferred for this alpha and documented in the release notes.
- Plugins run in-process with full trust. Only install plugins you trust.

[Unreleased]: https://github.com/raulshma/droiddeck/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/raulshma/droiddeck/releases/tag/v0.1.0
[0.0.2-alpha]: https://github.com/raulshma/droiddeck/releases/tag/v0.0.2-alpha
[0.0.1-alpha]: https://github.com/raulshma/droiddeck/releases/tag/v0.0.1-alpha
