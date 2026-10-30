# Changelog

All notable changes are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Prereleases sort below
their release: `0.0.1-alpha` precedes `0.0.1`.

## [Unreleased]

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

[Unreleased]: https://github.com/raulshma/droiddeck/compare/v0.0.1-alpha...HEAD
[0.0.1-alpha]: https://github.com/raulshma/droiddeck/releases/tag/v0.0.1-alpha
