# DroidDeck — Complete Product & Engineering Plan (v2.0)

> **Status:** Complete & Refined Engineering Plan v2.0  
> **Date:** 2026-10-27  
> **Working name:** DroidDeck  
> **Platform:** Windows 11 (22H2 / Build 22621+) primary; Windows 10 22H2 (Build 19045+) best effort (final minimum: open decision D1)  
> **Stack:** .NET 10, WinUI 3, Windows App SDK 2.5.x  
> **Scope:** Full v1 feature set, no deadline  
> **Repository:** `C:\Code\Projects\android-debug-ui`

## What Changed in v2.0 (Research & Engineering Refinements)

| Area | v1.0 Baseline | v2.0 Engineering Refinement | Why It Matters |
|---|---|---|---|
| **ADB Transport & Tracking** | Spawned `adb track-devices -l` process + spawned `adb devices -l` on each change | **Hybrid ADB Client**: direct TCP socket client (`127.0.0.1:5037`, `0014host:track-devices-l`) with 4-hex-byte framing + CLI process execution for complex commands (`install`, `pair`, `raw shell`) | Eliminates CRT pipe-buffering latency on Windows, avoids spawning redundant `adb.exe` processes on hotplug bursts, and matches Android Studio (`adblib`) reliability |
| **scrcpy Invocation** | Passed non-existent `--adb` flag and `--clipboard-autosync` | Passes configured `adb.exe` via `ADB` environment variable and server via `SCRCPY_SERVER_PATH`; uses `--no-audio` default, `--no-clipboard-autosync` toggle, and handles exit code `2` (disconnect) vs `1` (error) | Fixes immediate CLI failure (`--adb` is invalid in upstream `scrcpy`) and distinguishes normal USB unplugs from launch errors |
| **scrcpy Window Hosting** | Assumed `SetParent` into a WinUI 3 child HWND works without clipping | Three-mode architecture in POC-04: **Mode A** (`SetParent` + `WS_CLIPCHILDREN` + dialog cloaking), **Mode B** (**Docked Companion Window** via `--window-borderless` + `WinEventHook` position/z-order sync), and **Mode C** (Pop-out/External) | Solves WinUI 3 DirectComposition "airspace" clipping and SDL2 input/focus conflicts while preserving an integrated split-pane UX |
| **On-Demand Screen Recording** | Restarted interactive `scrcpy` with `--record` | Spawns a secondary headless recorder (`scrcpy --no-playback --no-window --record <file.mp4>`) alongside the live mirror | Starts and stops MP4 recording at any time without restarting or flickering the user's active mirror session |
| **Logcat PID & Crash Tracking** | Switched `adb logcat` to `--pid=<pid>` after launch | Single persistent stream (`adb logcat -v threadtime -v year -v uid -T 1 -b main,system,crash,events`) with **host-side UID + dynamic PID-set filtering** (`am_proc_start` / `am_proc_died`) | Prevents losing early startup logs, multi-process logs, `system_server` crash/ANR lines, or post-crash respawn logs when PIDs change |
| **Gradle Variant & APK Discovery** | Static regex parsing of `build.gradle(.kts)` and APK globbing | **Two-tier discovery**: instant static heuristics on add + AGP `output-metadata.json` parsing after build (with cached async `--group=build` task indexing for convention plugins) | Accurately resolves flavors defined in `build-logic` convention plugins and deterministically selects the exact built APK/split set |
| **Xiaomi / Vendor Install Errors** | Covered generic install errors only | Added `INSTALL_FAILED_USER_RESTRICTED` (MIUI/HyperOS "Install via USB" toggle) + pre-install ABI/minSdk validation | Directly prevents friction on the baseline Xiaomi POCO F1 test device |
| **Plugin UI in WinUI 3 ALCs** | Returned raw `UserControl` across `AssemblyLoadContext` | Added **Host UI Component Kit** (`PluginForm`, `PluginCard`, `PluginDataGrid`, `PluginCodeViewer`) in `DroidDeck.Plugin.UI` + composite `PluginXamlMetadataProvider` + 3-strike circuit breaker | Avoids WinUI 3 `.xbf`/`.pri` resource lookup failures across isolated `AssemblyLoadContext` boundaries |
| **Config Hierarchy & Repo Trust** | 7 scopes; single project scope | **8 scopes** (splitting Team-Shared `.droiddeck/project.json` from Local Project Overrides) + **Repository Script Trust** (SHA-256 pinned in `trust.json`) | Allows committing team configs without dirtying git with local paths, and blocks untrusted repo scripts until approved |
| **Single-Instance & Lifecycle** | Custom named pipe only | `AppInstance.FindOrRegisterForKey` + `RedirectActivationToAsync` paired with a `CurrentUserOnly` Named Pipe for unpackaged CLI args and `SetForegroundWindow` | Follows official Windows App SDK lifecycle guidance while preserving unpackaged CLI argument forwarding |

## Table of contents

1. Product vision
2. Locked decisions
3. Target users and primary workflows
4. Scope and capability matrix
5. Environment baseline
6. Architecture
7. Project detection and stack adapters
8. Device layer
9. Run and debug pipeline
10. Screen mirroring and control
11. Logs, activity, and diagnostics
12. UX and interaction design
13. Configuration and customization
14. Plugin SDK
15. Plugin catalog
16. Renderer SDK
17. Performance engineering
18. Testing and quality
19. Security, privacy, and safety
20. Packaging, distribution, and updates
21. POC plan
22. Milestones and build order
23. Risks and mitigations
24. Assumptions and open decisions
25. Appendices


## 1. Product vision

### 1.1 One-sentence vision

**DroidDeck is a fast, local-first Windows cockpit for Android development: add a project, detect how to build and run it, see every connected device, run on one or many devices, watch a unified timeline of build/install/launch/logcat activity, and mirror/control the device without leaving the app.**

### 1.2 Problem

Android development on Windows is fragmented:

- Android Studio is powerful but heavy for the day-to-day run/debug loop.
- Build commands, install flags, launch activities, logcat filters, and device actions live in different tools and terminals.
- Multi-device work is manual: build once, install repeatedly, launch, and track multiple log streams.
- Screen mirroring and device control are separate apps.
- Project-specific run scripts are tribal knowledge.
- Existing tools are hard to customize, extend, or automate.

### 1.3 Solution

DroidDeck treats a development workflow as three first-class objects:

- **Project** — a folder plus detected stack, modules, toolchains, and run configurations.
- **Device** — an adb target with state, metadata, screen, and actions.
- **Session** — one run of a project/run configuration on one device, with a step timeline, logs, and artifacts.

A run is a **pipeline**, not a single command:

```text
resolve toolchain → build → locate artifact → install → launch
→ attach logcat → optional mirror → monitor crash/ANR → finish
```

Every step has a status, duration, exact command, output, and failure reason. That is the core of the product’s “visibility” promise.

### 1.4 Principles

1. **Fast by default.** No polling, no UI-thread blocking, bounded memory, batched logs, cached detection.
2. **Visible.** Every action is a step with a command, duration, and log. Nothing happens silently.
3. **Local-first.** No accounts, no cloud dependency, no telemetry.
4. **Keyboard-friendly.** Command palette and shortcuts for every common action.
5. **Safe.** Destructive actions confirm; plugins are explicitly trusted; secrets are protected.
6. **Customizable.** Every feature has defaults, every default is visible and resettable, every layer can override.
7. **Extensible.** Built-in features use the same contracts as plugins, so the plugin SDK is real and complete.
8. **Honest about capabilities.** Unsupported or limited stacks are labeled clearly rather than presented as one-click magic.

### 1.5 What DroidDeck is not (v1)

- Not a replacement for Android Studio’s code editor, refactoring, or Gradle sync UI.
- Not an in-app breakpoint debugger. v1 is run + logs + crash + screen. IDE handoff and JDWP/DAP debugging are post-v1.
- Not a native screen-streaming implementation in v1. scrcpy powers mirroring; a native backend is post-v1.
- Not a cloud device farm, CI service, or test-lab orchestrator.
- Not an iOS tool.
- Not a marketplace host. The plugin catalog is a user-configurable feed; no default official marketplace ships in v1.
- Not a full terminal emulator. Device shell is a command console; external terminal integration can come later.

### 1.6 Success criteria for v1

- A new user can add an Android Gradle project and run it on a connected device in **under two minutes**.
- Incremental run loop (no clean build) is fast enough to feel like a daily driver.
- A user can run the same project on **multiple devices** with one action; the build happens once.
- Every run produces a readable timeline and persistent logs.
- Logcat at **10k lines/second** remains responsive.
- Mirroring is usable at **30–60 fps** with input latency under ~150 ms via scrcpy.
- A user can customize the shell, tools, run pipeline, logs, screen, and feature visibility without editing source code.
- A plugin can add a detector, run step, device action, command, page, and renderer through a documented SDK.
- The app runs from a portable folder on a clean Windows machine.


## 2. Locked decisions

| Area | Decision | Implications |
|---|---|---|
| **Debug scope** | Run + logs + crash/ANR + screen control in v1 | No in-app breakpoints; IDE handoff/JDWP post-v1 |
| **Screen backend** | scrcpy now; native C# streaming later | `IScreenSession` abstraction; embedding POC with external-window fallback |
| **Project stacks** | Gradle/Kotlin, Flutter, RN/Expo, .NET MAUI, KMP/Compose, Unity, Capacitor/Cordova, APK-only, custom scripts | All detected from day one; deep support in waves |
| **Deep support order** | Gradle/KMP + APK/custom → Flutter → RN/Expo → MAUI → Capacitor/Cordova → Unity | Foundation and universal fallback first |
| **Distribution** | Portable first, MSIX later | Self-contained portable build; installer/update story later |
| **Window model** | Single main window + tabs + pop-out mirror | Shared services; multi-monitor mirror support |
| **v1 extras** | Build variants/flavors, emulator + wireless ADB, device toolbox, APK drop/install, perf + UI inspector, test runner, hot reload, command palette | All in v1, sequenced by milestone |
| **Customization** | Fully configurable app and features | Layered settings, profiles, presets, feature visibility, JSON editing |
| **Plugin SDK** | Full SDK in v1 | In-process, full-trust plugins; local install + optional catalog URL; full UI + custom renderers |
| **Plugin isolation** | In-process, full trust | Native UI and performance; explicit trust prompt, hash pinning, safe mode; out-of-process later |
| **Plugin distribution** | Local install from file/folder + optional catalog URL | No default marketplace; catalog is a user-configured feed |
| **Plugin UI** | Full UI extensions + custom renderers | Nav pages, home widgets, project/device tabs, session panels, toolbar actions, settings, themes, log/file/chart/test renderers |
| **Plugin trust** | Hash pinning + trust prompt | SHA-256 mandatory; signatures displayed if present, not required; re-prompt on hash/capability change |
| **Config sharing** | Local-first + optional repo sharing | `.droiddeck/project.json` opt-in per project; no auto-install of plugins |
| **Settings UX** | Progressive disclosure | Basic/Advanced/Expert modes; search; source badges; JSON editing |
| **Profiles/presets** | Profiles + presets | Workflow profiles and smaller reusable bundles |
| **Schedule** | No deadline, build it all | Milestones are engineering order, not scope cuts |


## 3. Target users and primary workflows

### 3.1 Personas

| Persona | Needs |
|---|---|
| **Android/Kotlin developer** | Fast build/install/launch, variants, logcat filtering, screen control, device toolbox |
| **Flutter developer** | Hot reload/restart, device selection, logs, multi-device runs |
| **React Native developer** | Metro management, `adb reverse`, reload, logs, device screen |
| **.NET MAUI developer** | `dotnet build`/deploy, device selection, logs, no Android Studio dependency |
| **QA / release engineer** | APK drop/install, multi-device install, screenshots, recordings, app data/permissions |
| **Power user / plugin author** | Full configuration, JSON editing, custom detectors/steps/actions, plugin SDK, renderers |

### 3.2 Primary workflows

**Gradle daily loop**
1. Open DroidDeck; the last project and device selection are restored.
2. Press `F5` (or click Run).
3. Watch Build → Install → Launch → Logcat in the session timeline.
4. The mirror opens automatically (optional).
5. Filter logcat to the app; fix code; rerun.
6. Optionally run on all devices or a device group.

**Flutter hot reload loop**
1. Run the Flutter config on one device.
2. Use Hot Reload / Hot Restart buttons while the `flutter run` process stays alive.
3. Watch both Flutter output and device logcat.
4. Stop or restart the app from the same workspace.

**React Native loop**
1. Start Metro from the session.
2. Run on a device; `adb reverse` is applied automatically.
3. Use Reload / Dev Menu actions.
4. Watch Metro and logcat in separate source tabs.

**QA APK workflow**
1. Drag an APK into the app or onto a device.
2. Inspect package, version, min/target SDK, ABIs, permissions, launcher.
3. Select devices; install and launch.
4. Take screenshots or recordings; pull app data.

**Plugin author workflow**
1. `dotnet new droiddeck-plugin`.
2. Build and load the unpacked plugin in developer mode.
3. Add a detector/step/action/page/renderer.
4. Restart to reload; use plugin logs and health.
5. Package as `.ddplugin` and optionally publish a catalog entry.

### 3.3 Success metrics

- Time to first successful run after adding a project: **< 2 minutes**.
- Device hotplug visible: **< 300 ms**.
- Log UI responsive at **10k lines/s**.
- Multi-device run with 3 devices: one build, three installs, independent logs.
- Crash/ANR detected and surfaced without leaving the app.
- A user can hide unused features and customize layout without editing files.
- A plugin can be installed, trusted, enabled, and used without restarting more than once.


## 4. Scope and capability matrix

### 4.1 Supported stacks

All stacks are detected by the v1 release; internal milestones land detectors in waves alongside deep support. Day one means the v1 release, not the first internal milestone.

| Stack | Detection | Build/install/launch | Variants | Hot reload | Tests | Notes |
|---|---|---|---|---|---|---|
| **Android Gradle/Kotlin** | Full | Full | Full | — | Gradle unit + instrumented | Foundation adapter |
| **KMP / Compose Multiplatform** | Full | Full via Gradle | Full | — | Gradle | Reuses Gradle adapter |
| **Flutter** | Full | Full | Debug/profile/release | Hot reload/restart | `flutter test` | `flutter run` per device |
| **React Native** | Full | Full | Debug/release | Metro reload | Jest/custom | Metro + `adb reverse` |
| **Expo** | Full | CLI preset | Prebuild | Reload | Custom | Managed workflow limited |
| **.NET MAUI** | Full | Full | Debug/release | Rebuild/restart | `dotnet test` | Workload check |
| **Capacitor** | Full | CLI/Gradle | Debug/release | — | Custom | Dev server handling |
| **Cordova** | Full | CLI | Debug/release | — | Custom | CLI prompts |
| **Unity** | Detect | Custom script or APK | — | — | Custom | No generic build |
| **APK-only** | N/A | Install/launch | — | — | — | `aapt2` inspection |
| **Custom scripts** | Detect | User-defined | — | — | Custom JUnit | Universal fallback |

### 4.2 Deep-support waves

| Wave | Stacks | Why |
|---|---|---|
| **Wave 1** | Gradle/Kotlin, KMP, APK-only, custom scripts | Foundation, largest ecosystem, universal fallback |
| **Wave 2** | Flutter, React Native/Expo | Most common cross-platform mobile workflows |
| **Wave 3** | .NET MAUI, Capacitor, Cordova | .NET audience and mobile-web stacks |
| **Wave 4** | Unity | Requires project-specific Editor build scripts; APK fallback works earlier |

### 4.3 Feature matrix

| Feature | v1 | Notes |
|---|---|---|
| Add folder / detect stack / review configs | Yes | All stacks |
| Add APK as project | Yes | Inspect, install, launch |
| Run config editor | Yes | Full pipeline, env, steps, conditions |
| Build variants/flavors | Yes | Gradle + stack equivalents |
| Toolchain doctor | Yes | SDK, adb, JDK, Gradle, Flutter, Node, scrcpy, Unity |
| Device hotplug + metadata | Yes | Event-driven |
| Device groups/tags | Yes | Rules + manual |
| Multi-device run | Yes | Build once where possible |
| Live build/logcat timeline | Yes | Unified session events |
| Crash/ANR detection | Yes | Banner + restart actions |
| Screen mirror/control | Yes | scrcpy, embedded or external |
| Pop-out mirror | Yes | Separate window |
| Screenshot / recording | Yes | scrcpy + screencap |
| Device thumbnails | Yes | Low-rate screencap |
| Emulator management | Yes | List/launch/stop; create AVD later |
| Wireless ADB | Yes | Pair/connect, mDNS, auto-reconnect |
| Device toolbox | Yes | Shell, files, ports, permissions, app data, reboot, deep links |
| APK drop/install | Yes | Split APKs, inspect, install-multiple |
| Log filters/search/highlight/export | Yes | Saved filters, regex, crash grouping |
| Test runner | Yes | Gradle + Flutter + custom JUnit |
| Performance metrics | Yes | Launch time, FPS/jank, CPU/mem, battery |
| UI hierarchy inspector | Yes | `uiautomator` tree + overlay + tap |
| Hot reload | Yes | Flutter and RN only |
| Command palette + shortcuts | Yes | Remappable |
| Profiles + presets | Yes | Full customization |
| Feature visibility/flags | Yes | Hide unused stacks/tabs/tools |
| Plugin SDK | Yes | In-process full trust |
| Plugin catalog | Yes | Optional user-configured sources |
| Custom renderers | Yes | Log detail, charts, files, tests, data |
| MSIX installer | Post-v1 | Portable first |
| Native screen streaming | Post-v1 | scrcpy protocol/native decoder |
| In-app breakpoints/JDWP | Post-v1 | IDE handoff first |
| R8 retrace/crash deobfuscation | Post-v1 | Mapping upload + retrace |
| Cloud sync/accounts | Not planned | Local-first |
| Remote device farms | Post-v1 | Optional integration |
| iOS | Not planned | Android-focused |

### 4.4 Capability badges in the UI

Every project/run config shows one of:

- **Full** — build/install/launch/logcat/tests supported.
- **CLI** — runs through the stack’s CLI; multi-device may build per device.
- **APK-only** — install/launch existing APK.
- **Custom** — user-defined command pipeline.
- **Limited** — detection works but one-click run needs setup (e.g. Unity, Expo managed).

The badge is clickable and explains exactly what is and is not supported.


## 5. Environment baseline

The following was verified on the development machine. DroidDeck must detect tools at runtime rather than hardcode these paths.

| Item | Value |
|---|---|
| OS | Windows 11 (build 10.0.26300) |
| .NET SDK | 10.0.403 |
| Windows App SDK runtime | 2.5.1 installed |
| Visual Studio | 18.11.2 |
| WinUI workload/templates | Not installed initially; official `Microsoft.WindowsAppSDK.WinUI.CSharp.Templates` available |
| Android SDK | `C:\Users\raulshma\AppData\Local\Android\Sdk` |
| adb | 37.0.1 at `platform-tools\adb.exe` |
| Emulator | Installed |
| scrcpy | 4.1 via winget; latest upstream 5.0.1 |
| Android Studio JBR | JDK 25 |
| `JAVA_HOME` | JDK 17 |
| Node/npm/npx | Installed via nvm4w |
| Flutter | Not on PATH |
| .NET MAUI workload | Not installed |
| Connected device | Xiaomi POCO F1, Android 15 / API 35, arm64-v8a, 1080×2246, 440 dpi |

### 5.1 POC prerequisites

- Install the official WinUI templates:
  ```powershell
  dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates
  ```
- Validate that a WinUI 3 unpackaged app builds and runs with .NET 10 and Windows App SDK 2.5.x.
- Use the system Android SDK/adb. Do not bundle adb.
- For release builds, bundle a known-good scrcpy (e.g. 5.0.1) with license notices; for development, detect the installed scrcpy.
- Create a small sample Android Gradle project under `samples/hello-android` for POC and test fixtures.
- Real-device smoke tests use the POCO F1; emulator tests use an API 35 AVD.


## 6. Architecture

### 6.1 Solution layout

```text
DroidDeck.slnx
global.json
Directory.Build.props
.editorconfig
src/
  DroidDeck.App/                     WinUI 3 shell, views, view models, DI composition
  DroidDeck.Core/                    Domain models, interfaces, run pipeline, stores
  DroidDeck.Adb/                     adb process client, device tracker, logcat, shell
  DroidDeck.Projects/                Built-in detectors, stack adapters, toolchain discovery
  DroidDeck.Screen/                  scrcpy integration, window embedding, thumbnails
  DroidDeck.Plugins/                 Plugin host, loader, trust, catalog, renderer registry
  DroidDeck.Plugin.Abstractions/     Core plugin contracts, DTOs, manifest, no WinUI
  DroidDeck.Plugin.UI/               UI plugin contracts (WinUI 3)
tests/
  DroidDeck.Tests/                   Unit + integration tests
  DroidDeck.PluginHost.Tests/        Plugin host/security/catalog/renderer tests
  DroidDeck.PluginSamples/           Sample plugins, including failing/version-mismatch plugins
samples/
  hello-android/                     Minimal Gradle project for POC/fixtures
  sample-plugin-detector/
  sample-plugin-panel/
docs/
  adr/                               Architecture decision records
  plugin-sdk/                        SDK docs, manifest, extension points, security
  ux/                                UX spec, wireframes, design tokens
  schemas/                           JSON schemas for settings/project/catalog/theme
```

### 6.2 Project responsibilities

| Project | Responsibility | Must not |
|---|---|---|
| **DroidDeck.App** | WinUI shell, pages, view models, DI, window management | Contain adb parsing or project detection logic |
| **DroidDeck.Core** | Domain models, run pipeline, session orchestration, configuration; implements stable plugin service contracts | Reference WinUI or the plugin host |
| **DroidDeck.Adb** | Process-based adb client, device tracker, logcat, shell, install/launch primitives | Reference WinUI |
| **DroidDeck.Projects** | Detectors, Gradle/Flutter/RN/MAUI/etc. adapters, toolchain discovery | Reference WinUI |
| **DroidDeck.Screen** | scrcpy process management, window embedding, screenshots, thumbnails | Own session state |
| **DroidDeck.Plugins** | Plugin discovery, loading, trust, catalog, extension registry, renderer registry | Reference App |
| **DroidDeck.Plugin.Abstractions** | Stable plugin contracts, DTOs, manifest, and plugin-facing service interfaces | Reference WinUI, Core implementations, or host internals |
| **DroidDeck.Plugin.UI** | WinUI-specific plugin UI contracts | Reference host internals or Core implementations |

Dependency direction:

```text
App → Core, Adb, Projects, Screen, Plugins, Plugin.UI
Plugins → Core, Plugin.Abstractions, Plugin.UI
Plugin.UI → Plugin.Abstractions
Core → Plugin.Abstractions
Projects/Adb/Screen → Core, Plugin.Abstractions
Plugin.Abstractions → .NET only
```

`DroidDeck.Plugin.Abstractions` is the stable boundary. Core implements its service interfaces; built-in detectors, steps, actions, and renderers implement the same extension contracts as plugins. Only the app composes the plugin host.

### 6.3 Key services

| Service | Responsibility |
|---|---|
| `IToolchainService` | Discover SDK, adb, aapt2, emulator, JDK, Gradle, Flutter, Node, MAUI workloads, scrcpy, Unity; validate compatibility matrices |
| `IProjectService` | Add/remove/refresh projects, run ordered detectors, parse AGP `output-metadata.json`, persist project configs |
| `IAdbSocketClient` | Direct TCP client (`127.0.0.1:5037`) for length-framed ADB wire protocol (`host:version`, `host:devices-l`, `host:track-devices-l`) |
| `IAdbProcessClient` | Process-based `adb.exe` runner for streaming/complex CLI operations (`install`, `install-multiple`, `logcat`, `pair`, `exec-out`) |
| `IDeviceService` | Track devices, batched single-shell metadata, groups, emulators, wireless pairing/QR, per-device mutation locks |
| `IRunSessionService` | Orchestrate build/install/launch/logcat sessions; multi-device fan-out |
| `ILogcatService` | Per-device logcat streams, parsing, filtering, crash detection |
| `IScreenService` | Mirror/control lifecycle, screenshots, recordings, thumbnails |
| `IConfigurationService` | Layered settings, scopes, source map, change events |
| `IFeatureService` | Feature flags and visibility |
| `ICommandRegistry` | Commands, command palette, keybindings |
| `IThemeService` | Theme tokens, fonts, log colors, contrast validation |
| `ITemplateEngine` | Placeholder expansion and condition evaluation |
| `IProfileService` / `IPresetService` | Named profiles and reusable presets |
| `ISecretStore` | DPAPI-backed secrets |
| `IProcessRunner` | Streaming process execution, job objects, cancellation, ANSI handling |
| `ISessionStore` | Session summaries, logs, history, retention |
| `IPluginHost` | Plugin lifecycle, extension registry, failure isolation |
| `IPluginCatalogService` | Catalog sources, browse, update checks, downloads |
| `IRendererRegistry` | Renderer discovery, selection, fallback, disposal |
| `IDiagnosticsService` | App/tool/plugin diagnostics, log folder, copy report |
| `INotificationService` | In-app/toast notifications (toast optional) |
| `IEventBus` | Typed events for UI and plugins |

### 6.4 Threading and process model

- All I/O is async. No `.Result`, no UI-thread waits.
- Device tracking uses a persistent TCP socket connection to the ADB server (`127.0.0.1:5037`, sending `0014host:track-devices-l` and reading 4-hex-digit length-framed push payloads), with automatic `adb.exe start-server` bootstrap on `SocketException` and a CLI `adb.exe track-devices -l` fallback; no polling.
- Logcat uses long-lived per-device processes.
- Build/install/launch use `IProcessRunner` with:
  - `ProcessStartInfo.ArgumentList` for safe quoting.
  - UTF-8 stdout/stderr.
  - Async stream reads with `\n`/`\r` progress handling.
  - Windows Job Objects with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` for reliable tree kill.
  - `Process.Kill(entireProcessTree: true)` as a fallback.
  - Cancellation tokens per step/session.
- Batch/script files (`.bat`, `.cmd`) run through `cmd.exe /d /s /c` with correct quoting.
- PowerShell scripts run through `pwsh -NoLogo -NoProfile -File`.
- `.sh` scripts require Git Bash/WSL; if unavailable, show a clear error.
- Log pipelines use bounded `Channel<T>`; UI updates are batched (~50 ms), never one dispatcher call per line.
- Per-project build semaphore; per-device mutation semaphore; global concurrency limit.
- Plugin calls are wrapped; UI factories run on the UI thread; long-running plugin operations are async and cancellable.

### 6.5 Core data model

| Entity | Key fields |
|---|---|
| `ProjectModel` | Id, Name, RootPath, Kind, Modules, RunConfigurations, ToolchainOverrides, DetectionEvidence, LastOpened |
| `ModuleInfo` | Name, Path, Type, ApplicationId, Namespace, Variants |
| `RunConfiguration` | Id, Name, Kind, BuildPlan, Artifact, Install, Launch, Logs, Screen, Devices, Steps, HotReload, Tests |
| `RunPlan` | Ordered steps, BuildOnce flag, ArtifactLocator, LaunchSpec, LogSpec |
| `RunStepDefinition` | Type, Command, Args, Cwd, Env, Timeout, Retries, Condition, FailurePolicy |
| `DeviceInfo` | Serial, State, Model, Manufacturer, AndroidVersion, ApiLevel, Abi, IsEmulator, AvdName, ScreenSize, Density, Battery, TransportId, Tags |
| `DeviceGroup` | Id, Name, Rule/Manual members, Tags |
| `RunGroup` | Id, ProjectId, RunConfigId, ChildSessions, AggregateState, StartedAt |
| `RunSession` | Id, GroupId, DeviceSerial, State, Steps, Logs, StartedAt, EndedAt, CrashInfo |
| `RunStep` | Name, Type, State, StartedAt, EndedAt, Command, ExitCode, OutputSummary |
| `LogEntry` | Timestamp, Source, Level, Tag, Pid, Tid, Message, Raw, Continuation |
| `TestRunResult` | Suite, Case, State, Duration, StackTrace, Attachments |
| `AppSettings` | Appearance, Tools, RunDefaults, Logging, Screen, Features, Plugins, Catalog, Renderers, Keybindings |
| `ProjectSettings` | Detection overrides, Run configs, Log filters, Screen defaults, Steps, Toolchain, Sharing |
| `DeviceOverrides` | Screen, Install, Logs, Custom actions, Tags |
| `FeatureFlags` | Enabled, Visible, Experimental, RequiresRestart |
| `PluginManifest` | Id, Name, Version, ApiVersion, MinHostVersion, EntryPoint, Capabilities, Dependencies |
| `PluginInfo` | Manifest, InstallPath, Enabled, Healthy, LoadTime, Errors, TrustedHash |
| `CatalogSource` | Id, Name, Url, Enabled, Priority, AuthTokenSecret, AllowPrerelease, LastChecked, ETag |
| `CatalogPlugin` | Id, Name, Publisher, Summary, Versions, SourceId |
| `CatalogVersion` | Version, ApiVersion, MinHostVersion, DownloadUrl, Sha256, Signature, Size, Changelog, Capabilities |
| `RendererDescriptor` | Id, PluginId, Kind, DisplayName, Priority, Enabled, Health |
| `ThemeDefinition` | Name, Colors, LogColors, Fonts, IsDark |
| `KeybindingMap` | CommandId → Gesture |
| `CustomDetectorDefinition` | Id, Name, Priority, Markers, RunConfigs, Enabled |
| `CustomStepDefinition` | Name, Type, Command, When, Timeout, FailurePolicy |
| `DeviceActionDefinition` | Name, Command, Confirm, ShowOutput, Icon |
| `Preset` | Id, Name, Scope, Values |
| `Profile` | Id, Name, Overrides |

### 6.6 Run pipeline abstraction

```csharp
public interface IRunStep
{
    string Id { get; }
    Task<RunStepResult> ExecuteAsync(RunStepContext context, CancellationToken ct);
}

public sealed record RunPlan(
    IReadOnlyList<IRunStep> Steps,
    bool BuildOncePerGroup,
    ArtifactLocator? Artifact,
    LaunchSpec? Launch,
    LogSpec Logs);

public interface IRunCoordinator
{
    Task<RunGroup> RunAsync(
        ProjectModel project,
        RunConfiguration config,
        IReadOnlyList<DeviceInfo> devices,
        RunOptions options,
        CancellationToken ct);
}
```

Step types:

- `ProcessStep` — arbitrary command.
- `AdbInstallStep` — install APK/split APKs.
- `AdbLaunchStep` — resolve activity, `am start -W`.
- `WaitForDeviceStep` — wait for device/boot completion.
- `ResolvePidStep` — find app PID(s).
- `StartLogcatStep` — attach logcat.
- `MirrorStep` — optional screen mirror.
- `TestStep` — run tests and parse results.
- `CustomStep` — user/plugin-defined.
- `ConditionStep` — conditional branch or skip.

Every step emits:

```text
StepStarted
StepOutput(line)
StepProgress(percent/message)
StepCompleted(duration, exitCode)
StepFailed(error, outputTail)
```

### 6.7 Persistence and file layout

```text
%LocalAppData%\DroidDeck\
  settings.json
  profiles\<name>.json
  projects\<id>.json
  devices\<serial>.json
  themes\<name>.json
  keybindings.json
  presets\<name>.json
  plugins.json
  plugins\
    <plugin-id>\
      <version>\
      settings.json
      data\
      logs\
  catalog\
    cache\<source-id>.json
    packages\<sha256>.ddplugin
  trust.json
  sessions\
    <yyyyMMdd-HHmmss>-<project>-<device>\
      meta.json
      activity.jsonl
      build.log
      logcat.log
      adb.log
      tests\
  logs\
    app-*.log
```

Optional team-shared project config:

```text
<repo>\.droiddeck\project.json
<repo>\.droiddeck\run-configs\*.json
```

Rules:

- JSON with source-generated `System.Text.Json`.
- Atomic writes: temp file + replace; keep `.bak`.
- Schema version + migrations.
- Corrupt file quarantine; app remains usable.
- Unknown JSON keys preserved on round-trip.
- Session logs written continuously; summaries indexed for history.
- Retention configurable by age and size; cleanup on startup.
- Portable mode can store data next to the executable via `--data-dir` or a marker file.

### 6.8 Error handling and diagnostics

- Every service logs with correlation IDs: session ID, project ID, device serial, plugin ID.
- App log rolling files; no telemetry.
- Unhandled exception handler writes a crash report and keeps the app alive where possible.
- “Copy diagnostics” includes versions, paths, tool status, active sessions, plugin states, and recent errors.
- Failed steps keep the tail of output for the UI and logs.
- Plugin failures are isolated and surfaced in the plugin manager.
- Destructive actions require confirmation and show the exact command.


## 7. Project detection and stack adapters

### 7.1 Detection goals

- Fast: no Gradle invocation during add; typical scan < 500 ms.
- Explainable: show evidence, modules, variants, toolchain, and warnings.
- Non-destructive: never modify the project.
- Overridable: every detected value can be edited.
- Monorepo-aware: detect multiple projects/modules.
- Extensible: custom detectors via JSON or plugins.

### 7.2 Detector pipeline

```csharp
public interface IProjectDetector
{
    string Id { get; }
    int Priority { get; }
    Task<DetectionResult?> DetectAsync(ProjectScanContext context, CancellationToken ct);
}
```

`ProjectScanContext`:

- Root path.
- File system abstraction (testable).
- Scan limits (depth, file count, timeout).
- Ignore list (`node_modules`, `build`, `.gradle`, `.git`, `dist`, `.idea`, `Pods`, etc.).
- Toolchain snapshot.

Detection rules:

1. Check root markers first; then a small set of known subdirectories (`android/`, `app/`, `composeApp/`, `platforms/android/`).
2. Do not recursively scan the whole repository.
3. Score detectors by priority and evidence.
4. If multiple projects are detected (monorepo), list them with checkboxes.
5. If nothing matches, offer “Add as custom”.
6. Never run Gradle/Flutter/Node commands during add.
7. Cache results; “Refresh detection” is explicit or debounced on file changes.

### 7.3 Detection review UI

The review sheet shows:

- Detected stack and confidence.
- Root and module paths.
- Application ID, namespace, launcher if known.
- Suggested run configs with checkboxes.
- Toolchain warnings (JDK/Gradle/AGP mismatch, missing Flutter/MAUI workload).
- “Add as custom” fallback.
- “Rescan” and “Edit manually”.

### 7.4 Stack adapter contract

```csharp
public interface IStackAdapter
{
    ProjectKind Kind { get; }
    StackCapabilities Capabilities { get; }

    Task<IReadOnlyList<RunConfiguration>> CreateDefaultRunConfigsAsync(
        ProjectModel project,
        CancellationToken ct);

    Task<RunPlan> CreateRunPlanAsync(
        ProjectModel project,
        RunConfiguration config,
        CancellationToken ct);
}

public sealed record StackCapabilities(
    bool CanBuild,
    bool CanInstall,
    bool CanLaunch,
    bool CanListVariants,
    bool CanHotReload,
    bool CanRunTests,
    bool BuildOnceForMultipleDevices);
```

### 7.5 Android Gradle/Kotlin

**Markers:** `settings.gradle`/`settings.gradle.kts`, `build.gradle`/`build.gradle.kts`, `gradlew.bat`/`gradlew`, `gradle/wrapper/gradle-wrapper.properties`.

**Detection (Two-Tier Strategy):**
- **Tier 1 (Instant Static Scan, < 150 ms, zero Gradle invocation):**
  - Parse `settings.gradle(.kts)` for `include(":app")` and `includeBuild("build-logic")` composite builds.
  - Parse build files for `com.android.application`, `com.android.library`, `applicationId`, `namespace`.
  - Parse `gradle/libs.versions.toml` for AGP/Kotlin versions and `gradle-wrapper.properties` for wrapper version.
  - Parse `productFlavors`, `flavorDimensions`, and `buildTypes` heuristically across `app/` and `build-logic/`, plus any existing `build/outputs/apk/**/output-metadata.json` files; fall back to `debug`/`release`.
- **Tier 2 (Post-Build `output-metadata.json` & Cached Background Task Index):**
  - Because modern Gradle projects often define flavors inside convention plugins (`build-logic/`), DroidDeck reads AGP's generated `output-metadata.json` after builds to extract exact `variantName`, `applicationId`, `versionCode`, and split APK file lists (`elements[].outputFile`).
  - Offers an optional, cached background task query (`gradlew.bat :app:tasks --group=build --console=plain --quiet`) keyed by build-file hashes to populate complete variant lists without blocking project addition.
- Detect wrapper Gradle version and JDK compatibility matrix (e.g., warning immediately if `JAVA_HOME` is JDK 25 while Gradle < 8.10 requires JDK 17/21, and offering detected JDK 17/21 or Android Studio JBR).
- Detect launcher activity after build via `aapt2 dump badging` or `cmd package resolve-activity`.

**Default run config:**
1. `gradlew.bat :app:assemble<Variant>` (build once).
2. Locate APK (`app/build/outputs/apk/<variant>/*.apk`).
3. `aapt2 dump badging` → package, launcher, min/target SDK, ABIs.
4. `adb -s <serial> install -r -t <apk>`.
5. `adb -s <serial> shell am start -W -n <package>/<activity>`.
6. Resolve PID and start logcat.
7. Optional mirror.

**Variants:** `devDebug`, `prodDebug`, `prodRelease`, etc. The variant picker uses `assemble<Variant>` and selects the matching APK. If parsing fails, allow manual variant text.

**Toolchain:**
- Per-project JDK selection (`JAVA_HOME`, Android Studio JBR, other detected JDKs).
- Gradle args: offline, daemon, configuration cache, parallel, warning mode.
- `ANDROID_SERIAL` for tasks that require a device.
- Warn on JDK/Gradle/AGP mismatches; don’t block.

**Tests:**
- `:app:test<Variant>UnitTest`.
- `:app:connected<Variant>AndroidTest` with `ANDROID_SERIAL`.
- Parse JUnit XML from `build/test-results/` and `build/outputs/androidTest-results/`.

**Error parsing:**
- Kotlin `e: file:///...: (line, col): error`.
- Java `error:`.
- Gradle `FAILURE: Build failed with an exception.` / `What went wrong`.
- Clickable `file:line` links.

### 7.6 KMP / Compose Multiplatform

- Detect `kotlin("multiplatform")` / `org.jetbrains.kotlin.multiplatform`.
- Find the Android application module (often `composeApp` or `androidApp`).
- Reuse the Gradle adapter for build/install/launch/tests.
- No hot reload; rebuild/restart.
- iOS targets are ignored.

### 7.7 Flutter

**Markers:** `pubspec.yaml`, `.metadata`, `android/`.

**Detection:**
- Parse app name and Flutter environment.
- Detect `android/app/build.gradle(.kts)` application ID.
- Cache `flutter --version --machine` output.
- Devices use adb serials.

**Run:**
- Build once: `flutter build apk --debug` (or profile/release).
- Locate `build/app/outputs/flutter-apk/app-debug.apk`.
- Install and launch via adb.
- Multi-device: build once, install N.

**Hot reload (`flutter run --machine` JSON-RPC):**
- Single device: `flutter run --machine -d <serial> --debug` (with interactive stdin `r`/`R`/`q` fallback).
- Parses `app.started`, `app.progress`, and `app.stop` machine events to capture the active `appId` and exact reload durations in milliseconds.
- Buttons: Hot Reload (`app.restart` with `fullRestart: false` / `r`), Hot Restart (`fullRestart: true` / `R`), Stop (`app.stop` / `q`), Open DevTools URL.
- Stream both Flutter machine/stdout output and DroidDeck's native UID/PID logcat stream in parallel tabs.
- Multi-device hot reload: one `flutter run --machine` process per device, or fall back to build-once (`flutter build apk`) + parallel ADB install.

**Tests:** `flutter test --reporter json`; integration tests via `flutter test integration_test` when configured.

**Caveats:** Flutter SDK must be installed and on PATH or configured; show a clear doctor entry.

### 7.8 React Native / Expo

**Markers:** `package.json` with `react-native` or `expo`, `android/`, `metro.config.js`, `app.json`.

**Bare React Native:**
- Parse package name from `android/app/build.gradle`.
- Default Metro port 8081.
- Single-device fast path: `npx react-native run-android --deviceId <serial>`.
- Multi-device: Gradle `assembleDebug` → install/launch per device.
- Start Metro: `npx react-native start --port <port>`.
- Apply `adb reverse tcp:<port> tcp:<port>` per device.
- Reload via Metro/dev-menu integration; fallback documented.
- Tests: Jest (`npm test -- --json`) or custom.

**Expo:**
- If `android/` exists (prebuild/bare): use the RN/Gradle path.
- Managed workflow: detect and show a **Limited** badge. Offer `npx expo start --dev-client` / `npx expo run:android` when prebuild is configured.
- Do not pretend managed Expo supports arbitrary native debugging.

**Logs:** Metro output and logcat in separate source tabs.

### 7.9 .NET MAUI

**Markers:** `*.csproj` with `net*-android` in `TargetFrameworks`.

**Detection:**
- Parse `ApplicationId`, `ApplicationTitle`, target frameworks.
- Check `dotnet workload list` for the MAUI workload; show install guidance if missing.
- Locate the Android APK after build.

**Run:**
- `dotnet build -f net10.0-android -c Debug`.
- Locate signed APK under `bin/Debug/net10.0-android/<rid>/`.
- Install and launch via adb.
- Alternative: `dotnet build -t:Run` for single-device workflows.
- Multi-device: build once, install N.

**Hot reload:** not supported in v1; “Rebuild & restart”.
**Tests:** `dotnet test` for unit tests; device tests via custom command.
**Caveats:** MAUI workload and Android SDK must be installed; show doctor entries.

### 7.10 Capacitor / Cordova

**Capacitor markers:** `capacitor.config.ts`/`.json`, `@capacitor/core` dependency, `android/`.
- `npx cap sync android` when needed.
- `npx cap run android --target <serial>`.
- Multi-device fallback: sync + Gradle assemble + install/launch.
- Dev server handling is configurable.

**Cordova markers:** `config.xml`, `platforms/android`.
- `npx cordova run android --device <serial>`.
- Multi-device: build once, install N if artifacts are locatable.
- CLI prompts are captured; prefer non-interactive flags.

**Tests:** custom commands/JUnit XML.

### 7.11 Unity

**Markers:** `ProjectSettings/ProjectVersion.txt`, `Assets/`.

**Reality:** Unity has no generic one-command Android build. DroidDeck will:
- Detect Unity version and editor path.
- Offer “Open in Unity”.
- Offer “Install existing APK”.
- Allow a custom build method configured by the user. Provide a copyable `Assets/Editor/DroidDeckBuild.cs` template that implements a batch build method.
- Show a **Custom** capability badge.

This is explicit, not a silent limitation.

### 7.12 APK-only

- Add an `.apk` file or a folder of split APKs.
- Inspect with `aapt2 dump badging`:
  - package, version name/code, min/target SDK, launchable activity, ABIs, permissions.
- Install with `adb install` or `adb install-multiple` for split APKs.
- Launch via activity or `cmd package resolve-activity`.
- Handle signature mismatch, downgrade, test-only APKs with clear prompts.
- AAB/bundletool is post-v1.

### 7.13 Custom scripts/projects

Detect candidates:
- `package.json` scripts containing “android”.
- `.ps1`, `.cmd`, `.bat`, `.sh` files.
- `.vscode/tasks.json`.
- `Makefile`, `justfile`.

Offer them as suggestions; do not auto-create dozens of run configs. The user defines:
- Name, command, args, cwd, env, shell.
- Optional APK glob, package/activity, log filter.
- Pre/post steps, conditions, failure policy.
- Placeholders: `${projectDir}`, `${deviceSerial}`, `${apkPath}`, etc.

### 7.14 Variants, artifacts, and launch resolution

- Variant picker per stack.
- Artifact locator supports globs and split APK folders.
- Package/activity resolution order:
  1. Explicit config override.
  2. `aapt2 dump badging` on the built APK.
  3. `adb shell cmd package resolve-activity --brief <package>`.
  4. `monkey -p <package> -c android.intent.category.LAUNCHER 1` fallback.
- Cache package/activity per variant; invalidate on rebuild.
- Show the exact launch command in the session timeline.

### 7.15 Toolchain doctor

Checks:

- Android SDK path and platform-tools.
- adb version and server status.
- aapt2/build-tools.
- emulator and AVDs.
- JDKs and `JAVA_HOME`.
- Gradle wrapper version and AGP compatibility.
- Flutter SDK and version.
- Node/npm/npx and package manager.
- scrcpy version and adb compatibility.
- Unity editor.
- MAUI workload.
- SDK licenses.
- Environment variables (`ANDROID_HOME`, `ANDROID_SDK_ROOT`, `JAVA_HOME`).

Each check has status (OK/warning/error), detected value, fix action, and “Copy command”.

### 7.16 Refresh and monorepos

- “Refresh detection” re-scans markers; preserves user edits; shows a diff.
- Optional file-watcher auto-refresh with debounce (default off or 500 ms).
- Monorepo: list detected projects/modules; allow adding multiple; support nested project paths.
- Multiple projects remain independent: adding, removing, or refreshing one does not stop another project's active sessions or device assignments.
- Ignore rules configurable globally/project.

### 7.17 Detection tests

Fixture projects:

- `gradle-basic`, `gradle-flavors`, `gradle-multi-module`, `gradle-kts`.
- `kmp-basic`.
- `flutter-basic`.
- `rn-basic`, `expo-managed`, `expo-bare`.
- `maui-basic`.
- `capacitor-basic`, `cordova-basic`.
- `unity-basic`.
- `custom-scripts`.
- `apk/` fixtures.
- Malformed/partial projects.

Assertions: detected kind, modules, variants, application ID, run config commands, warnings, and no Gradle invocation.


## 8. Device layer

### 8.1 Device tracking (Hybrid ADB Socket + Process Architecture)

- **Primary Transport (`IAdbSocketClient`):** Opens a persistent TCP connection to the ADB server (`127.0.0.1:5037`, or `ANDROID_ADB_SERVER_PORT`) and sends `0014host:track-devices-l`.
- Reads the 4-byte `OKAY` status followed by continuous 4-hex-digit length-prefixed frames (`<hex4><payload>`) containing the full `-l` device list (`serial`, `state`, `usb:`, `product:`, `model:`, `device:`, `transport_id:`). This eliminates Windows CRT pipe-buffering delays and avoids spawning a new `adb devices -l` process on every hotplug event.
- **Server Bootstrap & Recovery:** If `127.0.0.1:5037` refuses the connection, runs `adb.exe start-server` once via `IProcessRunner` and reconnects with jittered exponential backoff (`100 ms → 250 ms → 500 ms → 2 s`).
- **Process Fallback (`IAdbProcessClient`):** Falls back to a long-lived `adb.exe track-devices -l` process if localhost socket access is restricted.
- Debounce hotplug bursts (50 ms window) and never poll on a timer.

### 8.2 Metadata (Single-Shell Sectioned Batch)

Batch all hardware and system state queries into **one single `adb -s <serial> shell` invocation** per device using section delimiters (`---PROP---`, `---WM---`, `---BAT---`, `---NET---`), completing in < 250 ms:

- `ro.product.manufacturer`, `ro.product.model`, `ro.product.name`.
- `ro.build.version.release`, `ro.build.version.sdk`.
- `ro.product.cpu.abi`, `ro.product.cpu.abilist`.
- `ro.build.type`, `ro.debuggable`.
- `ro.kernel.qemu`, `ro.boot.qemu.avd_name`.
- `ro.build.characteristics` (tv, watch, automotive).
- `wm size`, `wm density`.
- Battery via `dumpsys battery` (lazy).
- IP via `ip -f inet addr show wlan0` (on demand).

Metadata is cached; refresh on reconnect or explicit action.

### 8.3 Device states

- `device` — ready.
- `offline` — show reconnect/restart adb.
- `unauthorized` — show “Allow USB debugging” instructions; retry.
- `recovery`, `sideload`, `bootloader` — limited actions.
- `connecting` — wireless.
- Multiple devices with the same model — show serial suffix.

### 8.4 Device groups and tags

- Manual groups.
- Rule-based groups: model, manufacturer, API level, ABI, emulator/physical, tag.
- Built-in groups: All devices, Emulators, Physical devices.
- Per-run target: single device, group, or all.
- Device-specific overrides.

### 8.5 Emulator management

- List AVDs via `emulator -list-avds`.
- Read AVD config from `%USERPROFILE%\.android\avd\<name>.avd\config.ini`.
- Show API, ABI, resolution, RAM.
- Launch options: cold boot, snapshot, wipe data, headless (optional), no boot animation.
- Wait for `sys.boot_completed`; show boot progress.
- Stop via `adb -s <serial> emu kill`.
- Create AVD via `avdmanager` is post-v1; link to Android Studio for now.

### 8.6 Wireless ADB (Pairing Code, QR Code & mDNS)

- Android 11+ 6-digit code pairing:
  - Developer options → Wireless debugging → Pair device with pairing code.
  - Enter IP, pairing port, 6-digit code.
  - `adb pair <host>:<port> <code>`.
  - `adb connect <host>:<connect-port>`.
- Android 11+ QR code pairing:
  - Generates a standard ADB Wi-Fi QR code payload (`WIFI:T:ADB;S:<serviceName>;P:<randomPassword>;;`) rendered directly in the pairing dialog.
  - Monitors `adb mdns services` for `_adb-tls-pairing._tcp` announcements matching the session and pairs/connects automatically when scanned from the phone.
- mDNS discovery via `adb mdns check` and `adb mdns services`.
- Older devices: `adb tcpip 5555` over USB, then `adb connect <ip>:5555` (warn about security).
- Remember known wireless devices; optional auto-reconnect.
- Show IP and connection state.
- Handle firewall/network errors with guidance.

### 8.7 Device toolbox

| Tool | Capabilities |
|---|---|
| **Apps** | List/filter packages, system apps toggle, uninstall, clear data, force stop, app info, permissions, battery optimization |
| **Files** | Browse device paths, pull/push, drag-drop, create/delete folders, open local folder, overwrite prompts |
| **Shell** | Command input with history, output, clear/copy; no full TTY in v1 |
| **Ports** | List/add/remove `adb forward` and `adb reverse`; saved mappings; auto reverse for RN |
| **Permissions** | List dangerous permissions, grant/revoke, app ops |
| **Deep links** | Saved links per project, variables, `am start -a VIEW -d <uri>` |
| **Broadcasts** | Saved broadcasts, extras |
| **Reboot** | System/recovery/bootloader with confirmation |
| **Screenshot** | `screencap`, save/copy |
| **Recording** | scrcpy record, start/stop, save |
| **Device report** | Optional v1; hidden by default; warns about size and time; `adb bugreport` can be slow |
| **Perf** | Launch time, FPS/jank, CPU/memory, battery |
| **UI inspector** | `uiautomator` tree, screenshot overlay, tap element |

### 8.8 Per-device locking

- Mutating operations (install, uninstall, clear data, push/pull large files) acquire a per-device semaphore.
- Multiple logcat streams are allowed.
- One interactive mirror per device; reopening focuses the existing one.
- Builds are per-project, not per-device.

### 8.9 Custom device actions

- JSON/script-defined actions with placeholders.
- Confirm flag for destructive actions.
- Optional output panel.
- Toolbar visibility/order configurable.
- Plugin-contributed actions integrate with the same registry.


## 9. Run and debug pipeline

### 9.1 States

```text
Queued → Preparing → Building → Installing → Launching → Running
      → Stopping → Succeeded | Failed | Cancelled | PartiallyFailed
```

A `RunGroup` contains one child `RunSession` per device.

### 9.2 Steps

1. **Prepare** — resolve toolchain, validate paths, check device state, expand env/placeholders.
2. **Build** — run once per group when supported; stream output.
3. **Locate artifact** — find APK/split APKs; inspect package/activity.
4. **Install** — per device, with flags and error handling.
5. **Launch** — resolve activity, `am start -W`, capture launch time.
6. **Attach logs** — resolve PID, start logcat, detect crash/ANR.
7. **Mirror** — optional; embedded/external/pop-out.
8. **Monitor** — keep session alive; crash/ANR events; user actions.
9. **Finish** — persist summary and logs; notify.

### 9.3 Build once, install many

- Gradle/Flutter/MAUI/RN: build once per group where the artifact is device-agnostic.
- CLI-only stacks: run per device or fall back to Gradle/CLI build.
- Parallel installs limited by per-device locks and global concurrency.
- If one device fails, others continue; group status becomes `PartiallyFailed`.
- “Retry failed devices” action.

### 9.4 Install handling

- Flags: `-r` (replace), `-t` (test), `-g` (grant permissions), `-d` (downgrade), `--user`.
- Split APKs: `adb install-multiple`.
- Pre-install compatibility check: compares APK `minSdkVersion` and native ABIs (`aapt2 dump badging`) against the target device's `ApiLevel` and `ro.product.cpu.abilist` before transferring large files.
- Common errors and one-click recovery actions:
  - `INSTALL_FAILED_UPDATE_INCOMPATIBLE` (signature mismatch) → offer one-click **Uninstall Existing & Reinstall**.
  - `INSTALL_FAILED_VERSION_DOWNGRADE` → offer **Retry with `-d`** (debug builds) or **Uninstall & Reinstall**.
  - `INSTALL_FAILED_USER_RESTRICTED` (common on Xiaomi/POCO MIUI & HyperOS when "Install via USB" is disabled) → show vendor-specific instructions (*"Enable 'Install via USB' in Developer Options"*) + **Retry**.
  - `INSTALL_FAILED_INSUFFICIENT_STORAGE` → show `/data` free space (`df -h /data`) + link to Apps Toolbox.
  - `INSTALL_FAILED_TEST_ONLY` → automatically retry with `-t`.
- Show exact command and output.

### 9.5 Launch handling

- `am start -W -n <package>/<activity>`.
- Parse `Status`, `LaunchState`, `TotalTime`, `WaitTime`.
- Optional intent action/data/extras, flags, wait-for-debugger.
- If activity is unknown, resolve via package manager.
- Fallback `monkey` only if necessary; mark as fallback in the timeline.

### 9.6 Logcat attach (UID + Dynamic Process-Set Filtering)

- **Why not `adb logcat --pid=<pid>`:** Passing `--pid` to `adb logcat` drops early startup lines before `pidof` resolves, misses `system_server` (`ActivityManager` / `WindowManager`) and `crash_dump64` (`DEBUG`) lines logged under non-app PIDs, and goes permanently silent whenever the app crashes and respawns under a new PID.
- **v2.0 Persistent Stream Architecture:**
  1. Resolve the package's immutable kernel **UID** once after install via `cmd package list packages -U <package>` (e.g., `10245`).
  2. Start a single persistent stream before launch:
     `adb -s <serial> logcat -v threadtime -v year -v uid -T 1 -b main -b system -b crash -b events`
  3. Watch `am_proc_start`, `am_proc_bound`, `am_proc_died`, `am_crash`, and `am_anr` in the `events` buffer to maintain the live set of active PIDs for `<package>`.
  4. Perform **host-side App-Only filtering**: match lines where `entry.Uid == appUid` OR `activePids.Contains(entry.Pid)` OR crash/system lines (`AndroidRuntime`, `DEBUG`, `ActivityManager`, `WindowManager`) referencing `<package>` or its PIDs.
- Clear logcat before launch (configurable via virtual clear bookmark or `adb logcat -c`).
- Detect `FATAL EXCEPTION`, `ANR in`, `am_crash`, `am_anr`, and native tombstones (`F/DEBUG`).
- Pin the **Crash & ANR Banner** with grouped stack trace, **Restart App**, **Rebuild & Restart**, and **Copy Bug Report** while keeping the log stream alive across process restarts.

### 9.7 Hot reload

- **Flutter:** `flutter run` process; Hot Reload `r`, Hot Restart `R`, Stop `q`.
- **React Native:** Metro + reload via dev server/dev menu; `adb reverse` maintained.
- **Other stacks:** “Rebuild & restart”.
- No false promises: MAUI/KMP/Capacitor/Cordova/Unity do not get hot reload in v1.

### 9.8 Test runner

- Gradle unit/instrumented tests.
- Flutter unit/widget/integration tests.
- Custom command + JUnit XML.
- RN Jest/custom.
- MAUI `dotnet test`/custom.
- Multi-device instrumented tests.
- Results UI: suite tree, pass/fail/skip, duration, stack trace, attachments.
- Rerun failed tests; export JUnit XML.
- Test logs in the session.

### 9.9 Session actions

- Stop (kill build/install; optionally stop app).
- Restart app (launch step only).
- Rebuild & restart.
- Force stop.
- Clear app data.
- Uninstall.
- Screenshot/record.
- Open mirror.
- Copy command / copy diagnostics.
- Export logs.

### 9.10 Session history

- Summaries in `sessions/index.json`.
- Full logs on disk.
- Reopen read-only.
- Re-run creates a new session.
- Filters by project, device, status, date.
- Retention configurable.

### 9.11 Multi-device orchestration

- Device picker persists per run config.
- “All devices” and groups.
- Build once; install per device.
- Per-device tabs in the session workspace.
- Aggregate status and progress.
- Independent cancellation.
- Sessions from different projects can run concurrently, subject to the global concurrency limit; closing a project page does not stop them.
- Each session keeps its project, run config, and device context; logs and actions never cross projects.
- Notifications when a background run finishes/fails.

### 9.12 Failure policies

- Step-level: stop, continue, retry N times.
- Session-level: stop all on build failure; continue installs on per-device failure.
- Timeouts for build/install/launch/logcat.
- Clear error summaries with the tail of output.
- “Copy diagnostics” for support.


## 10. Screen mirroring and control

### 10.1 Strategy

- **v1:** scrcpy, with embedded window if the POC proves stable; otherwise a managed external window.
- **Post-v1:** native C# scrcpy-protocol client for integrated multi-device walls and tighter rendering.
- The UI depends only on `IScreenSession`.

### 10.2 scrcpy integration

- Detect installed `scrcpy`; validate version (4.x / 5.x) and `adb` protocol compatibility.
- Release builds bundle a known-good `scrcpy` (`5.0.1`) under `Tools\scrcpy\` with Apache-2.0 license notices.
- **Environment Variable Configuration:** Upstream `scrcpy` does **not** have an `--adb` CLI flag; DroidDeck sets `ProcessStartInfo.Environment["ADB"] = configuredAdbPath` and `ProcessStartInfo.Environment["SCRCPY_SERVER_PATH"] = bundledServerPath`.
- Launch one interactive `scrcpy` process per device.
- Default CLI options:
  - `--serial <serial>`
  - `--window-title "DroidDeck · <model> · <serial>"`
  - `--max-size 1024` (or `0` for native resolution)
  - `--max-fps 60`
  - `--video-bit-rate 8M`
  - `--video-codec h264` (`h264`, `h265`, or `av1`)
  - `--no-audio` by default (audio capture is enabled by default in `scrcpy` 2.0+; passing `--no-audio` prevents unexpected desktop audio echo unless the user enables audio in DroidDeck)
  - `--stay-awake` (when connected via USB; `--screen-off-timeout 600` optional for wireless)
  - `--no-clipboard-autosync` only when the user disables clipboard sync (clipboard sync is enabled by default in `scrcpy`)
- **Exit Code Handling:**
  - Exit code `0`: normal window close.
  - Exit code `1`: startup/initialization error → display InfoBar with `scrcpy` stderr output.
  - Exit code `2`: device disconnected mid-session → show `Device disconnected — waiting to reconnect` state rather than an error dialog.
- Expose custom `scrcpy` arguments for power users.

### 10.3 Window Hosting Modes (Solving WinUI 3 Airspace & Clipping)

Because WinUI 3 renders its client area using a full-window DirectComposition surface (`Microsoft.UI.Composition`), reparenting an external SDL2/Direct3D HWND requires handling airspace clipping, DPI scaling, and XAML dialog occlusion. POC-04 validates three hosting modes behind `IScreenSession`:

1. **Mode A — Reparented Child HWND (`SetParent` + `WS_CLIPCHILDREN`):**
   - Applies `WS_CLIPCHILDREN` to the top-level WinUI HWND, creates a dedicated child container HWND mapped to the mirror pane's physical-pixel bounds (`XamlRoot.RasterizationScale`), launches `scrcpy --window-borderless`, and reparents the SDL2 window via `SetParent` + `SetWindowLongPtr(GWL_STYLE, WS_CHILD | WS_VISIBLE)`.
   - Temporarily hides/cloaks the child HWND whenever a modal XAML `ContentDialog` or flyout opens over the mirror pane so the child HWND does not occlude the dialog.
2. **Mode B — Docked Companion Window (`Owned` Borderless HWND):**
   - Launches `scrcpy --window-borderless --window-x <x> --window-y <y> --window-width <w> --window-height <h>`, sets the WinUI HWND as its Win32 owner (`GWLP_HWNDPARENT`), and synchronizes position, size, minimize, and restore via `SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE)`.
   - Avoids DirectComposition airspace conflicts and SDL2 input coordinate translation issues completely while appearing visually docked inside the Session Workspace.
3. **Mode C — Managed External / Pop-Out Window:**
   - Standard standalone `scrcpy` window or a secondary WinUI Pop-Out window with always-on-top and position persistence for multi-monitor setups.

### 10.4 Pop-out mirror

- New WinUI window hosting the mirror.
- Shared session/device services.
- Closing the pop-out stops or detaches the mirror per settings.
- Multi-monitor friendly.

### 10.5 Thumbnails

- Low-rate `adb exec-out screencap -p` (e.g. 1 fps) for device cards.
- Decode off-thread; reuse buffers.
- Pause when the card is not visible or the window is minimized.
- Fallback placeholder on failure.

### 10.6 Screenshot and Zero-Flicker Recording

- **Screenshot:** `adb -s <serial> exec-out screencap -p` streamed directly into memory; saves PNG to the session folder (or user Pictures folder) and copies the bitmap to the Windows clipboard in one click.
- **Zero-Flicker MP4 Recording:** Starting or stopping a recording while a live mirror is already open spawns a secondary headless recorder process (`scrcpy --serial <serial> --no-playback --no-window --no-audio --record <path.mp4>`) so the user's active mirror window never closes or flickers. Stopping the recording sends `Ctrl+C` / `WM_CLOSE` to flush the MP4 header cleanly.
- Fallback `adb shell screenrecord` is used only if `scrcpy` is unavailable (noting its 3-minute limit on older Android versions).
- Show file path, duration, file size, and **Open Folder**.

### 10.7 Input and control

- Touch/mouse, scroll, keyboard, clipboard sync.
- Drag-and-drop APK → install; other files → push to `/sdcard/Download`.
- Device buttons: power, volume, rotate, back/home/recents, notification shade.
- Keyboard shortcuts configurable.
- Screen-off/stay-awake options.

### 10.8 Multi-device

- One mirror per device.
- Session workspace tabs.
- Optional multi-device wall post-v1 (native backend preferred).
- Resource guard: warn when many mirrors are open.

### 10.9 Native backend (post-v1)

- Implement scrcpy protocol: push server, start `app_process`, video/control sockets.
- Decode H.264 via Media Foundation; render with Win2D/D3D11.
- Input injection via control socket.
- Multi-device wall, lower integration overhead, custom overlays.
- Keep `IScreenSession` stable.

### 10.10 Screen performance

- Mirror target: 30–60 fps, input latency < 150 ms.
- Thumbnails: < 2% CPU when idle.
- Pause video when hidden.
- Avoid re-encoding/decoding in the host in v1.
- Record only when requested.


## 11. Logs, activity, and diagnostics

### 11.1 Unified session view

Sources:

- **All** — merged timeline.
- **Build** — Gradle/Flutter/dotnet/CLI output.
- **Logcat** — device logs.
- **Activity** — app-level events (steps, device actions, crashes).
- **Test** — test runner output/results.

### 11.2 Log entry model

- Timestamp, source, level, tag, PID/TID, message, raw, continuation flag.
- Multi-line stack traces attach to the previous entry.
- Build output preserves ANSI colors or strips them based on settings.
- Raw line retained for export/copy.

### 11.3 Logcat parsing

- `threadtime` format.
- `--------- beginning of ...` markers.
- Multi-process apps.
- `FATAL EXCEPTION`, `ANR in`, `am_crash`, `am_anr`.
- Native crash/tombstone lines.
- Optional crash buffer.

### 11.4 Filters and search

- Level chips (V/D/I/W/E/F).
- Tag, package, PID.
- Text search, case sensitivity, regex, whole word.
- Saved filters per project/global.
- Exclude patterns.
- Search debounce (~200 ms).
- Highlight matches; next/previous.
- Filter applies to live data.

### 11.5 Highlight rules

- Built-in: errors red, warnings amber, crashes magenta, test failures red.
- Custom regex/string rules with foreground/background/bold.
- Priority and enable/disable.
- Import/export.
- Plugin-provided highlight rules.

### 11.6 Crash and ANR UX

- Banner at the top of the log panel.
- Stack trace extraction and grouping.
- Actions: Restart app, Rebuild & restart, Copy stack trace, Copy formatted report.
- Optional auto-scroll to crash.
- Session remains alive.

### 11.7 Build output

- Parse Gradle tasks, warnings, errors, `BUILD SUCCESSFUL/FAILED`, `What went wrong`.
- Kotlin/Java error locations clickable.
- Open in default editor or configured IDE.
- Show task progress and durations.
- Tail of output on failure.

### 11.8 Export and retention

- Export current filter or full session.
- Formats: `.log`, `.txt`, `.json`.
- Include device/session metadata and step summary.
- "Copy formatted report" template (session metadata, steps, logs, crash).
- Retention by days and size; cleanup on startup.
- Session logs survive restarts.

### 11.9 Log performance

- Bounded UI buffer (e.g. 20k lines) with full logs on disk.
- Batched UI flushes (~50 ms).
- Virtualized list; measure `ListView` vs `ItemsRepeater`.
- Custom bounded collection with batched notifications.
- No per-line dispatcher calls.
- Background parsing and filtering.
- Pause rendering when the log tab is hidden.

### 11.10 App diagnostics

- Rolling app log.
- Diagnostics page: versions, paths, tool status, active sessions, plugin health, config health.
- “Copy diagnostics” for bug reports.
- No telemetry.


## 12. UX and interaction design

### 12.1 Shell

```text
┌──────────────────────────────────────────────────────────────────────────────┐
│ DroidDeck          [Quick run ▾]  [Run F5]          ADB ● 2 devices    — □ × │
├──────────────┬───────────────────────────────────────────────────────────────┤
│ Home         │                                                               │
│ Projects     │                       page content                            │
│ Devices      │                                                               │
│ Sessions     │                                                               │
│ Extensions   │                                                               │
│ ───────────  │                                                               │
│ Settings     │                                                               │
│ Diagnostics  │                                                               │
├──────────────┴───────────────────────────────────────────────────────────────┤
│ ADB connected · 2 devices · 1 build running · Log queue 0 · v0.1.0           │
└──────────────────────────────────────────────────────────────────────────────┘
```

- Custom title bar, Mica backdrop, dark default.
- `NavigationView` left rail, collapsible.
- Status bar: adb state, device count, active builds, log queue depth, version.
- Single main window; pop-out mirror windows.
- Second launch focuses the existing instance.

### 12.2 Home

```text
Quick run
┌──────────────────────────────────────────────────────────────────────────────┐
│ Project [MyApp ▾]   Config [appDebug ▾]                                     │
│ Devices [POCO F1 ✓] [Pixel_8 ✓] [All]                        [Run F5]       │
└──────────────────────────────────────────────────────────────────────────────┘

Active sessions                                                    See all
┌──────────────────────────────┐  ┌──────────────────────────────┐
│ MyApp · POCO F1              │  │ MyApp · Pixel_8              │
│ Building 62%                 │  │ Running 00:42                │
│ ▓▓▓▓▓▓▓▓░░░░       [logs]    │  │ ✓ Build 12s  ✓ Install 2s    │
└──────────────────────────────┘  └──────────────────────────────┘

Devices
┌────────────┐  ┌────────────┐  ┌────────────┐
│ [thumb]    │  │ [thumb]    │  │  + Add     │
│ POCO F1    │  │ Pixel_8    │  │  device    │
│ Android 15 │  │ Android 15 │  │            │
│ ● Online   │  │ ● Online   │  │            │
└────────────┘  └────────────┘  └────────────┘

Recent projects                              Environment
MyApp            2 min ago                   adb ✓   SDK ✓
ShopApp          1 day ago                   JDK ✓   scrcpy ✓
```

- Home widgets are reorderable/hideable in Customize mode.
- Quick run remembers the last config/devices.
- Active sessions span projects and devices; each card opens its own session workspace.
- Environment health chips deep-link to Settings.

### 12.3 Add project flow

```text
Add project
Folder: C:\src\myapp                                             [Change]

Scanning... done in 180 ms
We found 1 project and 3 run configurations.

┌──────────────────────────────────────────────────────────────────────────────┐
│ ✓ MyApp                                            Android Gradle · Kotlin  │
│   app module · com.example.myapp · variants: devDebug, prodDebug, prodRelease│
│   ✓ Build:   gradlew.bat :app:assembleDebug                                  │
│   ✓ Install: adb install -r app/build/outputs/apk/debug/app-debug.apk         │
│   ✓ Launch:  am start -W -n com.example.myapp/.MainActivity                  │
│   ✓ Logcat:  filter by package/PID                                           │
│   ⚠ JDK 25 detected; Gradle 8.14 does not support it. Use JDK 17.          │
└──────────────────────────────────────────────────────────────────────────────┘

Also detected
  [ ] app (application)   [ ] core (library)   [ ] feature:login (library)

[Rescan]                                                [Add as custom] [Add]
```

Rules:
- Scan only marker files; never invoke Gradle.
- Show evidence and warnings.
- Multiple projects in a monorepo can be added individually.
- Unsupported folders get “Add as custom”.
- Adding the same folder again focuses the existing project.

### 12.4 Project detail

```text
MyApp   [Android Gradle]   C:\src\myapp                    [Open folder] [Refresh]
Run: [appDebug ▾] on [POCO F1 ▾] [Mirror ✓]   [Run F5]   [More ▾]
Tabs: Overview | Run configs | Modules & variants | Toolchain | Sessions | Settings

Health
  ✓ Gradle 8.14   ✓ JDK 17 (JAVA_HOME)   ✓ SDK 35   ✓ adb 37   ✓ scrcpy 5.0

Modules
  app            com.example.myapp      application
  core           com.example.core       library
  feature:login

Variants
  devDebug   prodDebug   prodRelease

Last run
  appDebug · POCO F1 · succeeded · 12.4s · 2 min ago        [Open session]

Quick actions
  [Build only] [Install APK] [Run tests] [Open logs] [Edit config]
```

- Capability badge explains supported actions.
- Settings tab contains project overrides.
- Sessions tab filters history to this project.

### 12.5 Devices

```text
POCO F1   ● Online   Xiaomi · Android 15 · API 35 · arm64-v8a   [Mirror] [More ▾]
┌──────────────────────────────┬───────────────────────────────────────────────┐
│ [live thumbnail / mirror]    │ Overview | Apps | Files | Shell | Ports | Perf │
│                              │                                               │
│ [Screenshot] [Record]        │ Battery 78% · 1080×2246 · 440 dpi              │
│ [Rotate] [Volume] [Power]    │ IP 192.168.1.42 · USB · transport 1            │
│                              │ [Wireless] [Reboot] [Device report]               │
└──────────────────────────────┴───────────────────────────────────────────────┘
```

- Device cards show state/model/Android/battery.
- Device tabs are reorderable/hideable.
- Toolbox actions live in tabs and the “More” menu.

### 12.6 Session workspace

```text
MyApp / appDebug / POCO F1                    [Pop out mirror] [Stop] [More ▾]
Steps: ✓ Build 12.4s   ✓ Install 2.1s   ✓ Launch 0.8s   ● Logcat 00:42
┌──────────────────────────────────┬───────────────────────────────────────────┐
│                                  │ [All] [Build] [Logcat] [Activity]         │
│          device mirror           │ [search…] [level ▾] [tag ▾] [pause] [⋯]   │
│          (scrcpy)                │ 10:32:01.123 I/ActivityManager: ...        │
│                                  │ 10:32:01.456 E/AndroidRuntime: FATAL ...   │
│                                  │ 10:32:01.457 E/AndroidRuntime:   at ...    │
│                                  │ ...                                       │
│ [Screenshot] [Record] [Rotate]   │                                           │
│ [Volume] [Power] [More ▾]        │ [Jump to latest ↓]                        │
└──────────────────────────────────┴───────────────────────────────────────────┘
Crash banner: FATAL EXCEPTION · Restart app · Copy stack trace
```

- Multi-device runs add device tabs above the workspace.
- Aggregate status and per-device progress.
- Failed devices can be retried.
- Layout (split orientation, pane sizes, visible tabs) is customizable.

### 12.7 Settings

```text
Settings
[Search settings…]                    Scope: [Global ▾]   [Advanced mode]
┌ Categories ──────────────┬──────────────────────────────────────────────┐
│ General                  │ Appearance                                   │
│ Appearance               │ Theme        [System ▾]                     │
│ Tools                    │ Accent       [System ▾]  [swatch]           │
│ Devices                  │ Density      [Comfortable ▾]                │
│ Projects & detection     │ Background   [Mica ▾]                       │
│ Run & build              │ Motion       [Full ▾]                       │
│ Logs                     │ UI font      [Segoe UI Variable ▾]          │
│ Screen                   │ Log font     [Cascadia Mono ▾]  14 px       │
│ Tests                    │                                              │
│ Notifications            │ [Reset section]                              │
│ Keyboard                 │                                              │
│ Features                 │                                              │
│ Plugins                  │                                              │
│ Advanced                 │                                              │
└──────────────────────────┴──────────────────────────────────────────────┘
```

- Scope switcher: Global / Project / Device / Run config.
- Source badges and reset arrows.
- Basic/Advanced/Expert modes.
- “Edit as JSON” with schema validation, diff, import/export.
- “Test” buttons for tools, detectors, scripts.
- “Preview plan” / “Dry run”.
- “Restart required” badges.
- Configuration health panel.

### 12.8 Extensions

```text
Extensions
[Installed] [Browse catalog] [Updates]        [Check for updates] [Sources…]

┌──────────────────────────────────────────────────────────────────────┐
│ My Plugin  1.0.0  by Example                 ● Enabled   [Settings] ⋯│
│ Detector · Device action · Panel · Renderer                          │
│ Healthy · loaded in 42 ms · 0 errors                                 │
└──────────────────────────────────────────────────────────────────────┘
```

- Installed/Browse/Updates tabs.
- Trust dialog on install/update.
- Restart-required flow.
- Rollback on failed update.
- Plugin-contributed pages appear in nav with a plugin badge.
- Missing-plugin banners in projects.

### 12.9 Command palette

```text
┌─────────────────────────────────────────────────────────────┐
│ > run                                                    ✕  │
├─────────────────────────────────────────────────────────────┤
│ ▶ Run MyApp · appDebug on POCO F1                    F5     │
│ ▶ Run MyApp · appDebug on all devices                      │
│ ▶ Mirror POCO F1                                            │
│ ▶ Take screenshot · POCO F1                                 │
│ ▶ Open project MyApp                                        │
└─────────────────────────────────────────────────────────────┘
```

Shortcuts:

| Action | Default |
|---|---|
| Run | `F5` |
| Stop | `Shift+F5` |
| Rebuild & restart | `Ctrl+Shift+F5` |
| Command palette | `Ctrl+K` |
| Focus logs | `Ctrl+L` |
| Mirror | `Ctrl+Shift+M` |
| Search settings | `Ctrl+F` in Settings |
| Toggle nav rail | `Ctrl+B` |
| Next/previous device | `Ctrl+Tab` / `Ctrl+Shift+Tab` |
| Screenshot | `Ctrl+Shift+S` |
| Record toggle | `Ctrl+Shift+R` |

All shortcuts are remappable with conflict detection.

### 12.10 Dialogs

**Wireless pairing**

```text
Pair device over Wi-Fi
1. Developer options → Wireless debugging → Pair device with pairing code.
2. Enter IP, port, and 6-digit code.
   IP [192.168.1.42]  Port [37123]  Code [123456]   [Pair]
Discovered: _adb-tls-pairing._tcp · 192.168.1.42:37123
[Cancel]
```

**APK drop**

```text
Drop APK to install
  com.example.myapp 1.2.3 (42)
  minSdk 24 · targetSdk 35 · arm64-v8a
  Devices: [POCO F1 ✓] [Pixel_8 ✓]
  [Install & launch] [Install only] [Cancel]
```

**Emulator**

```text
Pixel_8 · API 35 · arm64 · 1080×2400        [Launch ▾] [Stop]
Launch options: Cold boot · Wipe data · Headless · Snapshot
```

**Plugin trust**

```text
Install plugin?
My Plugin 1.0.0 by Example
Capabilities: project detection, shell commands, UI page
SHA-256: 9f2c…
[ ] I trust this publisher and understand plugins run with full app privileges
[Cancel] [Install]
```

### 12.11 Onboarding

Three steps, skippable:

1. **Tools** — scan SDK/adb/JDK/scrcpy/Flutter/Node; show status and “Locate/How to install”.
2. **Project** — add folder or APK; review detection.
3. **Device** — connect USB, authorize, or pair wirelessly.

Finish → Home with a quick-run card. Environment health remains accessible in Settings.

### 12.12 Design system

- **Theme:** dark default; light/system optional; Mica with fallback.
- **Typography:** Segoe UI Variable; Cascadia Mono/Consolas for logs.
- **Spacing:** 4/8/12/16/24/32; 8px grid.
- **Cards:** 8px radius, 1px stroke, 12px padding.
- **Colors:** Fluent theme resources; semantic success/warning/error/info; log level colors with accessible contrast.
- **Icons:** Segoe Fluent Icons, 16/20px.
- **Density:** comfortable/compact; compact for logs/lists.
- **Motion:** subtle 150–250 ms; respect reduced motion.
- **Responsive:** nav collapses under 900px; session split stacks under 1000px.
- **Accessibility:** keyboard navigation, focus visuals, screen reader names, high contrast, 200% zoom, color never the only signal.
- **Localization:** resource strings from day one; English UI initially.

### 12.13 Interaction rules

- **Run** — `F5` runs the last-used config on selected devices; no devices opens the picker.
- **Cancel** — stop build/install immediately; optionally stop the app.
- **Crash** — banner + restart actions; session stays alive.
- **Logs** — manual scroll pauses auto-scroll; “Jump to latest (N)”; search debounced.
- **Mirror** — one per device; reopen focuses; fallback external window with explanation.
- **Destructive actions** — confirm with device name and consequence.
- **Errors** — InfoBar with primary action, details, copy diagnostics.
- **Session close** — confirm if active.
- **Refresh detection** — no Gradle; preserve edits; show diff.
- **Plugin install/update** — trust prompt; restart required; rollback on failure.

### 12.14 Empty, loading, and error states

- Empty: icon, title, one sentence, primary action.
- Loading: skeleton rows for lists; progress with step text for long operations.
- Error: severity, summary, action, expandable details, copy.
- Unauthorized device: instructions and retry.
- Missing tool: locate/install guide.
- Unsupported capability: badge + explanation, not a dead button.
- Missing plugin: install/open-without banner.


## 13. Configuration and customization

### 13.1 Goals

- Every feature has a default.
- Every default is visible and resettable.
- Every layer can override.
- Power users can edit validated JSON without forking the app.
- Teams can share project configuration without sharing machine paths or secrets.
- Customization never requires a restart unless the change is structurally unsafe.

### 13.2 Configuration principles

1. **Defaults are data, not magic numbers.** An embedded `defaults.json` defines every setting, its type, description, and default value.
2. **Layered resolution.** Global → profile → project → run config → device → session → CLI.
3. **Source map.** The UI can say “this value comes from Global” or “overridden in this run config”.
4. **Typed UI + raw JSON.** Known settings have typed models; unknown/plugin keys are preserved as JSON.
5. **Progressive disclosure.** Basic/Advanced/Expert modes, search, presets, and reset.
6. **Local-first.** No cloud dependency. Optional repo sharing is explicit.
7. **Validated and migratable.** JSON Schema, migrations, backups, quarantine.
8. **Safe.** Scripts and plugins require trust; secrets are encrypted; destructive actions confirm.
9. **Observable.** Diagnostics show effective settings and their sources.

### 13.3 Scopes and precedence

| Scope | Storage | Typical contents |
|---|---|---|
| **Built-in defaults** | Embedded `defaults.json` | Every setting’s baseline |
| **Global** | `%LocalAppData%\DroidDeck\settings.json` | Theme, tools, logs, performance, shortcuts, feature flags, plugin policy |
| **Profile** | `profiles\<name>.json` | Named workflow overrides (theme, tools, defaults, visibility) |
| **Project** | `projects\<id>.json` or optional `<repo>\.droiddeck\project.json` | Detection overrides, run configs, toolchain, log filters, screen defaults |
| **Run configuration** | Inside project config | Build/install/launch steps, variants, env, screen, tests |
| **Device override** | `devices\<serial>.json` or device group | Screen defaults, install flags, custom actions, tags |
| **Session override** | In-memory; optional “Save as default” | One-off changes for this run |
| **CLI / environment** | Command line / env vars | Automation, diagnostics, CI |

Precedence (8 Scopes — splitting Team-Shared vs Local Project Overrides):

```text
1. Built-in Defaults (embedded defaults.json)
2. Global User Settings (%LocalAppData%\DroidDeck\settings.json)
3. Active Profile (%LocalAppData%\DroidDeck\profiles\<name>.json)
4. Team-Shared Project Config (<repo>\.droiddeck\project.json — opt-in)
5. Local Project Overrides (%LocalAppData%\DroidDeck\projects\<id>.json)
6. Run Configuration Overrides (inside project config)
7. Device Overrides (All Devices → Device Group → Specific Device Serial)
8. Session / CLI Ephemeral Overrides (--set key=value / DROIDDECK__* env vars)
```

Splitting **Team-Shared Project Config** (Layer 4) from **Local Project Overrides** (Layer 5) allows teams to commit shared run configurations and log filters to `<repo>\.droiddeck\project.json` while each developer keeps personal overrides (such as local JDK paths or mirror preferences) in `%LocalAppData%\DroidDeck\projects\<id>.json` without dirtying `git status`. Furthermore, any executable script or shell step inside `<repo>\.droiddeck\project.json` requires a one-time **Repository Script Trust** approval (pinned by SHA-256 in `trust.json`) before execution.

Example:

```text
screen.maxSize
  Default      1024
  Global       1280
  Project       720
  Run config   1080
  Device       1024   ← effective
```

The UI shows the effective value, the source badge, an override control, and a reset arrow.

### 13.4 What is configurable

| Domain | Examples |
|---|---|
| **App shell** | Theme, accent, density, Mica/acrylic/solid, motion, fonts, language, startup page, window state, single-instance behavior, status-bar items |
| **Layout** | Home widgets, nav items, detail tabs, toolbar actions, session split orientation, pane sizes, table columns |
| **Keyboard** | Every command remappable, chords, conflict detection, reset, import/export |
| **Tools** | Android SDK, adb, aapt2, emulator, JDK(s), Gradle, Flutter, Node/npm/npx, scrcpy, Unity, environment variables |
| **Toolchain behavior** | Gradle args, offline mode, daemon, configuration cache, JDK per project, Flutter/Node args |
| **Devices** | Groups/tags, wireless auto-reconnect, install flags, uninstall policy, screen defaults, emulator launch options, thumbnail FPS, custom actions |
| **Projects** | Detector priority/enablement, ignore folders, scan depth/timeout, monorepo handling, auto-refresh, capability overrides |
| **Run pipeline** | Build/install/launch commands, variants, APK globs, applicationId/activity overrides, pre/post steps, conditions, retries, timeouts, failure policy, concurrency |
| **Logs** | Fonts, columns, timestamps, wrap, max lines, saved filters, highlight rules, parsers, crash patterns, export format, retention, redaction |
| **Screen** | scrcpy path/custom args, codec, size, FPS, bitrate, audio, clipboard, stay-awake, embedded/external/pop-out, window behavior, recording, screenshots |
| **APK** | Inspect fields, install flags, grant permissions, launch after install, split APK handling, downgrade/signature policy |
| **Tests** | Runner, tasks, args, result globs, timeouts, retries, parallel devices, rerun failed, report format |
| **Performance** | Sampling interval, metrics shown, chart window, gfxinfo reset, CSV export |
| **UI inspector** | Refresh behavior, invisible nodes, bounds overlay color, tap actions, max depth |
| **Hot reload** | Enable/disable, command, key mapping, reload-on-save, debounce |
| **Notifications** | Per-event matrix: in-app/toast, always/when unfocused/never, quiet hours, sound |
| **Feature flags** | Experimental native screen, plugin system, auto-rerun, new log viewer, stack visibility, toolbox tab visibility |
| **Plugins** | Enable/disable, developer mode, safe mode, trust hashes, catalog sources, update policy, plugin settings |
| **Catalog** | Sources, auth tokens, prerelease, auto-check cadence, offline mode |
| **Renderers** | Default renderer per kind, enabled renderers, plugin renderer visibility |
| **Themes** | Built-in/custom themes, color tokens, log colors, fonts, contrast validation |
| **Secrets** | Named secrets in DPAPI; `${secret:NAME}` placeholders |
| **Advanced** | Developer mode, verbose logging, adb command logging, performance overlay, config folder access |

### 13.5 Settings UI

The Settings hub uses:

- Search across labels, descriptions, keys, and synonyms.
- Scope switcher: Global / Project / Device / Run config.
- Source badges and reset arrows.
- Basic / Advanced / Expert modes.
- “Modified” dots.
- “Edit as JSON” with schema validation and diff.
- “Test” buttons for tool paths, custom detectors, scripts, and command templates.
- “Preview plan” / “Dry run” for run configs and custom steps.
- “Restart required” badges.
- “Reset section” and “Reset all”.
- “Open config folder” / “Reveal in Explorer”.
- Configuration health panel: invalid paths, deprecated keys, missing tools, schema warnings.

#### Per-project settings

Project detail gains a **Settings** tab:

```text
MyApp · Settings
Tabs: General | Detection | Run configs | Logs | Screen | Steps | Toolchain | Sharing

Detection
  [x] Use global detection rules
  Detector priority: Gradle > Flutter > RN > MAUI > KMP > Capacitor > Cordova > Unity
  Ignore folders: build, .gradle, node_modules, .git
  Auto-refresh on file changes: [x]  debounce 500 ms

Toolchain
  JDK: [Project default ▾]
  Gradle args: [--offline]
  SDK: [Global ▾]
  scrcpy: [Global ▾]

Sharing
  [ ] Write .droiddeck/project.json for team sharing
  [Export project config] [Import…]
```

#### Run configuration editor

```text
Run configuration: appDebug
[General] [Build] [Artifact] [Install] [Launch] [Logs] [Screen] [Devices] [Steps] [Advanced]

Build
  Command   gradlew.bat
  Task      :app:assembleDebug
  Variant   [devDebug ▾]
  Env       ANDROID_SERIAL=${deviceSerial}
  Timeout   10 min
  Retries   0

Steps
  + Add step   [Pre-build] [Post-build] [Pre-install] [Post-launch] [On failure]
  ✓ Grant camera permission   adb shell pm grant ${applicationId} android.permission.CAMERA
  ✓ Deep link smoke test      adb shell am start -a android.intent.action.VIEW -d ${deepLink}

[Preview plan] [Test command] [Duplicate] [Export] [Save]
```

### 13.6 Customize mode

A **Customize** toggle (title bar or command palette) reveals:

- Drag handles for home widgets and nav items.
- Show/hide toggles for project/device/session tabs.
- Toolbar action editor.
- Status-bar item editor.
- Table column editor.
- “Reset layout”.

Normal usage stays clean; customization is explicit.

### 13.7 Profiles and presets

**Profiles** are named sets of global settings:

- Theme, tool paths, run defaults, screen defaults, feature visibility, plugin policy.
- Switch from the title bar or command palette.
- Duplicate, export, import, reset.
- Example profiles: “Work”, “Personal”, “QA”, “Release”, “Pixel testing”.

**Presets** are smaller reusable bundles:

- Install flags.
- Screen options.
- Log filters.
- Device actions.
- Run steps.
- Renderer defaults.
- Apply from dropdowns or “Save current as preset”.

Profiles and presets can include plugin settings, subject to export rules.

### 13.8 Feature visibility and flags

```text
Features
Stacks:      [✓] Gradle [✓] Flutter [✓] RN [✓] MAUI [✓] KMP [✓] Capacitor [✓] Cordova [✓] Unity [✓] APK [✓] Custom
Toolbox:     [✓] Apps [✓] Files [✓] Shell [✓] Ports [✓] Perf [✓] Inspector
Home:        [✓] Quick run [✓] Active sessions [✓] Devices [✓] Recent [✓] Environment
Experimental:[ ] Native screen [ ] Plugin system [ ] Auto re-run
```

Rules:

- Hiding a feature removes it from navigation/pickers but does not delete its configuration.
- Developer mode unlocks experimental toggles.
- Experimental features show a badge and may require restart.
- Safe mode disables all experimental features and plugins.
- Diagnostics lists enabled flags, visibility, and overrides.
- Feature gating uses `IFeatureService` in both UI and services.

### 13.9 Declarative customization

#### Custom detectors

```json
{
  "id": "my-stack",
  "name": "My Android Stack",
  "priority": 100,
  "markers": ["myapp.config.json"],
  "kind": "custom",
  "runConfigs": [
    {
      "name": "Debug",
      "build": {
        "command": "mycli build --project ${projectDir}",
        "cwd": "${projectDir}"
      },
      "artifact": { "apkGlob": "build/**/*.apk" },
      "launch": { "package": "${package}", "activity": "${activity}" }
    }
  ]
}
```

- “Test detection” against a folder.
- Shows matched evidence.
- Validates JSON.
- Priority and enable/disable.
- Built-in detectors can be disabled/reordered; their internals are not editable.

#### Custom steps and device actions

```json
{
  "name": "Grant camera",
  "type": "adb-shell",
  "command": "pm grant ${applicationId} android.permission.CAMERA",
  "when": "device.api >= 23",
  "continueOnError": true
}
```

Step types: process, adb-shell, adb-install, wait-for-device, wait, log, script.
Each supports cwd, env, timeout, retries, failure policy, and output capture.

Device actions support:

- Name, icon, command/script.
- Confirm flag.
- Output panel.
- Toolbar visibility/order.
- Placeholders and conditions.

#### Placeholders

| Placeholder | Meaning |
|---|---|
| `${projectDir}` | Project root |
| `${deviceSerial}` | adb serial |
| `${deviceModel}` | Device model |
| `${apiLevel}` | Android API level |
| `${variant}` | Selected build variant |
| `${apkPath}` | Located APK path |
| `${applicationId}` | Package name |
| `${launcherActivity}` | Launcher activity |
| `${sessionId}` | Current session ID |
| `${timestamp}` | ISO timestamp |
| `${env:NAME}` | Environment variable |
| `${secret:NAME}` | DPAPI secret |

All placeholders are documented, validated, and previewed before running. Secrets are never logged or exported.

#### Conditions

A small, validated expression language:

```text
device.api >= 30
device.isEmulator == false
project.kind == "gradle"
variant == "debug"
```

No arbitrary code in JSON. Scripts are explicit, separately trusted, and confirmed when destructive.

#### Also declarative

- Log parsers and highlight rules (regex).
- Saved filters and crash patterns.
- Test runners (command + JUnit XML path).
- Themes (color tokens) and keybindings.
- Device groups and tags.
- Notification rules.
- Toolbar actions and home widgets.
- Renderer defaults.

### 13.10 Storage and portability

```text
%LocalAppData%\DroidDeck\
  settings.json
  profiles\<name>.json
  projects\<id>.json
  devices\<serial>.json
  themes\<name>.json
  keybindings.json
  presets\<name>.json
  plugins.json
  plugins\
    <plugin-id>\
      <version>\
      settings.json
      data\
      logs\
  catalog\
    cache\<source-id>.json
    packages\<sha256>.ddplugin
  trust.json
  sessions\<...>\
  logs\
```

Optional team-shared:

```text
<repo>\.droiddeck\project.json
<repo>\.droiddeck\run-configs\*.json
```

Rules:

- Local-first by default.
- Repo sharing is opt-in per project.
- Shared config excludes machine paths, secrets, session history, trust hashes, and plugin binaries.
- Import/export supports scope selection, merge vs replace, diff preview, and backup.
- `$schema` fields provide editor IntelliSense.
- Unknown keys are preserved.
- Atomic writes, backups, corrupt-file quarantine, schema migrations.
- Portable mode via `--data-dir` or a marker file.

### 13.11 Configuration architecture

```csharp
public interface IConfigurationService
{
    ConfigSnapshot Current { get; }
    Task<T> GetAsync<T>(ConfigKey<T> key, ConfigScope scope, CancellationToken ct);
    Task SetAsync<T>(ConfigKey<T> key, T value, ConfigScope scope, CancellationToken ct);
    Task ResetAsync(ConfigKey key, ConfigScope scope, CancellationToken ct);
    ConfigSource GetSource(ConfigKey key, ConfigScope scope);
    event EventHandler<ConfigChangedEventArgs> Changed;
}

public interface IFeatureService
{
    bool IsEnabled(string featureId);
    bool IsVisible(string featureId);
    event EventHandler<FeatureChangedEventArgs> Changed;
}

public interface IProfileService { /* list/switch/duplicate/export/import */ }
public interface IPresetService { /* list/apply/save/export/import */ }
public interface ITemplateEngine { /* expand placeholders + evaluate conditions */ }
public interface ICommandRegistry { /* commands + keybindings */ }
public interface IThemeService { /* tokens, fonts, contrast */ }
public interface ISecretStore { /* DPAPI */ }
public interface IConfigImportExport { /* scopes, diff, merge, backup */ }
```

Implementation notes:

- Immutable `ConfigSnapshot` with effective values and a source map.
- Deep merge of JSON scopes; arrays replace by default.
- Change events debounced; only affected services reload.
- Validation on save, not on hot paths.
- Settings search index built lazily.
- Feature gating in UI and services.
- Theme service validates contrast.
- Command registry powers palette and keybindings.
- Template engine uses a strict allowlist.
- Secrets never appear in logs/exports.

### 13.12 Import, export, and sharing

- Export selected scopes.
- Merge or replace.
- Diff preview before applying.
- Automatic backup.
- Secrets excluded by default.
- Machine-specific paths optional.
- Plugin binaries never included; plugin IDs/versions and settings can be.
- Trust hashes are machine-local and not exported by default.
- Import validates schema and shows errors with JSON paths.
- Shared project config can reference required plugins; missing plugins show a banner and never auto-install.

### 13.13 CLI and environment overrides

```text
--data-dir <path>
--profile <name>
--config <path>
--set key=value
--safe-mode
--log-level <level>
```

Environment overrides:

```text
DROIDDECK__LOGGING__LEVEL=Debug
DROIDDECK__PLUGINS__SAFEMODE=true
```

Used for automation, diagnostics, and CI. Secrets are not passed through command-line args by default.

### 13.14 Configuration performance and testing

Performance:

- Cached config resolve < 1 ms.
- Settings page load < 200 ms.
- Config change propagation < 100 ms.
- No file I/O on log/render hot paths.

Tests:

- Precedence matrix across all scopes.
- Device override specificity: serial > group > all.
- Schema migrations and unknown-key preservation.
- Import/export round-trip; secrets excluded.
- Corrupt config quarantine and recovery.
- Feature flag gating, visibility, developer mode, safe mode.
- Keybinding conflict detection.
- Theme contrast validation.
- Custom detector/step/action fixtures.
- Placeholder and condition evaluation.


## 14. Plugin SDK

### 14.1 Goals

- Let plugins add real product features, not just scripts.
- Keep the API stable, documented, versioned, and testable.
- Use the same contracts for built-in features.
- Make plugin failures non-fatal.
- Be explicit that in-process plugins are trusted code.

### 14.2 Assemblies and versioning

```text
DroidDeck.Plugin.Abstractions   (core contracts, DTOs, manifest, no WinUI)
DroidDeck.Plugin.UI             (WinUI UI contracts)
DroidDeck.Plugins               (host: loader, trust, catalog, registry)
```

Versioning:

- Semantic versioning for the API: `1.0`, `1.1`, `2.0`.
- Plugin manifest declares `apiVersion`, `minHostVersion`, optional `maxHostVersion`.
- Host refuses incompatible majors, warns on minor mismatch, explains in plugin manager.
- Breaking API changes only in major versions with a migration guide.
- Shared contract assemblies are resolved from the host to preserve type identity.

### 14.3 Manifest

```json
{
  "id": "com.example.myplugin",
  "name": "My Plugin",
  "version": "1.0.0",
  "author": "Example",
  "description": "Adds a custom detector, device action, panel, and renderer.",
  "apiVersion": "1.0",
  "minHostVersion": "1.0.0",
  "entryAssembly": "MyPlugin.dll",
  "entryType": "MyPlugin.Plugin",
  "capabilities": [
    "project-detector",
    "run-step",
    "device-action",
    "ui-page",
    "ui-panel",
    "renderer",
    "theme"
  ],
  "dependencies": [
    { "id": "com.example.shared", "minVersion": "1.0.0" }
  ],
  "icon": "icon.png",
  "homepage": "https://example.com",
  "license": "MIT"
}
```

Manifest validation:

- Required fields and semver.
- Unique plugin ID.
- Entry assembly/type exists.
- API/host compatibility.
- Dependencies present and compatible.
- Package path safety (no zip slip, no symlinks).
- Size limits.
- Capability names from a known set (unknown capabilities warn, not fail).

### 14.4 Entry point and context

```csharp
public interface IPlugin
{
    Task InitializeAsync(IPluginContext context, CancellationToken ct);
    Task ShutdownAsync(CancellationToken ct);
}

public interface IPluginContext
{
    IPluginInfo Info { get; }
    ILogger Logger { get; }
    IPluginSettings Settings { get; }
    IDeviceService Devices { get; }
    IProjectService Projects { get; }
    ISessionService Sessions { get; }
    ILogService Logs { get; }
    ICommandRegistry Commands { get; }
    IEventBus Events { get; }
    IPluginRegistry Extensions { get; }
    string DataDirectory { get; }
}
```

- `ISessionService` and `ILogService` are plugin-facing read-only facades over `IRunSessionService` and `ILogcatService`.
- The service interfaces exposed by `IPluginContext` are defined in `DroidDeck.Plugin.Abstractions` and implemented by Core.
- All service methods are async and expose stable, read-only DTOs mapped from Core models.
- Plugins never receive mutable internal models.
- Plugin UI factories run on the UI thread; plugin background work must be async/cancellable.
- Event subscriptions must be disposed in `ShutdownAsync`.

### 14.5 Extension points

| Area | Contracts |
|---|---|
| **Projects** | `IProjectDetector`, `IStackAdapter`, `IArtifactLocator`, `IRunStep`, `ITestRunner` |
| **Devices** | `IDeviceAction`, `IDeviceTab`, `IDeviceMonitor` |
| **Sessions** | `ISessionPanel`, `IToolbarAction`, `ILogParser`, `IHighlightRuleProvider`, `IExportProvider` |
| **UI** | `INavigationPage`, `IHomeWidget`, `IProjectTab`, `ISettingsSection`, `IPluginDialog`, `IThemeProvider` |
| **Commands** | `ICommand` (palette + keybinding), `ICommandProvider` |
| **Settings** | JSON-schema settings and/or custom settings UI |
| **Notifications** | `INotificationChannel` (optional) |
| **Renderers** | `IRenderer` for log detail, charts, file preview, test results, data views |

Registration:

```csharp
context.Extensions.AddProjectDetector<MyDetector>();
context.Extensions.AddStackAdapter<MyAdapter>();
context.Extensions.AddRunStep<MyStep>();
context.Extensions.AddDeviceAction<MyAction>();
context.Extensions.AddTestRunner<MyRunner>();
context.Extensions.AddLogParser<MyParser>();
context.Extensions.AddTheme<MyTheme>();
context.Extensions.AddCommand(new MyCommand());

context.Extensions.AddNavigationPage<MyPage>("My Plugin", icon: "icon.png");
context.Extensions.AddHomeWidget<MyWidget>(order: 50);
context.Extensions.AddProjectTab<MyProjectTab>("My Tab");
context.Extensions.AddDeviceTab<MyDeviceTab>("My Tab");
context.Extensions.AddSessionPanel<MySessionPanel>("My Panel");
context.Extensions.AddToolbarAction<MyToolbarAction>();
context.Extensions.AddSettingsSection<MySettingsSection>();
context.Extensions.AddRenderer<MyRenderer>();
```

Built-in implementations use the same contracts:

- `GradleDetector : IProjectDetector`
- `GradleAdapter : IStackAdapter`
- `AdbInstallStep : IRunStep`
- `ScreenshotAction : IDeviceAction`
- `GradleTestRunner : ITestRunner`
- `DefaultTheme : IThemeProvider`
- `RunCommand : ICommand`
- Built-in log/file/chart/test renderers

This keeps the SDK complete and continuously tested.

### 14.6 Lifecycle

1. **Install** — user selects `.ddplugin` or a folder (developer mode).
2. **Validate** — manifest, ID, version, entry point, dependencies, package safety.
3. **Trust** — show publisher, version, capabilities, SHA-256, signature status.
4. **Extract** — to `%LocalAppData%\DroidDeck\plugins\<id>\<version>\`.
5. **Enable** — restart required for a clean load.
6. **Load** — `AssemblyLoadContext` per plugin; shared contracts from host; dependencies isolated.
7. **Initialize** — async; register extensions.
8. **Run** — host wrappers isolate exceptions.
9. **Disable/update/uninstall** — restart required; previous version retained for rollback.
10. **Safe mode** — all plugins disabled for troubleshooting.

### 14.7 Loading and isolation

- One `AssemblyLoadContext` per plugin.
- `AssemblyDependencyResolver` loads plugin-private dependencies.
- Shared contract assemblies resolve from the host.
- Native dependencies allowed under `runtimes/win-x64` but not sandboxed.
- UI plugins must target the host’s WinUI/Windows App SDK version.
- Assemblies remain loaded after disable; restart is required to unload.
- Plugin load failures are isolated and reported.

### 14.8 Failure isolation

- Every plugin call is wrapped in try/catch with plugin ID and operation context.
- Repeated failures disable the plugin and show an error panel.
- UI factory failures render a fallback error panel.
- Renderer failures fall back to the built-in renderer.
- Detector/step/action failures are reported in the session/project UI.
- A plugin cannot crash the host process through normal extension calls (though full-trust code can still do anything).

### 14.9 Plugin settings

- `settings.schema.json` (safe JSON Schema subset) or custom settings UI.
- Host renders bool, string, number, enum, path, secret, list, object.
- Stored in `plugins/<id>/settings.json`.
- Secrets use the host DPAPI store.
- Plugin data in `plugins/<id>/data/`.
- Settings included in profiles/presets and export/import, excluding secrets.
- Per-project plugin settings are not a separate host scope in v1; plugins can store project-keyed data in their data directory.

### 14.10 UI extensions

- Navigation pages with title/icon.
- Home widgets with order/visibility.
- Project tabs, device tabs, session panels.
- Toolbar actions with context.
- Settings sections.
- Plugin dialogs.
- Command palette entries.
- Customize mode can reorder/hide plugin UI.

UI contract requirements & the **Host UI Component Kit**:

Because WinUI 3 XAML `.xbf` and `.pri` resource maps compiled inside a secondary `AssemblyLoadContext` can fail to resolve at runtime without host registration, `DroidDeck.Plugin.UI` provides a two-tier UI authoring model:
1. **Tier 1 — Host UI Component Kit (Recommended, Zero XAML Packaging Friction):**
   - Plugins compose native WinUI 3 views in C# using host-instantiated, pre-themed building blocks: `PluginPageLayout`, `PluginCard`, `PluginForm` (auto-bound to `IPluginSettings`), `PluginDataGrid`, `PluginTreeView`, `PluginCodeViewer`, `PluginMetricChart`, and `PluginInfoBar`.
2. **Tier 2 — Custom XAML `UserControl` + `PluginXamlMetadataProvider`:**
   - Advanced plugins shipping compiled XAML controls expose `IXamlMetadataProvider`, which the host bridges through a composite `PluginXamlMetadataProvider` on `App.xaml.cs`.
- All plugin UI views must use host theme resources, support high contrast, dispose event subscriptions on close, never block the UI thread, and respect `CancellationToken`s.
- **Circuit Breaker (`PluginFaultBoundary`):** If any plugin extension throws 3 unhandled exceptions within 60 seconds, the host automatically pauses that plugin for the session, falls back to built-in behavior, and surfaces a diagnostic banner in the Plugin Manager.

### 14.11 Commands and keybindings

- Plugin commands register with the command registry.
- Appear in the command palette.
- Remappable with conflict detection.
- Context-aware enablement.
- Icons, keywords, and descriptions for search.

### 14.12 Themes

- Plugins can contribute `IThemeProvider` implementations.
- Theme tokens: background, surface, card, text, accent, success, warning, error, info, log levels.
- Contrast validation; high contrast support.
- Import/export theme JSON.
- User selects default theme and log color scheme.

### 14.13 Log parsers and highlight rules

- Plugin log parsers run only for selected sources and can be disabled.
- They run on the background log pipeline, not the UI thread.
- Highlight rules are regex/string based with priority.
- Host measures parser throughput; slow parsers can be disabled.
- No plugin log parser runs on the hot path unless explicitly enabled.

### 14.14 Test runners

- Plugin test runner implements `ITestRunner`.
- Receives project/device/session context.
- Returns structured results and JUnit XML.
- Results UI supports plugin renderers.

### 14.15 Configuration and sharing

- Plugin system flags: enabled, developer mode, safe mode.
- Project config can declare required plugins:

```json
{
  "requiredPlugins": [
    { "id": "com.example.myplugin", "minVersion": "1.2.0", "reason": "custom detector" }
  ]
}
```

- Missing plugin → banner with “Install plugin…” and “Open without it”.
- Never auto-install from a repo.
- Run configs contributed by plugins store `pluginId`, `stepType`, and config.
- Missing plugin step → unavailable, run blocked with a clear fix.
- Export/import includes plugin IDs/versions/settings, never binaries, auth tokens, or trust hashes.

### 14.16 Plugin manager UX

```text
Extensions
[Installed] [Browse catalog] [Updates]        [Check for updates] [Sources…]

┌──────────────────────────────────────────────────────────────────────┐
│ My Plugin  1.0.0  by Example                 ● Enabled   [Settings] ⋯│
│ Detector · Device action · Panel · Renderer                          │
│ Healthy · loaded in 42 ms · 0 errors                                 │
└──────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────┐
│ Broken Plugin  0.9.0  by Example             ● Disabled  [Logs] ⋯   │
│ Failed to load: missing dependency com.example.shared 1.0+          │
└──────────────────────────────────────────────────────────────────────┘
```

Actions:

- Install from file/folder.
- Enable/disable (restart).
- Update/rollback.
- Settings.
- Logs.
- Open data folder.
- Copy diagnostics.
- Uninstall (keep/delete data).
- Restart in safe mode.
- View capabilities, hash, signature, source.

### 14.17 Developer experience

- `dotnet new install DroidDeck.Plugin.Templates`
- `dotnet new droiddeck-plugin -n MyPlugin`
- Template includes manifest, plugin class, sample detector/action/panel/renderer, settings schema, tests.
- `--plugin-dev` loads an unpacked plugin folder.
- Rebuild + restart to reload.
- Sample plugins:
  - Detector + adapter.
  - Device action.
  - Navigation page + home widget.
  - Log parser + highlighter.
  - Theme.
  - Test runner.
  - Log detail renderer.
  - Chart renderer.
  - File preview renderer.
  - Failing plugin.
  - Version-mismatch plugin.
  - Dependency plugin.
- Docs: overview, getting started, manifest, extension points, UI, settings, security, versioning, packaging, samples.
- API reference generated from XML comments.

### 14.18 Plugin performance

- Read manifest metadata without loading assemblies.
- Load enabled plugins asynchronously at startup.
- 5 enabled plugins add < 300 ms startup.
- Create UI pages/panels/renderers on demand; dispose when closed.
- No plugin calls on the log hot path unless explicitly enabled.
- Measure load time, UI creation time, parser throughput, and errors.
- Plugin manager shows health and performance.

### 14.19 Plugin testing

- Manifest validation, zip safety, duplicate IDs, version mismatch, missing dependencies.
- Shared contract type identity across load contexts.
- Trust prompt/hash pinning, capability changes, safe mode.
- Enable/disable/update/rollback restart flows.
- Failure isolation for detectors, steps, actions, commands, UI factories, renderers.
- Settings schema rendering, secrets, export/import.
- Missing-plugin project behavior.
- Sample plugins built and tested in CI.
- Performance budgets.

### 14.20 Plugin limitations (v1)

- In-process full trust; no sandbox.
- No automatic updates.
- No marketplace hosting.
- No hot unload; restart for enable/disable/update/uninstall.
- No per-project plugin settings scope (plugin-managed data instead).
- Native dependencies are allowed but unsupported for isolation.
- Plugins must target the host WinUI/Windows App SDK version.

### 14.21 Future plugin work

- Out-of-process host for untrusted/non-UI plugins.
- Permission broker for file/network/shell capabilities.
- Signed-plugin policy.
- Plugin dependency resolution UI.
- Official catalog/marketplace (separate product).
- Hot reload for plugin development.


## 15. Plugin catalog

### 15.1 Model

A catalog is a static JSON document served over HTTPS or read from a local file/folder. Hosting requires no server.

```json
{
  "schemaVersion": 1,
  "generatedAt": "2026-10-27T12:00:00Z",
  "plugins": [
    {
      "id": "com.example.myplugin",
      "name": "My Plugin",
      "publisher": "Example",
      "summary": "Custom detector, device actions, and panels.",
      "description": "Longer markdown description.",
      "iconUrl": "https://example.com/icon.png",
      "homepage": "https://example.com",
      "license": "MIT",
      "tags": ["detector", "ui"],
      "versions": [
        {
          "version": "1.0.0",
          "apiVersion": "1.0",
          "minHostVersion": "1.0.0",
          "downloadUrl": "https://example.com/myplugin-1.0.0.ddplugin",
          "sha256": "9f2c…",
          "signature": "optional-base64-signature",
          "size": 123456,
          "publishedAt": "2026-10-20T10:00:00Z",
          "changelog": "Initial release.",
          "capabilities": ["project-detector", "device-action", "ui-page", "renderer"],
          "dependencies": [{ "id": "com.example.shared", "minVersion": "1.0.0" }]
        }
      ]
    }
  ]
}
```

### 15.2 Sources

- Multiple sources: HTTPS URL, local file/folder, internal team feed.
- Source settings: name, URL, enabled, priority, bearer token (DPAPI), allow prerelease.
- Source test connection.
- Offline cache with last checked/error state.
- No default official catalog URL ships in v1; users add sources.
- Sample catalog and schema included in docs.

### 15.3 Browse and updates

```text
Extensions
[Installed] [Browse catalog] [Updates]        [Check for updates] [Sources…]
Search…                                      [Source ▾] [ ] Include prerelease

┌──────────────────────────────────────────────────────────────────────┐
│ My Plugin  1.0.0  by Example                          [Install]      │
│ Custom detector, device actions, and panels.                          │
│ Detector · Device action · Panel · Compatible with DroidDeck 1.x      │
└──────────────────────────────────────────────────────────────────────┘
```

- Search, source filter, compatibility, capabilities, changelog.
- Updates tab shows installed vs available version, changelog, update button.
- “Update all” requires showing the list; no silent install.
- Prerelease only if enabled per source.
- Multiple sources: show source, do not silently choose; user picks.
- Version selection: highest compatible stable version by default.

### 15.4 Install/update pipeline

```text
resolve version → check compatibility → download → verify SHA-256
→ verify signature (if present) → validate manifest/package
→ trust prompt → extract to versioned folder → update active version
→ restart → load → health check → rollback on failure
```

- Download to temp; progress and cancel.
- Atomic install.
- Keep previous version for rollback.
- Hash mismatch aborts and deletes temp.
- Capability changes trigger a new trust prompt.
- Audit log records installs, updates, trust decisions, rollbacks, failures.

### 15.5 Trust and signatures

- SHA-256 pinning is mandatory.
- Signatures are displayed as verified/unverified; not required in v1.
- Trust prompt shows publisher, version, capabilities, hash, signature status.
- Re-prompt on hash or capability change.
- No auto-install or auto-update.
- Safe mode disables plugins.
- Catalog metadata is untrusted text; no HTML rendering; length limits.

### 15.6 Update policy defaults

| Setting | Default |
|---|---|
| Auto-check | Off |
| Auto-download | Off |
| Auto-install | Never |
| Prerelease | Off per source |
| Default catalog URL | None |
| Update notifications | In-app badge |

Options: manual, startup, daily, weekly. Auto-download can be enabled; install remains manual.

### 15.7 Privacy

- No telemetry.
- Catalog requests only on manual check or explicitly enabled schedule.
- Requests reveal the user’s IP to the catalog host; document this.
- User agent: `DroidDeck/<version>`.
- No machine ID, no usage data.
- Auth tokens stored with DPAPI; never exported.
- Offline mode uses cached catalog/packages.

### 15.8 Hosting and schemas

- Publish JSON Schema for the catalog.
- Provide a sample static catalog and a hosting guide.
- Support local file catalogs for offline/team use.
- Optional catalog signature verification with a configured public key (future-friendly).
- Package size limits and timeouts.

### 15.9 Catalog testing

- Schema/version selection/compatibility/prerelease/multiple sources/duplicates.
- Auth token handling, ETag/cache, offline mode.
- Malformed catalog, hash mismatch, optional signatures.
- Install/update/rollback/restart/trust re-prompt/capability change.
- No auto-install.
- Privacy defaults.


## 16. Renderer SDK

### 16.1 Goals

Allow plugins to add powerful views without destroying log performance or UI consistency.

### 16.2 Renderer kinds

| Kind | Used for | Notes |
|---|---|---|
| `LogDetail` | Selected log entry detail | JSON pretty-print, stack trace grouping, protobuf, custom formatting |
| `LogLine` | Custom line rendering | Opt-in only, filtered/small views; never the default hot path |
| `Chart` | Performance/metrics charts | Host supplies sampled series |
| `FilePreview` | Device/local file preview | Images, SQLite, JSON, text, custom formats |
| `TestResult` | Test case details | Screenshots, diffs, attachments |
| `DataView` | UI tree nodes, app data, generic JSON | Inspector/details panels |

### 16.3 Contracts

```csharp
public enum RendererKind
{
    LogDetail,
    LogLine,
    Chart,
    FilePreview,
    TestResult,
    DataView
}

public interface IRenderer
{
    string Id { get; }
    string DisplayName { get; }
    RendererKind Kind { get; }
    int Priority { get; }
    bool CanRender(RenderContext context);
    FrameworkElement CreateView(RenderContext context);
    void Dispose();
}

public interface IRenderContext
{
    object Data { get; }                 // stable DTO, never internal mutable model
    RendererKind Kind { get; }
    IServiceProvider Services { get; }
    IThemeService Theme { get; }
    IUiDispatcher Dispatcher { get; }
    CancellationToken CancellationToken { get; }
    Size AvailableSize { get; }
}
```

`IRenderer` and `IRenderContext` live in `DroidDeck.Plugin.UI`. `RendererKind`, descriptor DTOs, and selection metadata live in `DroidDeck.Plugin.Abstractions`, so non-UI plugin logic can reference renderer kinds without taking a WinUI dependency.

Registration:

```csharp
context.Extensions.AddRenderer<JsonLogDetailRenderer>();
context.Extensions.AddRenderer<SqlitePreviewRenderer>();
context.Extensions.AddRenderer<FrameTimeChartRenderer>();
```

### 16.4 Host built-ins

- Plain log detail.
- JSON pretty-print.
- Stack trace.
- Text/image file preview.
- Line chart.
- Test result detail.

Built-ins implement the same `IRenderer` contract.

### 16.5 UX

- “View as” / “Open with” menu on log details, files, test results, data views.
- Plugin renderers show a plugin badge.
- Settings → Renderers sets default per kind; “Reset to built-in”.
- Renderer failure falls back to the built-in view with an InfoBar.
- Repeated failures disable the renderer.
- Plugin manager lists contributed renderers and health.

### 16.6 Performance rules

- Create on demand; dispose when the view closes.
- No plugin `LogLine` renderer by default; only for filtered views under a configurable line limit.
- Chart renderers receive host-sampled data; no device polling from renderers.
- `CreateView` must be fast; async data loading uses the cancellation token.
- Host measures creation time and memory.
- Renderers must use host theme resources and support high contrast.
- Renderer UI must not block the UI thread.

### 16.7 Failure handling

- Wrap `CanRender`/`CreateView` in try/catch.
- On failure, show built-in renderer + error InfoBar.
- Disable after repeated failures.
- Log with renderer/plugin IDs.
- Never crash the session or host.

### 16.8 Renderer testing

- Selection/priority.
- Fallback on failure.
- Disposal and event unsubscription.
- Theme/high contrast.
- Performance budgets.
- Sample plugins with log detail, chart, and file preview renderers.
- UI tests for “View as”.


## 17. Performance engineering

### 17.1 Principles

- **Performance is a feature.** The daily run loop must feel immediate; every avoidable wait is a product defect.
- **Budgets, not vibes.** Every hot path has a numeric budget, a measurement method, and an owner.
- **Measure on reference hardware.** Budgets are meaningless without a fixed machine, device, and workload.
- **Keep the UI thread free.** No file I/O, process waits, JSON parsing, log parsing, image decoding, or plugin calls on the UI thread.
- **Bound everything.** Queues, buffers, caches, log lines, thumbnails, concurrent processes, and plugin work all have limits.
- **Coalesce and batch.** UI updates and disk writes happen in batches, never per log line.
- **Prefer events to polling.** Device tracking, file watching, and process output are event-driven.
- **Cache what is expensive, invalidate what is stale.** Detection, toolchain probes, APK metadata, package/activity resolution, and thumbnails are cached with explicit invalidation.
- **Lazy by default.** Pages, tabs, plugin assemblies, renderers, and toolbox panels load on demand.
- **Never drop data silently.** If the UI cannot keep up, the disk log remains complete and the UI shows a dropped-line indicator.
- **Regressions are bugs.** A performance regression blocks release unless an ADR records the trade-off.

### 17.2 Reference environment

| Role | Configuration |
|---|---|
| Primary development | Windows 11, 8-core CPU, 16 GB RAM, NVMe SSD |
| Low-end reference | Windows 11, 4-core CPU, 8 GB RAM, SATA SSD |
| Physical device | POCO F1, Android 15 / API 35, arm64-v8a, 1080x2246 |
| Emulator | Pixel API 35 AVD, x86_64, 4 GB RAM |
| Displays | 100% and 150% scaling, single and dual monitor |
| Network | Wired and Wi-Fi, including high-latency Wi-Fi for wireless ADB |

All budgets are p50 unless stated otherwise. p95 is recorded for startup, UI frame time, device hotplug, and mirror input latency. Measurements are stored as JSON in `docs/perf/results/` with date, build, hardware, device, and workload.

### 17.3 Budgets

| Area | Metric | Budget | Measurement |
|---|---|---|---|
| Startup | Cold start to interactive | 2.0 s p50 / 3.0 s p95 | Stopwatch from process start to Home accepting input |
| Startup | Warm start to interactive | 800 ms p50 | Stopwatch |
| Startup | First frame | 500 ms | ETW frame trace |
| Startup | Plugins ready (5 enabled) | 300 ms after first frame | Plugin host metrics |
| Projects | Add/scan typical project | 500 ms | Detector harness |
| Projects | Add/scan large monorepo | 2 s | Detector harness |
| Projects | Refresh detection | 300 ms | Detector harness |
| Tools | Toolchain doctor, cached | 200 ms | Doctor timing |
| Tools | Toolchain doctor, full | 5 s | Doctor timing |
| Devices | Hotplug visible in UI | 300 ms | track-devices event to UI |
| Devices | Metadata complete | 500 ms per device | adb timing |
| Run | Run command accepted | 100 ms | Click to step queued |
| Run | Build overhead vs direct CLI | 2% wall clock | Side-by-side timing |
| Run | Install/launch overhead vs adb | 300 ms | Side-by-side timing |
| Logs | Ingest throughput | 10k lines/s for 10 minutes | Replay harness |
| Logs | UI frame while streaming | 16.7 ms p95 | ETW frame trace |
| Logs | UI batch interval | 50 ms | Instrumentation |
| Logs | UI buffer | 20k lines default | Configuration |
| Logs | UI buffer memory | 50 MB | dotnet-counters |
| Logs | Search over 20k lines | 200 ms | Benchmark |
| Logs | Filter apply | 100 ms | Benchmark |
| Logs | Crash detected after line | 1 s | Replay harness |
| Session | Switch session | 100 ms | UI timing |
| Screen | Mirror start | 2 s | scrcpy process to first frame |
| Screen | Mirror frame rate | 30-60 fps | scrcpy statistics |
| Screen | Input latency | under 150 ms | High-speed camera measurement |
| Screen | Thumbnail CPU | under 2% idle, under 5% active | ETW |
| Memory | Baseline working set | 350 MB | Task Manager / ETW |
| Memory | Three active mirrors | 900 MB | Task Manager / ETW |
| Memory | Eight-hour growth | under 5% | Soak test |
| CPU | Idle | under 1% | ETW |
| Config | Cached resolve | 1 ms | BenchmarkDotNet |
| Config | Settings page load | 200 ms | UI timing |
| Config | Change propagation | 100 ms | Instrumentation |
| Commands | Command palette open | 50 ms | UI timing |
| Plugins | Startup with 5 plugins | 300 ms | Plugin host metrics |
| Plugins | Plugin page creation | 100 ms | Plugin host metrics |
| Plugins | Renderer creation | 50 ms | Renderer registry metrics |
| Plugins | Log parser throughput | 50k lines/s | Benchmark |
| Catalog | Parse 1k-plugin catalog | 100 ms | Benchmark |
| Shutdown | Graceful shutdown | 2 s | Stopwatch |

### 17.4 Startup path

| Phase | Work | Budget |
|---|---|---|
| A | .NET runtime and WinUI initialization | 500 ms |
| B | Shell first frame with skeleton | 300 ms |
| C | Load settings, theme, and feature flags | 100 ms |
| D | Restore last project, run config, and device selection | 100 ms |
| E | Start adb tracker; show cached devices | 500 ms |
| F | Load cached toolchain snapshot | 300 ms |
| G | Scan plugin manifests without loading assemblies | 200 ms |
| H | Load and initialize enabled plugins asynchronously | 300 ms |
| I | Populate home widgets and recent sessions | 500 ms |

Rules:

- No synchronous file or network I/O during startup.
- No `.Result`, `.Wait()`, or UI-thread blocking on async work.
- Shell renders before device, toolchain, or plugin data is ready; each region has a skeleton.
- Device tracking starts immediately but never blocks first frame.
- Toolchain discovery runs in the background and uses the last cached snapshot for immediate display.
- Plugin assemblies load after first frame; plugin UI is created only when opened.
- Home widgets load independently; one slow widget cannot block the page.
- Startup failures degrade to an empty but usable shell with a diagnostics banner.
- A startup trace is captured in developer mode and attached to diagnostics on request.

### 17.5 UI rendering

- Use compiled `x:Bind` bindings; avoid converters and reflection binding on hot paths.
- Virtualize every long list; use `ItemsRepeater` or `ListView` with recycling and fixed row heights where possible.
- Batch collection changes; never raise one change notification per log line.
- Freeze and share brushes, pens, and fonts; avoid per-item shadows, blur, and gradients in lists.
- Use `x:Load` for heavy tabs and unload hidden content.
- Pause rendering, animations, thumbnails, and chart updates when a page or tab is hidden.
- Debounce resize and split-pane drags; avoid layout thrash.
- Avoid nested scroll viewers; use a single scrolling surface per pane.
- Keep visual trees shallow; prefer templates over deeply nested panels.
- Use `DispatcherQueue.TryEnqueue` with coalescing; check queue depth and skip stale frames.
- Never update UI from background threads without the dispatcher.
- Use `AutomationProperties` and accessible names without adding extra layout nodes.
- Respect reduced motion and high contrast without triggering expensive re-templating.

Log viewer specifics:

- Fixed line height, monospace font, and a custom virtualized surface.
- Selection and search operate on the bounded buffer, not the visual tree.
- Auto-scroll pauses when the user scrolls up; a jump-to-latest button shows the count of new lines.
- Wrapped lines are opt-in; wrapping large logs is expensive and off by default.
- Highlighting is applied to visible rows only, not the entire buffer.
- Column visibility (time, level, tag, PID) is configurable without rebuilding the data.
- Export and copy use the raw line, not the rendered text.

Charts:

- Host samples metrics at a fixed cadence; renderers receive downsampled series.
- Use largest-triangle-three-buckets or min/max decimation to match pixel width.
- Cap points per chart and per session; never keep unbounded history in memory.
- Charts pause when hidden and resume with a gap marker.

### 17.6 Log pipeline

```text
adb stdout
  -> UTF-8 line reader
  -> threadtime parser
  -> crash/ANR detector
  -> filter chain (level, tag, pid, text)
  -> bounded UI ring buffer
  -> batched UI flush (50 ms or 500 lines)
  -> async disk writer (session log)
```

Rules:

- **Disk is the source of truth.** Every line is written to the session log even if the UI cannot display it.
- **UI buffer is bounded.** Default 20k lines; oldest lines are evicted and a dropped-line counter is shown.
- **Ingest never waits for the UI.** The UI batcher reads from a snapshot or channel and drops frames rather than blocking adb.
- **Disk writer is bounded.** A 50k-line channel with batched writes; if the disk cannot keep up, show a warning and keep counting.
- **Parse with spans.** Avoid regex per line; use prefix checks and a compiled regex only for crash and ANR patterns.
- **Filter cheaply first.** Level and tag checks run before text search; regex search runs only when requested.
- **Search off-thread.** Cancellable, debounced, incremental results; never scan on the UI thread.
- **Multi-device fairness.** Per-device pipelines feed a shared UI scheduler with round-robin batches and per-device quotas.
- **Pause hidden logs.** A hidden log tab stops UI flushing but continues disk writes and crash detection.
- **Crash detection is always on.** Even when the UI is paused, `FATAL EXCEPTION`, `ANR in`, `am_crash`, and `am_anr` are detected.
- **Metrics are visible.** Queue depth, lines per second, dropped lines, and disk write latency appear in the developer overlay and diagnostics.
- **No ANSI injection.** Strip or render control sequences safely; never pass raw escape sequences to the UI.
- **No unbounded strings.** Truncate pathological lines for display while preserving the raw line on disk.

### 17.7 Process, adb, and filesystem

- Run executables directly with `ProcessStartInfo.ArgumentList`; use a shell only when the user explicitly configures a shell step.
- Use Windows Job Objects with kill-on-close for build and tool processes; fall back to `Process.Kill(entireProcessTree: true)`.
- Read stdout and stderr asynchronously with bounded buffers; never call `ReadToEnd` on a long-running process.
- Apply per-step timeouts and cancellation; a cancelled build must not leave orphan processes.
- Use a single persistent `host:track-devices-l` TCP socket connection to `127.0.0.1:5037` (with `adb.exe track-devices -l` process fallback); never poll `adb devices`.
- Batch device metadata into one shell invocation per device using markers, then parse the sections.
- Cache metadata and refresh on reconnect, explicit refresh, or relevant property change.
- Debounce file watchers (500 ms default) and ignore `build`, `.gradle`, `node_modules`, `.git`, and configured folders.
- Never recursively scan an entire repository during project add; use marker files and a bounded depth.
- Run `aapt2 dump badging` once per artifact and cache by path, size, and last-write time.
- Resolve APK globs with ignore rules; never traverse `node_modules` or build caches unnecessarily.
- Use source-generated `System.Text.Json`; no reflection-based serialization on startup or log paths.
- Write settings and session metadata atomically (temp file + replace) off the UI thread.
- Support long paths, Unicode paths, and paths with spaces; never concatenate shell strings.
- Keep session log writes buffered and flush on a timer and on shutdown.
- Prefer `PipeReader` or a buffered stream reader for process output; avoid per-byte reads.
- Measure process startup overhead separately from tool execution time.

### 17.8 Memory

| Subsystem | Budget |
|---|---|
| WinUI shell and framework | 150 MB |
| Project, device, and session models | 20 MB |
| Log UI buffer | 50 MB |
| Thumbnails and images | 50 MB |
| Plugin metadata and loaded assemblies | 40 MB |
| Caches (detection, APK, toolchain) | 20 MB |
| Headroom | 20 MB |
| **Baseline total** | **350 MB** |

Rules:

- Use `ArrayPool<byte>` and reusable buffers for stream reads and image decoding.
- Avoid duplicate strings for log tags, levels, and package names; intern bounded, known values only.
- Decode thumbnails at display size, not full device resolution.
- Use an LRU cache for thumbnails and APK icons with a hard item and byte limit.
- Release log buffers when a session is closed; keep only the on-disk log and summary.
- Dispose scrcpy processes, window handles, image sources, and event subscriptions deterministically.
- Avoid static events; use weak events or explicit unsubscribe in view models and plugins.
- Plugins cannot be unloaded without a restart; document and measure their retained memory.
- Respond to memory pressure by trimming caches, pausing thumbnails, reducing the log UI buffer, and warning before opening more mirrors.
- Run `dotnet-gcdump` and handle-count checks in soak tests; investigate growth above 5% per eight hours.
- Do not use `GC.Collect` as a fix; find the retention path.

### 17.9 Screen performance

- Use scrcpy defaults tuned for responsiveness: `--max-size 1024`, `--max-fps 60`, `--video-bit-rate 8M`, hardware decoding where available.
- Do not re-encode or copy frames in the host when scrcpy owns the window.
- Pause or close mirrors when the session is hidden, the device disconnects, or the user navigates away.
- One interactive mirror per device; reopening focuses the existing window.
- Thumbnails run at 1 fps only for visible device cards; pause when minimized or occluded.
- Record only on demand; show a clear recording indicator and file size.
- Warn when more than three mirrors or two active recordings are open.
- Measure mirror start, frame rate, input latency, and host CPU per device.
- Prefer external scrcpy windows over an unstable embedding path; correctness beats integration.
- Native streaming later must meet or beat these budgets before becoming the default.

### 17.10 Plugins and renderers

- Scan manifests and metadata without loading assemblies.
- Load enabled plugins asynchronously after first frame; never block startup on a plugin.
- Create plugin pages, panels, widgets, and renderers on demand; dispose when closed.
- Enforce budgets for page creation (100 ms), renderer creation (50 ms), and log parser throughput (50k lines/s).
- Measure plugin load time, UI creation time, parser throughput, memory, and failures.
- Show slow or failing plugins in the plugin manager with health details.
- Do not run plugin log parsers on the hot path unless the user explicitly enables them.
- Do not let a renderer own polling; the host supplies sampled data.
- In-process plugins cannot be preempted; document that a badly behaved plugin can freeze the app and must be disabled in safe mode.
- Sample plugins are part of performance CI to catch regressions in the host contracts.

### 17.11 Build and run performance

- Build once per run group when the artifact is device-agnostic.
- Reuse the Gradle daemon; do not pass `--no-daemon` by default.
- Do not clean before every run; offer clean as an explicit action.
- Configuration cache and offline mode are opt-in per project because they can change build behavior.
- Stream and parse build output incrementally; stop parsing after a terminal failure line when safe.
- Cache artifact paths, package names, launcher activities, and APK metadata per variant.
- Invalidate caches on rebuild, artifact timestamp change, or explicit refresh.
- Install to multiple devices in parallel, bounded by per-device locks and a global limit.
- Avoid redundant adb calls; combine metadata, package, and activity queries where possible.
- Keep hot reload processes alive; do not rebuild for a Flutter hot reload or Metro reload.
- Measure host overhead against the direct CLI on the same project and record it.

### 17.12 Measurement toolkit

| Tool | Purpose |
|---|---|
| BenchmarkDotNet | Microbenchmarks for parsers, filters, config, detection, catalog |
| dotnet-counters | CPU, GC, working set, thread pool, exceptions |
| dotnet-trace | CPU and allocation traces |
| dotnet-gcdump | Managed heap retention and leak analysis |
| PerfView | CPU, allocations, ETW, handle analysis |
| Windows Performance Recorder / Analyzer | UI responsiveness, frame times, disk I/O |
| ETW markers | Custom spans for startup, run steps, log batches, plugin calls |
| FlaUI timing | UI automation durations and interaction latency |
| Replay harness | Deterministic log files at 10k lines/s |
| Fake adb | Scriptable devices, logcat, install failures, latency |
| scrcpy statistics | Frame rate, dropped frames, bitrate |
| Developer overlay | Live FPS, frame time, memory, GC, queues, sessions, mirrors |

Developer overlay contents:

```text
FPS 58 · frame 12.4 ms · managed 212 MB · working set 348 MB
GC 0/1/2 · log queue 1,204 · UI dropped 0 · disk lag 12 ms
Sessions 2 · mirrors 1 · builds 1 · plugins 5 · renderers 3
```

The overlay is available in developer mode and is excluded from release screenshots unless enabled.

### 17.13 Regression policy

- Maintain a hot-path list: startup, device tracking, log ingest, log UI, filtering, search, config resolve, detection scan, APK inspect, run pipeline, plugin load, renderer creation.
- Every PR touching a hot path includes a before/after measurement or a reason it cannot affect performance.
- Benchmarks run on every PR for Core, Adb, Projects, and Plugins; a regression above 10% fails unless approved.
- UI frame and memory checks run nightly and before release.
- An eight-hour soak runs before each milestone release.
- Milestone reviews compare results against the previous milestone and the original budgets.
- Exceptions are documented in an ADR with the user-visible trade-off and a removal plan.
- Budgets are revisited only with measurements, not opinions.


## 18. Testing and quality

### 18.1 Strategy

| Layer | Scope | Speed | Runs |
|---|---|---|---|
| Unit | Pure logic, parsers, state machines, config, templates, validation | Under 1 s per test | Every PR |
| Integration | Process runner, fake adb, local catalog server, file system, plugin host | Seconds per test | Every PR |
| UI automation | Shell navigation, dialogs, settings, palette, plugin pages, accessibility | Minutes per suite | PR smoke, nightly full |
| Device E2E | Real adb, emulator, physical device, install/launch/logcat/mirror | Minutes per scenario | Nightly and release |
| Performance | Benchmarks, frame traces, soak, stress | Minutes to hours | Nightly and release |
| Security | Adversarial fixtures, injection, package safety, trust, secrets | Seconds per test | Every PR |
| Manual | Exploratory, accessibility, visual review, device quirks | Hours | Milestone and release |

Principles:

- Test behavior and contracts, not private implementation details.
- Every bug fix gets a regression test unless it is impossible to reproduce deterministically; then document the manual check.
- Tests are deterministic: fixed clocks, temp directories, no external network, no sleeps for synchronization.
- Real devices are used for truth; fakes are used for speed and edge cases.
- Fixtures are small, committed, and buildable offline.
- Tests must not require a specific user name, drive letter, or Android SDK location.
- A flaky test is a defect; quarantine it with an issue and fix or delete it.
- Test output is diagnostic: include the command, exit code, and relevant log tail on failure.

### 18.2 Unit tests

| Project | Focus |
|---|---|
| **DroidDeck.Core** | Run state machine, step failure policies, retries, cancellation, run group aggregation, session store, retention, feature flags, command registry, keybinding conflicts, theme contrast, import/export, redaction, template engine, condition evaluator |
| **DroidDeck.Adb** | `adb devices -l` parser, `track-devices` parser, device state transitions, logcat threadtime parser, install error mapping, `am start` output parser, `dumpsys battery`, `wm size`, `wm density`, package list, UIAutomator XML, command builder and quoting |
| **DroidDeck.Projects** | Every detector fixture, Gradle settings/build parsing, variants and flavors, artifact globs, package/activity resolution, toolchain compatibility, Gradle/Kotlin/Java error parsers, Flutter/RN/Expo/MAUI/Capacitor/Cordova/Unity/APK/custom adapters |
| **DroidDeck.Screen** | scrcpy argument builder, option validation, window embedding abstraction, thumbnail scheduler, recording lifecycle, cleanup |
| **DroidDeck.Plugins** | Manifest validation, semver and API compatibility, package safety, trust and hash pinning, capability changes, safe mode, catalog parsing and version selection, renderer selection and fallback, settings schema mapping |
| **DroidDeck.App** | View models, command enablement, validation messages, navigation state, layout persistence, scope switching, search indexing |

Techniques:

- Fakes for `IFileSystem`, `IProcessRunner`, `IClock`, `IAdbClient`, `ISecretStore`, `IUiDispatcher`, and `IEventBus`.
- Snapshot tests for generated commands, run plans, scrcpy arguments, and catalog selections.
- Property-based tests for placeholder expansion, config merge precedence, and path handling where valuable.
- Golden files for log parsing, Gradle output, install errors, and UIAutomator trees.
- No mocking of the type under test; mock boundaries only.
- Tests assert user-visible outcomes and stable DTOs.

Coverage targets (guidance, not a vanity metric):

| Area | Line | Branch |
|---|---|---|
| Core run and config logic | 85% | 75% |
| Adb parsers and command builders | 90% | 80% |
| Project detectors and adapters | 85% | 75% |
| Plugin host, trust, catalog | 85% | 75% |
| App view models | 70% | 60% |
| Screen argument and lifecycle logic | 80% | 70% |

Critical state machines (run lifecycle, trust decisions, install failure recovery) target 90% branch coverage. Mutation testing is optional for Core and the plugin trust path.

### 18.3 Integration tests

- **Process runner:** helper executables for stdout, stderr, large output, binary output, ANSI sequences, exit codes, environment variables, working directory, Unicode, cancellation, timeout, and child-process tree kill.
- **Fake adb:** a scriptable executable or local server that simulates device lists, unauthorized/offline states, logcat streams, install failures, shell responses, and adb server restarts.
- **Real adb:** emulator tests for metadata, install, launch, logcat, screenshot, and package queries; run nightly.
- **File system:** temp directories, spaces, Unicode, long paths, read-only files, corrupt JSON, missing folders, and permission errors.
- **Configuration:** precedence across defaults, global, profile, project, run config, device, session, and CLI; migrations; unknown-key preservation; atomic writes; backup recovery.
- **Plugin host:** load sample plugins, shared contract identity, dependency resolution, failure isolation, settings schema rendering, enable/disable/restart behavior, safe mode.
- **Catalog:** local file source and local HTTPS server; ETag, auth token, prerelease, duplicate IDs, malformed JSON, hash mismatch, redirect to HTTP, timeout, and size limit.
- **Renderer host:** selection, priority, fallback, disposal, repeated failure disablement.
- **Session store:** continuous writes, crash-safe recovery, retention cleanup, export, read-only reopen.
- **Screen:** scrcpy argument construction and process lifecycle with a fake scrcpy executable; real embedding is a POC and manual test.

### 18.4 UI automation

- Use FlaUI and Windows UI Automation with stable `AutomationId` values.
- Run the app with `--test-mode`, a temp data directory, fake services, and a deterministic clock.
- Cover onboarding, add project, detection review, run, cancel, failure, log filtering, crash banner, settings scope switching, JSON editing, command palette, plugin page, plugin trust dialog, and destructive confirmations.
- Verify keyboard-only navigation, focus order, accessible names, and dialog focus trapping.
- Verify light, dark, high contrast, 100%, 150%, and 200% scaling.
- Avoid pixel assertions for layout; use visual snapshots only as review artifacts, not gates.
- Wait for UI idle and specific states; never use fixed sleeps.
- Each UI test cleans up temp data and closes windows.
- Full UI suite runs nightly; a smoke subset runs on every PR.

### 18.5 Device and end-to-end matrix

| Target | Purpose |
|---|---|
| POCO F1, Android 15, USB | Primary physical device, vendor quirks, real performance |
| Pixel API 35 emulator | Repeatable baseline, snapshots, fast reset |
| API 28 emulator | Older runtime behavior and permission differences |
| Tablet or foldable emulator | Layout, orientation, and screen size changes |
| Wireless ADB device | Pairing, mDNS, reconnect, network loss |
| Unauthorized and offline states | Guidance, retry, adb restart |
| One physical plus two emulators | Multi-device orchestration and partial failure |

Scenarios:

- First run on a fresh project, including toolchain warnings.
- Incremental run after a code change.
- Build failure with a clickable Kotlin/Java error.
- Install failure: signature mismatch, downgrade, insufficient storage, test-only APK.
- Launch failure: missing activity, crash on startup.
- Crash and ANR detection with stack trace and restart actions.
- Cancel during build, install, and launch.
- Device disconnect during install and during logcat.
- Multi-device run with one device failing; retry failed devices.
- Flutter hot reload and hot restart.
- React Native Metro start, `adb reverse`, and reload.
- APK drop, inspection, split install, and launch.
- Screen mirror, pop-out, screenshot, recording, and device buttons.
- Wireless pairing, disconnect, and auto-reconnect.
- Emulator launch, cold boot, wipe data, and stop.
- Device toolbox: apps, files, shell, ports, permissions, deep links, reboot.
- Performance metrics and UI inspector.
- Plugin detector, device action, session panel, command, and renderer.
- Catalog install, update, rollback, trust re-prompt, and safe mode.

### 18.6 Test fixtures

```text
samples/
  hello-android/          Minimal Gradle app, one module, debug/release
  gradle-flavors/         Product flavors and variants
  gradle-multi-module/    app + core + feature modules
  gradle-kts/             Kotlin DSL and version catalog
  kmp-basic/              Kotlin Multiplatform with Android app
  flutter-basic/          Flutter app with android/ folder
  rn-basic/               Bare React Native project
  expo-managed/           Managed Expo project
  expo-bare/              Prebuilt Expo project
  maui-basic/             .NET MAUI Android target
  capacitor-basic/        Capacitor Android project
  cordova-basic/          Cordova Android project
  unity-basic/            Unity project metadata only
  custom-scripts/         package.json, ps1, cmd, sh, Makefile
  apk/                    Standalone and split APK fixtures
  malformed/              Partial, corrupt, and ambiguous projects
```

Golden files:

- Expected detection result, evidence, modules, variants, and warnings.
- Expected generated commands for build, install, launch, and mirror.
- Expected parsed log entries and crash groups.
- Expected install error classification and suggested action.
- Expected plugin manifest validation errors.
- Expected catalog version selection.

Fixtures must not require network access, a running Gradle daemon, or a specific JDK. Detector tests never invoke Gradle.

### 18.7 Performance, soak, and stress

- **Benchmarks:** log parser, filter chain, ring buffer, config resolve, detector scan, template expansion, condition evaluation, catalog parse, renderer selection, scrcpy argument builder.
- **Replay harness:** deterministic log files at 1k, 10k, and 50k lines/s with realistic multi-line stack traces.
- **Soak:** eight hours with three devices, continuous logcat at 2k lines/s, two mirrors, twenty runs, and repeated settings changes. Assert memory growth under 5%, stable handle count, no orphan processes, and no window leaks.
- **Stress:** 10k lines/s burst; 100 hotplug cycles; adb server kill; device disconnect during install; disk full; read-only data directory; network loss during catalog download; plugin throwing on every call; corrupt config; 50 plugin manifests.
- **Leak checks:** process handles, window handles, event subscribers, image sources, scrcpy processes, and temp files.
- **Startup tests:** cold and warm start on the low-end reference machine.
- **UI frame tests:** streaming logs while scrolling, searching, and switching sessions.
- Results are stored and compared with the previous run; regressions are reviewed before release.

### 18.8 Security tests

- Injection attempts through project name, path, package name, activity, device serial, model, variant, environment value, deep link, catalog field, and plugin manifest.
- Zip slip, absolute paths, symlinks, duplicate entries, zip bombs, oversized packages, and malformed manifests.
- Hash mismatch, tampered catalog, downgrade, capability escalation, and trust re-prompt.
- Secret redaction in logs, exports, diagnostics, crash reports, and command previews.
- Safe mode disables plugins and experimental features.
- Plugin exceptions in detectors, steps, actions, commands, UI factories, log parsers, and renderers.
- TLS certificate errors, HTTP redirects, missing auth, and expired tokens.
- Local file catalog path traversal and permission errors.
- ANSI escape injection and untrusted HTML in catalog descriptions.
- Data directory ACLs and portable-mode warnings.
- No auto-install, auto-update, or silent trust decision.

### 18.9 Compatibility and accessibility

Compatibility:

- Windows 11 24H2 and 25H2 primary; Windows 10 22H2 best effort at the documented minimum build.
- Windows Sandbox or a clean VM for portable publish verification.
- 100%, 125%, 150%, and 200% scaling; mixed-DPI dual monitors.
- Light, dark, high contrast, and reduced motion.
- Long paths, Unicode user names, paths with spaces, and non-English locales.
- x64 primary; ARM64 only if the open decision resolves to support it.
- Android devices from API 26 through API 35 where the project supports them.
- Android Studio installed and not installed; adb server already running and not running.
- Flutter, Node, MAUI workload, and Unity present and absent.

Accessibility:

- Every interactive element has an accessible name and role.
- Full keyboard navigation with visible focus.
- Dialog focus trapping and Escape behavior.
- Screen reader checks with Narrator for main flows.
- Color is never the only signal; icons and text accompany status colors.
- Log level colors meet contrast targets in light, dark, and high contrast.
- 200% text scaling does not clip critical controls.
- Reduced motion disables nonessential animation.
- Error messages are announced and actionable.

### 18.10 CI/CD

| Pipeline | Trigger | Steps |
|---|---|---|
| **PR** | Every pull request | Restore locked, build with warnings as errors, analyzers, unit tests, integration tests with fake adb, plugin host tests, schema validation, markdown lint, secret scan, license check |
| **Nightly** | Scheduled | Full UI smoke, emulator device tests, performance benchmarks, short soak, dependency audit, flaky test report |
| **Release candidate** | Release branch | Full device matrix, full UI suite, eight-hour soak, security tests, portable publish, clean-VM smoke, accessibility pass |
| **Release** | Tag | Rebuild from tag, sign, generate SBOM and checksums, publish artifacts, update changelog, verify download |

Rules:

- Required checks block merge.
- Test results and coverage are published as artifacts.
- Device tests run on a self-hosted Windows runner with the emulator and physical device.
- No telemetry is collected from users; CI metrics are build-only.
- Flaky tests are quarantined with an owner and issue, not silently retried forever.
- Release artifacts are built once and promoted, never rebuilt differently for distribution.
- Every release includes a portable zip, symbols, checksums, third-party notices, and release notes.

### 18.11 Code quality

- Nullable reference types enabled everywhere.
- Warnings as errors in CI; pragmatic suppressions require a comment and an issue.
- .NET analyzers, code-style enforcement, and `dotnet format` verification.
- Architecture tests enforce dependency direction: no WinUI in Core, Adb, Projects, or Plugin.Abstractions.
- Public API analyzer for plugin contract assemblies.
- Central package management with locked restore.
- Dependency review, license audit, and secret scanning.
- XML documentation on public plugin APIs.
- No dead code, commented-out blocks, or TODO without an owner and issue.
- Error messages are specific, actionable, and free of internal jargon.
- Async methods end in `Async`, accept `CancellationToken`, and never use `async void` except event handlers.
- Disposable resources are owned and disposed deterministically.
- Logging uses structured properties and correlation IDs.

Definition of Done for a feature:

1. Behavior implemented behind a service or contract.
2. Unit tests for logic and edge cases.
3. Integration or UI test where the risk justifies it.
4. Error, empty, loading, and unauthorized states handled.
5. Keyboard and accessibility checked.
6. Settings and defaults documented; feature flag or visibility considered.
7. Diagnostics and logs include enough context to debug.
8. Performance budget checked if on a hot path.
9. Docs updated, including plugin or config schema changes.
10. No new warnings, no flaky tests, and CI green.

### 18.12 Manual QA and release sign-off

- Each milestone has a written test plan mapped to acceptance criteria.
- Exploratory charters target device quirks, vendor ROMs, wireless ADB, multi-monitor, and plugin failure modes.
- Bug severity: blocker, critical, major, minor, cosmetic. Blockers and criticals block release.
- Triage happens at least weekly during active development and daily before release.
- Accessibility and UX reviews are part of milestone sign-off.
- Release sign-off requires: CI green, device matrix pass, soak pass, security review, docs complete, and a clean-VM portable smoke test.
- Known issues are published with workarounds; silent omissions are not acceptable.


## 19. Security, privacy, and safety

### 19.1 Threat model

Assets:

- User source code and build scripts.
- Device data, app data, screenshots, recordings, and logs.
- Secrets: catalog tokens, signing material, API keys used by custom steps.
- The user's Windows account and local files.
- Trust decisions for projects, plugins, and catalog sources.
- The integrity of the DroidDeck release.

Adversaries and failure sources:

- A malicious or compromised project repository.
- A malicious or compromised plugin.
- A compromised or impersonated catalog source.
- A malicious APK or split APK set.
- A network attacker intercepting catalog or update traffic.
- A local attacker with access to the user's profile or portable folder.
- A user making a destructive mistake under time pressure.
- A vendor ROM or adb behavior that produces unexpected output.

Trust boundaries:

| Boundary | What crosses it | Control |
|---|---|---|
| Project folder to host | Build scripts, Gradle files, package scripts, configs | User-initiated run, exact command display, no auto-run, path validation |
| Plugin package to host | Assemblies, manifests, UI, settings | SHA-256 pinning, trust prompt, capability disclosure, safe mode |
| Catalog to host | Metadata, download URLs, hashes, signatures | HTTPS, hash verification, no auto-install, optional signatures |
| Host to device | adb commands, APK installs, shell, input | Per-device locks, confirmations, install error handling, authorization |
| Host to network | Catalog requests, package downloads, update checks | Off by default, TLS, minimal data, documented disclosure |
| Host to local disk | Settings, logs, sessions, secrets | Per-user data directory, DPAPI, redaction, retention |

Threat table:

| Threat | Vector | Impact | Mitigation | Residual risk |
|---|---|---|---|---|
| Arbitrary code via project | Gradle wrapper, npm scripts, custom shell steps | Full user code execution | Run is explicit; show command; never auto-run on add; warn on repo-shared scripts | User trusts the project they run |
| Malicious plugin | Installed package or folder | Full app and user privileges | Hash pinning, trust prompt, capability list, no auto-install, safe mode, source review guidance | Full trust is not a sandbox |
| Compromised catalog | Tampered metadata or package | Malicious plugin install | Mandatory SHA-256, HTTPS, optional signatures, trust prompt, no auto-update | User may trust a bad publisher |
| Command injection | Package name, serial, path, variant, catalog field | Arbitrary command execution | `ArgumentList`, validation, no shell interpolation, canonical paths | Custom shell steps are inherently powerful |
| Malicious APK | Dropped file | Device compromise or data loss | Inspect before install, show permissions, require install action | Device-level malware is outside host control |
| Secret leakage | Logs, exports, diagnostics, crash reports | Credential exposure | DPAPI, `${secret:...}`, redaction, export exclusions, review screen | Logs may contain app secrets not known to the host |
| Local data exposure | Shared portable folder, permissive ACLs | Source and device data exposure | Per-user `%LocalAppData%`, ACL inheritance, portable warning | User-controlled storage location |
| Network interception | Catalog or update download | Tampered content | TLS, certificate validation, hash, signatures | Compromised publisher |
| Destructive mistake | Uninstall, clear data, wipe emulator, reboot | Data loss | Confirmation with exact target and consequence, dry run | User confirms |
| Unauthorized device | RSA prompt, wireless debugging | Device access | User authorization, no auto-accept, pairing guidance | User grants access |
| Log injection | ANSI/control sequences, crafted tags | UI spoofing or confusion | Strip control sequences, safe rendering, no raw terminal emulation | Visual deception is limited |
| Zip slip | Plugin package paths | Arbitrary file write | Canonical path validation, no absolute paths, no symlinks, entry limits | Parser bugs |
| Supply chain | NuGet or tool dependency | Compromised build | Locked restore, audit, SBOM, signed releases, pinned tools | Upstream compromise |

### 19.2 Process execution safety

- Execute programs directly with `ProcessStartInfo.ArgumentList`; never build a command line by concatenating user-controlled strings.
- Use a shell only for steps the user explicitly defines as shell steps, and display the exact shell, command, arguments, and working directory.
- Treat package names, activity names, device serials, variants, and paths as data, not code.
- Validate device serials against the adb-safe character set; validate package and activity names with Android-compatible patterns.
- Canonicalize and validate paths; reject paths outside the project for project-relative steps unless the user explicitly allows absolute paths.
- Validate that the working directory exists before starting a process.
- Pass only configured environment variables to child processes; do not dump the full environment into logs.
- Show the exact command before running, and offer a dry-run preview for run configs and custom steps.
- Never request elevation; the app runs as the current user with `asInvoker`.
- Never run `adb root`, `adb remount`, `fastboot`, or bootloader operations automatically. Reboot actions require explicit confirmation.
- Use job objects and process-tree kill for cancellation and app shutdown.
- Apply timeouts and output limits; a runaway process cannot consume unbounded memory.
- Capture the tail of output for errors and keep full output in the session log.
- Treat `.ps1`, `.cmd`, `.bat`, and `.sh` files as executable code; show a warning in the step editor.
- Run PowerShell with `pwsh -NoLogo -NoProfile -File`; run batch files through `cmd.exe /d /s /c` with correct quoting.
- Require Git Bash or WSL for `.sh` scripts; show a clear error if neither is available.
- Do not execute scripts from a catalog or downloaded package automatically.
- Repo-shared configs that add scripts require an explicit trust confirmation the first time they are used on that machine.

### 19.3 Plugin security

- Plugins run in-process with full trust. This is a product decision, not an accident.
- The plugin manager states plainly that a plugin can read files, access the network, run processes, and access device data with the user's privileges.
- Capabilities in the manifest are disclosure, not enforcement. The host does not claim to sandbox them.
- SHA-256 is mandatory for every installed package and is pinned on first trust.
- The trust prompt shows publisher, version, capabilities, hash, signature status, source, and requested dependencies.
- Re-prompt when the hash, version, capabilities, or publisher changes.
- Never auto-install or auto-update a plugin.
- Developer mode can load an unpacked folder, but shows a persistent warning and is disabled by default.
- Safe mode disables all plugins and experimental features without deleting configuration.
- Validate package structure: no absolute paths, no `..` traversal, no symlinks, no duplicate entries, bounded entry count and uncompressed size.
- Load each plugin in its own `AssemblyLoadContext` for dependency isolation, not as a security boundary.
- Wrap every extension call in try/catch and attribute failures to the plugin.
- Disable a plugin after repeated failures and show the error in the plugin manager.
- Keep an audit log of installs, updates, trust decisions, rollbacks, and failures.
- Uninstall offers keep-data or delete-data; the default is keep data with a clear note.
- Signatures are displayed when present and verified if a public key is configured, but are not required in v1.
- Document that plugin authors should publish source and reproducible builds.

### 19.4 Catalog security

- Require HTTPS for remote catalog sources; allow local file and folder sources for offline and team use.
- Reject redirects from HTTPS to HTTP.
- Validate TLS certificates with the system trust store; do not add custom bypasses.
- Apply connect and read timeouts; cap catalog size and plugin package size.
- Verify SHA-256 before extraction; abort and delete the temporary file on mismatch.
- Display signature status; optional signature verification uses a configured public key.
- Store catalog bearer tokens with DPAPI; never export them.
- Ship no default catalog URL and no default marketplace.
- Treat all catalog text as untrusted: plain text or sanitized Markdown only, no scripts, no remote HTML, no automatic media loads.
- Do not auto-install, auto-update, or auto-trust a plugin from any catalog.
- Cache catalog responses with ETag and last-checked metadata; support offline mode.
- Show the source of every catalog entry; never silently choose between conflicting sources.
- Document that catalog requests reveal the user's IP address to the catalog host.
- Support removing a source and clearing its cache and downloaded packages.
- Publish the catalog JSON Schema and a sample static catalog for teams.

### 19.5 Secrets

- Store named secrets with Windows DPAPI in the current user context.
- Reference secrets with `${secret:NAME}`; never store secret values in project files.
- Never write secret values to logs, session files, exports, diagnostics, crash reports, or command previews.
- Mask secret inputs in the UI after save; allow replace or delete, not reveal.
- Exclude secrets from profiles, presets, project sharing, and catalog requests unless explicitly requested for a child process.
- Prefer environment variables for child processes over command-line arguments when a tool supports it.
- Redact common secret patterns from logcat and build output using configurable rules, while documenting that redaction is best effort.
- Clear temporary secret buffers where practical; .NET strings are immutable, so treat memory zeroization as best effort and document it.
- Rotate or delete catalog tokens from the source settings.
- A diagnostics export includes a review screen showing exactly what will be included.

### 19.6 Device and data safety

| Action | Confirmation | Notes |
|---|---|---|
| Install APK | Show package, version, devices, flags | Signature mismatch or downgrade adds a stronger warning |
| Uninstall app | Show device, package, data loss | Offer keep-data (`-k`) explicitly; the default removes app data |
| Clear app data | Show device and package | Irreversible |
| Force stop | Show device and package | Low risk, still explicit |
| Reboot | Show device and reboot target | Recovery and bootloader require stronger confirmation |
| Wipe emulator data | Show AVD name and consequence | Irreversible for that AVD |
| Push file | Show source, destination, overwrite state | Prompt before overwrite |
| Pull file | Show source and destination | Avoid silently overwriting local files |
| Shell command | Show exact command and device | Warn that shell can modify the device |
| Wireless ADB | Explain network exposure | Prefer pairing; do not enable `tcpip` automatically |
| Screen recording | Show recording indicator and storage path | Recordings may contain sensitive data |

Additional rules:

- Never auto-accept the device RSA authorization prompt; show instructions and a retry action.
- Prefer Android 11+ wireless pairing over `adb tcpip`; warn that `tcpip` exposes the device on the network.
- Remember wireless devices only with user consent; provide a forget action.
- Show the exact device name and serial in destructive confirmations when multiple devices are connected.
- Warn when an APK requests dangerous permissions or targets an older SDK.
- Do not upload screenshots, recordings, logs, or device data anywhere.
- Store screenshots and recordings in the session folder or a user-chosen folder; show the path.
- Device reports and logcat may contain personal or app data; warn before export or sharing.
- Provide a clear-data action for DroidDeck session logs and a retention policy.

### 19.7 Local data protection

- Default data directory: `%LocalAppData%/DroidDeck`, protected by the user's profile ACLs.
- Portable mode stores data next to the executable only when a marker file or `--data-dir` is present.
- Warn when portable data is placed on a shared, synced, or network drive.
- Settings, project configs, and trust records use atomic writes and backups.
- Secrets use DPAPI; non-secret settings are plain JSON for inspectability.
- Session logs are plain text by design; document that they may contain sensitive app output.
- Retention is configurable by age and size; cleanup runs on startup and on demand.
- Crash dumps are opt-in and stored locally; they may contain memory contents.
- Diagnostics exports are user-initiated and reviewed before creation.
- Uninstall or reset offers keep-data or delete-data; deletion is explicit.

### 19.8 Privacy

DroidDeck is local-first:

- No accounts, sign-in, telemetry, analytics, advertising, or cloud sync.
- No machine ID, device fingerprint, or usage tracking.
- No background network calls by default.

Network calls:

| Call | When | Data sent | Default |
|---|---|---|---|
| Catalog refresh | Manual or user-enabled schedule | HTTP request, user agent, optional bearer token | Off |
| Plugin download | User clicks install/update | HTTP request, user agent | Manual only |
| App update check | User enables it or clicks check | HTTP request, user agent | Off |
| Documentation link | User clicks | Opens default browser | Manual |
| Issue report | User opens GitHub | User-controlled content | Manual |

Disclosure:

- Catalog and update requests reveal the user's IP address to the host.
- The user agent is `DroidDeck/<version>`; no additional identifiers are added.
- Authentication tokens are sent only to the configured source over HTTPS.
- No data is sold, shared, or used for analytics.
- A plain-language privacy document ships with the app and is linked from About.

### 19.9 Application hardening

- Run as the current user with `asInvoker`; never elevate.
- Single-instance activation uses a named pipe with a restrictive ACL.
- Do not open a local HTTP listener in production; tests use an isolated local server.
- Do not enable remote debugging, WebView devtools, or diagnostic endpoints in release builds.
- If a WebView is used for rendered content, disable scripting and remote content; prefer native rendering.
- Sanitize Markdown and catalog descriptions; block HTML, scripts, iframes, and remote images by default.
- Strip ANSI and control sequences from tool output before display.
- Validate plugin archive paths and sizes before extraction.
- Verify update and catalog downloads with SHA-256; verify signatures when configured.
- Sign release binaries and publish checksums.
- Keep dependencies patched; run audit and license checks in CI.
- Generate an SBOM for every release.
- Use secure defaults: plugins off in safe mode, catalog auto-check off, auto-install never, telemetry none.
- Ensure crash handlers do not include secrets or full environment dumps.

### 19.10 Safety UX patterns

Confirmation dialog template:

```text
Uninstall com.example.myapp?
Device: POCO F1 (serial 9f2c1a7d)
This removes the app and its data from the device. This cannot be undone.
[Cancel] [Uninstall]
```

Rules:

- The primary destructive action is not the default focused button.
- The dialog names the device and the exact target.
- The consequence is stated in plain language.
- A dry run or command preview is available for run configs and custom steps.
- Cancel is always available for long-running work.
- Errors explain what happened, what was not changed, and the next action.
- Safe mode is reachable from the startup failure screen and the plugin manager.
- Corrupt configuration is quarantined, not silently deleted.
- Resource guards warn before opening many mirrors, recordings, or builds.
- Settings changes that require restart are labeled before saving.

### 19.11 Compliance and licensing

- Bundle scrcpy under its Apache-2.0 license with LICENSE, NOTICE, version, and source link.
- Do not bundle adb, platform-tools, or the Android SDK; detect and use the user's installation.
- Include .NET, Windows App SDK, and NuGet license notices.
- Generate a third-party notices file as part of the release.
- Provide an About dialog with app version, runtime version, Windows App SDK version, scrcpy version, and license links.
- Include an Android trademark disclaimer: Android is a trademark of Google LLC; DroidDeck is not affiliated with Google.
- Publish a privacy document and a security disclosure policy.
- Target WCAG 2.1 AA-equivalent behavior for the desktop UI where applicable.
- No export-controlled cryptography is implemented beyond standard TLS and DPAPI.

### 19.12 Incident response

- Publish a security contact and a private disclosure process.
- Acknowledge reports, triage severity, and coordinate a fix and advisory.
- For a compromised release: yank the release, publish an advisory, publish a fixed build, and update checksums.
- For a compromised catalog: document removal steps and advise users to remove the source, clear cache, and review trusted plugins.
- Trust pins change when a plugin hash changes; users must explicitly re-trust.
- Safe mode is the first troubleshooting step for suspected plugin issues.
- There is no central kill switch; the app is local-first and user-controlled.
- Release notes state security-relevant changes plainly.


## 20. Packaging, distribution, and updates

### 20.1 Strategy

| Aspect | Portable (v1) | MSIX (post-v1) |
|---|---|---|
| Install | Unzip and run | Installer or App Installer |
| Updates | Manual, optional check | App Installer automatic updates |
| Plugins | Any trusted folder | Full-trust packaged app with data-folder plugins |
| File associations | None in v1 | Possible later |
| Data location | `%LocalAppData%` or portable folder | Same user data location |
| Signing | Recommended, required for public releases | Required |
| Uninstall | Delete folder | Apps and Features |
| Enterprise | Easy to copy and pin | Manageable via policies |
| Default | Yes | Later |

Portable first because it is fast to ship, easy to debug, friendly to plugin developers, and works without installer infrastructure. MSIX follows once the app identity, update path, and signing pipeline are stable.

### 20.2 Portable layout

```text
DroidDeck-1.0.0-win-x64/
  DroidDeck.exe
  DroidDeck.dll
  DroidDeck.*.dll
  runtimes/
  Assets/
  Tools/
    scrcpy/
      scrcpy.exe
      scrcpy-server
      SDL2.dll
      licenses/
  Licenses/
    DOTNET-LICENSE.txt
    WINDOWSAPPSDK-LICENSE.txt
    SCRCPY-LICENSE.txt
    THIRD-PARTY-NOTICES.txt
  README.txt
  CHANGELOG.md
  portable.marker          (optional)
```

Rules:

- The app never writes to its install folder unless portable mode is explicitly enabled.
- Default data lives in `%LocalAppData%/DroidDeck`.
- Portable mode is enabled by `portable.marker` next to the executable or by `--data-dir <path>`.
- The portable folder can be copied to another machine; machine-specific paths are re-detected.
- Do not bundle adb or the Android SDK.
- Bundle a pinned scrcpy build with notices.
- Keep the layout flat enough to avoid long-path problems.

### 20.3 Build and publish

```powershell
dotnet publish src/DroidDeck.App/DroidDeck.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:WindowsPackageType=None `
  -p:WindowsAppSDKSelfContained=true `
  -p:PublishReadyToRun=true `
  -p:PublishSingleFile=false `
  -p:PublishTrimmed=false `
  -p:DebugType=portable `
  -p:Deterministic=true `
  -o artifacts/publish/win-x64
```

- Single-instance activation uses `Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("DroidDeck.MainInstance")` combined with a user-scoped Named Pipe (`PipeOptions.CurrentUserOnly`) to forward unpackaged CLI arguments (e.g., dropped `.apk` paths) and `SetForegroundWindow` to focus the running main window.

Rules:

- Pin the .NET SDK with `global.json`.
- Pin package versions with central package management and locked restore.
- Use `WindowsAppSDKSelfContained=true` so the Windows App SDK runtime is not a prerequisite.
- Do not trim in v1; WinUI trimming is not reliable enough.
- Do not use single-file publishing in v1; it complicates native dependencies and plugin loading.
- Measure ReadyToRun: it improves startup but increases size; keep it if the startup win is meaningful.
- Generate SourceLink and symbols; publish symbols separately.
- Stamp assembly, file, and informational versions from one source.
- Build the portable zip from the publish output; do not include test files, PDBs, or caches.
- Generate SHA-256 checksums for the zip and the executable.
- Validate the publish output on a clean Windows Sandbox or VM with no .NET SDK.

### 20.4 Runtime prerequisites

- Windows 11 primary; Windows 10 22H2 best effort at the documented minimum.
- .NET runtime is self-contained; no separate .NET install.
- Windows App SDK runtime is self-contained; no separate Windows App SDK install.
- Validate whether the Visual C++ runtime is required by WinUI or scrcpy; document or bundle only if the license allows.
- Avoid a WebView2 dependency unless a feature truly needs it; Windows 11 includes it, older machines may not.
- Android SDK, adb, JDK, Gradle, Flutter, Node, MAUI workload, and Unity are user-provided and detected.
- scrcpy is bundled; its server is pushed to the device at runtime.
- The app does not require administrator rights.

### 20.5 First run and data location

- Create the data directory and subfolders on first run.
- Run toolchain discovery in the background; show the onboarding checklist.
- Do not block on a missing tool; show a fix action.
- Restore the last project and device selection if available.
- Show a one-time note about local-only data and where logs are stored.
- If the data directory is not writable, offer a portable or alternate location.
- If settings are corrupt, quarantine them and start with defaults, with a visible notice.
- Keep a backup of the previous settings file on every schema migration.

### 20.6 MSIX (post-v1)

- Package as a full-trust desktop app with the `runFullTrust` capability.
- Target x64; add ARM64 only if the product decision supports it.
- Use App Installer for updates and delta downloads.
- Sign the package and timestamp the signature.
- Support `.apk` file association and a `droiddeck://` protocol only after the core flows are stable.
- Plugins continue to load from the user data directory; developer mode remains portable-first.
- Migrate settings from portable mode on first launch.
- Keep the portable build available for plugin developers and users who cannot install MSIX.
- Document enterprise deployment, allowlists, and update policies.

### 20.7 Updates

v1 update policy:

| Setting | Default |
|---|---|
| Automatic update check | Off |
| Check frequency if enabled | Weekly |
| Automatic download | Off |
| Automatic install | Never |
| Update channel | Stable |
| Signature verification | Required for published releases |
| Rollback | Keep previous portable folder and data backup |

Behavior:

- A manual check opens the release page or queries a configured HTTPS release feed.
- The app shows version, date, size, changelog, and checksum.
- The user downloads and verifies the package; the app does not silently replace itself in v1.
- Portable updates replace the program folder, not the data folder.
- Keep the previous version until the new version starts successfully.
- If the new version fails to start, the user can restore the previous folder.
- MSIX updates use App Installer with signed packages and rollback where supported.
- Config schema migrations are forward-only with backups; downgrades warn about incompatible settings.
- Plugin API compatibility is checked before install; incompatible plugins are disabled, not deleted.

### 20.8 Signing and trust

- Sign public release binaries with a code-signing certificate and timestamp.
- Publish SHA-256 checksums and an SBOM with every release.
- Unsigned developer builds are labeled clearly and are not presented as official releases.
- SmartScreen reputation is expected to be poor for a new unsigned publisher; signing and consistent releases reduce friction.
- Catalog signatures are optional in v1; the UI shows verified, unverified, or absent.
- Plugin signatures are optional; hash pinning is mandatory.
- Never disable certificate validation to make a download work.

### 20.9 Release process

1. Freeze the release branch and confirm scope.
2. Bump the version in one place; update the changelog.
3. Run the full CI pipeline, device matrix, soak, and security tests.
4. Publish the portable build from the tagged commit.
5. Sign binaries and generate checksums, SBOM, and third-party notices.
6. Smoke test on a clean Windows Sandbox or VM: launch, detect adb, add project, run, mirror, settings.
7. Verify upgrade from the previous version and data migration.
8. Update docs, known issues, and plugin compatibility notes.
9. Tag the release and attach artifacts.
10. Publish release notes with security-relevant changes called out.
11. Monitor issue reports; there is no telemetry, so rely on user reports and reproducible diagnostics.
12. If a blocker is found, yank the release and publish a fixed build with an advisory.

Release checklist:

- [ ] Version and changelog updated
- [ ] CI green and required checks passed
- [ ] Device matrix passed
- [ ] Eight-hour soak passed
- [ ] Security tests passed
- [ ] Portable build verified on a clean machine
- [ ] Upgrade and rollback verified
- [ ] Signed and timestamped
- [ ] Checksums and SBOM published
- [ ] Third-party notices updated
- [ ] Docs and known issues updated
- [ ] Release notes reviewed
- [ ] Artifacts attached and tagged

### 20.10 Size and performance

- Target extracted portable size under 250 MB and zip under 120 MB; measure every release.
- Major contributors: .NET self-contained runtime, Windows App SDK, WinUI assets, scrcpy, and symbols.
- Keep symbols out of the portable zip; publish them separately.
- Avoid bundling redundant native runtimes; publish only win-x64 in v1.
- ReadyToRun improves startup at a size cost; decide per release with measurements.
- Do not trim in v1.
- Compress with ZIP for compatibility; do not require 7-Zip.
- Track startup time on the low-end reference machine, not only the development machine.

### 20.11 Licensing and notices

- Include scrcpy LICENSE, NOTICE, version, and source link.
- Include .NET, Windows App SDK, and NuGet notices.
- Do not redistribute Android SDK, platform-tools, adb, or JDK.
- Include the Android trademark disclaimer.
- Include the DroidDeck license and privacy document.
- Generate notices from package metadata and review manually before release.
- Keep license texts in `Licenses/` and link them from About.

### 20.12 Support and diagnostics

- Provide a diagnostics bundle with app version, OS version, runtime version, Windows App SDK version, tool paths and versions, adb state, device list, plugin list and health, effective settings with sources, recent errors, and redacted logs.
- Show a review screen before export; allow excluding logs and paths.
- Provide a GitHub issue template requesting the diagnostics bundle and reproduction steps.
- Document safe mode, config reset, and data folder location.
- Publish a support policy: current stable release plus the previous minor for critical fixes.
- Do not collect diagnostics automatically.


## 21. POC plan

### 21.1 Purpose and ground rules

The POCs answer the highest-risk technical questions before the product commits to an architecture. They are throwaway code, not the product.

- Timebox each POC to one to three days.
- Put code under `poc/<id>/` and results under `docs/poc/<id>.md`.
- Use the reference machine, POCO F1, and Pixel API 35 emulator.
- Define pass criteria before starting; do not move the goalposts.
- Stop when the question is answered; do not polish.
- A failed POC is useful; record the fallback and the trade-off.
- Critical POCs gate the architecture review at the end of M0.
- Any POC that changes a locked decision requires an ADR.

| ID | Question | Timebox | Gate |
|---|---|---|---|
| POC-01 | Does WinUI 3 on .NET 10 and Windows App SDK 2.5.x build, run, and publish portably? | 2 days | Critical |
| POC-02 | Can adb device tracking be event-driven, fast, and resilient? | 2 days | Critical |
| POC-03 | Can logcat sustain 10k lines/s with a responsive UI and bounded memory? | 3 days | Critical |
| POC-04 | Can scrcpy be embedded reliably, with an external-window fallback? | 3 days | Critical |
| POC-05 | Does the Gradle build/install/launch/logcat pipeline meet the two-minute goal? | 3 days | Critical |
| POC-06 | Does build-once, install-many work with independent sessions and partial failure? | 2 days | Critical |
| POC-07 | Does the portable self-contained build run on a clean Windows machine? | 2 days | Critical |
| POC-08 | Can a full-trust plugin add a detector, action, page, and renderer without destabilizing the host? | 3 days | Critical |
| POC-09 | Can the catalog install, update, trust, and roll back a plugin safely? | 2 days | High |
| POC-10 | Does layered configuration with a source map meet the performance and UX goals? | 2 days | High |
| POC-11 | Can the renderer host select, isolate, and dispose plugin renderers? | 2 days | High |

### 21.2 POC-01: WinUI 3 shell on .NET 10 and Windows App SDK 2.5.x

**Question:** Does a WinUI 3 unpackaged app build and run on .NET 10 with Windows App SDK 2.5.x, including a custom title bar, Mica, navigation, and self-contained portable publish?

**Method:**

1. Install the official WinUI templates.
2. Create an unpackaged WinUI 3 app targeting .NET 10 and Windows App SDK 2.5.x.
3. Add a custom title bar, `NavigationView`, Mica backdrop with fallback, dark/light theme switching, and a placeholder page.
4. Publish self-contained with `WindowsAppSDKSelfContained=true`.
5. Run on the development machine and in Windows Sandbox.
6. Measure cold start, first frame, and shutdown.

**Pass criteria:**

- Builds with warnings as errors and no missing workload.
- Launches unpackaged with no separate Windows App SDK runtime install.
- Mica works where supported and falls back cleanly.
- Custom title bar drag, maximize, restore, and close work at 100% and 150% scaling.
- Portable publish runs in Windows Sandbox.
- Cold start to interactive under 2.0 s p50 on the reference machine.
- Clean shutdown with no orphan processes.

**Fallback:** Pin a different Windows App SDK patch; use a packaged development build while keeping portable publish for release; document any required runtime prerequisite.

**Output:** `docs/poc/01-winui.md`, measured startup trace, publish command, and an ADR if the version pin changes.

### 21.3 POC-02: adb device tracker

**Question:** Can DroidDeck track devices with a persistent `host:track-devices-l` TCP socket (`127.0.0.1:5037`), refresh metadata in a single batched shell call, and recover from `adb kill-server` without polling?

**Method:**

1. Connect to `127.0.0.1:5037`, send `0014host:track-devices-l`, and parse 4-hex-byte length-framed `-l` device events (auto-starting `adb start-server` on `ConnectionRefused`).
2. On online transition, batch all device metadata (`getprop`, `wm`, `dumpsys battery`, `ip`) into one sectioned shell call per device.
3. Unplug and replug the POCO F1; start and stop two emulators.
4. Kill the adb server while tracking; verify recovery.
5. Put the device in unauthorized and offline states.
6. Measure hotplug latency and idle CPU.

**Pass criteria:**

- Hotplug visible in under 300 ms p50.
- No duplicate devices after bursts.
- Metadata complete in under 500 ms per device.
- Recovers automatically after `adb kill-server` and server restart.
- Unauthorized and offline states show actionable guidance.
- Idle CPU under 1%.
- No polling loop.

**Fallback:** Use a 500 ms polling fallback behind `IDeviceService`, clearly documented and disabled when track-devices works.

**Output:** tracker state diagram, event samples, failure recovery notes, and latency measurements.

### 21.4 POC-03: logcat throughput and log UI

**Question:** Can the log pipeline ingest 10k lines/s for ten minutes while the UI remains responsive and memory stays bounded?

**Method:**

1. Build a replay harness that emits realistic threadtime lines, including multi-line stack traces, at 1k, 10k, and 50k lines/s.
2. Feed the same parser, filter, ring buffer, disk writer, and batched UI used by the product skeleton.
3. Render into a virtualized list with 20k-line buffer and 50 ms batches.
4. Scroll, search, filter, and switch sessions during the run.
5. Run against real logcat from the POCO F1 as a sanity check.
6. Measure frame times, memory, CPU, dropped lines, and disk lag.

**Pass criteria:**

- Sustained 10k lines/s for ten minutes.
- UI frame p95 under 16.7 ms while streaming and scrolling.
- UI buffer memory under 50 MB.
- Disk log contains every line; UI dropped-line counter is accurate.
- Crash detection within 1 s of the fatal line.
- Search over 20k lines under 200 ms.
- No UI-thread blocking and no unbounded queue growth.

**Fallback:** Reduce the UI buffer, increase batching, render only visible rows, cap display rate while preserving disk truth, and use a custom virtualized surface if `ListView` cannot meet the budget.

**Output:** throughput report, frame trace, memory trace, and the chosen UI virtualization approach.

### 21.5 POC-04: scrcpy embedding and external fallback

**Question:** Can scrcpy be embedded into a WinUI 3 window reliably enough for v1, and is the external-window fallback acceptable?

**Method:**

1. Launch scrcpy with a fixed serial and window title.
2. Find the top-level HWND by process ID and title.
3. Create a child host HWND in the WinUI window and call `SetParent`.
4. Remove caption and border styles; handle resize, DPI change, focus, z-order, and visibility.
5. Exercise touch, keyboard, scroll, and clipboard for 30 minutes.
6. Close the host, navigate away, disconnect the device, and kill scrcpy.
7. Implement the external-window fallback with title, position, and always-on-top options.

**Pass criteria:**

- Embedded session stable for 30 minutes without crashes or lost input.
- Resize and DPI changes do not break rendering.
- Input latency under 150 ms.
- Clean close with no orphan scrcpy process or window handle.
- External fallback works when embedding fails.
- The UI depends only on `IScreenSession`; swapping backends requires no page changes.

**Fallback:** Ship managed external scrcpy windows in v1 with clear positioning and focus behavior. Embedding remains behind a flag until stable.

**Output:** embedding notes, failure modes, fallback behavior, and the `IScreenSession` contract.

### 21.6 POC-05: Gradle end-to-end pipeline

**Question:** Can DroidDeck build, locate, inspect, install, launch, and attach logcat for a Gradle project within the two-minute first-run goal, with useful error parsing?

**Method:**

1. Use `samples/hello-android` and a flavored sample.
2. Detect the wrapper, module, variant, application ID, and launcher.
3. Run `gradlew.bat :app:assembleDevDebug`.
4. Locate the APK, inspect with `aapt2`, install, and launch with `am start -W`.
5. Attach logcat and resolve the PID.
6. Test a Kotlin compile error, a Gradle configuration error, an install signature mismatch, and a missing activity.
7. Measure first run, incremental run, and cancellation.

**Pass criteria:**

- First run under two minutes on the reference machine.
- Incremental build/install/launch under 30 seconds on the sample.
- Exact commands shown in the session timeline.
- Kotlin/Java errors are clickable with file and line.
- Package and launcher resolved without manual input.
- Cancel leaves no Gradle or adb child processes.
- JDK/Gradle/AGP mismatch produces a warning, not a dead end.

**Fallback:** Add manual artifact, package, and activity overrides; use the CLI wrapper path; surface the raw command and log tail.

**Output:** pipeline trace, timing table, error parser samples, and required overrides.

### 21.7 POC-06: multi-device orchestration

**Question:** Can DroidDeck build once, install on multiple devices in parallel, keep independent sessions, and handle partial failure?

**Method:**

1. Connect the POCO F1 and two emulators.
2. Run the same Gradle config on all three.
3. Build once, install in parallel, launch, and attach separate logcat streams.
4. Make one device fail install deliberately.
5. Cancel one device while the others continue.
6. Retry the failed device only.

**Pass criteria:**

- One build, three installs.
- Independent session state and logs per device.
- Aggregate state becomes `PartiallyFailed` with a clear reason.
- Retry failed devices does not rebuild unnecessarily.
- Per-device locks prevent overlapping mutations.
- No cross-device log mixing or UI freeze.

**Fallback:** Serialize installs; allow per-device build for CLI-only stacks; keep the same session model.

**Output:** orchestration state diagram, concurrency limits, and partial-failure UX notes.

### 21.8 POC-07: portable publish on a clean machine

**Question:** Does the self-contained portable build run on a clean Windows machine with no .NET SDK, no Windows App SDK runtime, and no Android SDK?

**Method:**

1. Publish to `artifacts/publish/win-x64`.
2. Copy the folder to Windows Sandbox or a clean VM.
3. Launch, open Settings, and run the toolchain doctor.
4. Add an APK-only project and inspect it.
5. Connect a device if USB passthrough is available; otherwise verify the no-device state.
6. Measure startup, size, and shutdown.
7. Delete the folder to simulate uninstall.

**Pass criteria:**

- Launches with no missing DLL or runtime prompt.
- Uses `%LocalAppData%` for data by default.
- Toolchain doctor explains missing Android SDK/adb without crashing.
- scrcpy is present and reports its version.
- Portable mode works with a marker file or `--data-dir`.
- Extracted size and zip size are within budget.
- No administrator rights required.

**Fallback:** Document a required Visual C++ runtime or bundle it if licensing allows; keep a framework-dependent build for developers.

**Output:** clean-machine report, dependency list, size breakdown, and publish script.

### 21.9 POC-08: plugin host and UI extension

**Question:** Can a full-trust plugin register a detector, device action, navigation page, and renderer, while failures are isolated and shared contracts keep type identity?

**Method:**

1. Create a sample plugin with a manifest, entry point, detector, device action, page, and log detail renderer.
2. Load it from a folder in developer mode and from a `.ddplugin` package.
3. Verify shared contract assemblies resolve from the host.
4. Add a second plugin with a private dependency.
5. Add a plugin that throws in initialization, a detector, an action, a UI factory, and a renderer.
6. Add a version-mismatched plugin and a missing-dependency plugin.
7. Disable, re-enable, update, and roll back with restarts.

**Pass criteria:**

- All extensions appear and work.
- Shared contract type identity is preserved.
- Throwing extensions are isolated; the host and other plugins continue.
- Failed UI factories show a fallback panel.
- Version and dependency errors are explained in the plugin manager.
- Five plugins add under 300 ms to startup after first frame.
- No plugin can be silently installed or trusted.

**Fallback:** Reduce the UI surface if a specific WinUI contract is unstable, but keep detector, action, command, settings, and renderer extensions. Do not claim sandboxing.

**Output:** plugin host notes, ALC strategy, failure matrix, and SDK contract adjustments.

### 21.10 POC-09: catalog install, update, and rollback

**Question:** Can the catalog browse, download, verify, trust, install, update, and roll back a plugin without auto-install or silent trust?

**Method:**

1. Serve a local catalog over HTTPS and from a file.
2. Include stable and prerelease versions, a missing hash, a wrong hash, an unsigned package, and a capability change.
3. Install a plugin, trust it, update it, and roll back.
4. Tamper with a package and verify the install aborts.
5. Disable the network and verify cached/offline behavior.
6. Verify audit entries and trust re-prompts.

**Pass criteria:**

- Hash mismatch aborts and deletes the temporary file.
- Trust prompt appears before extraction or first load.
- Capability change triggers a new trust prompt.
- Update keeps the previous version for rollback.
- Failed update rolls back and leaves the app usable.
- No auto-install or auto-update path exists.
- Offline mode uses cache and explains staleness.
- Audit log records the decisions.

**Fallback:** Ship local file catalogs only in v1 if remote hosting is not ready; keep the same schema and install pipeline.

**Output:** catalog schema draft, install state machine, trust UX, and audit format.

### 21.11 POC-10: configuration layering and source map

**Question:** Does the layered configuration system resolve quickly, show correct sources, and support JSON editing without blocking the UI?

**Method:**

1. Define defaults for appearance, tools, run, logs, screen, features, plugins, and catalog.
2. Create global, profile, project, run config, device, session, and CLI overrides.
3. Resolve effective values and source badges.
4. Edit JSON with schema validation, unknown-key preservation, and reset.
5. Change a value and measure propagation.
6. Corrupt a file and verify quarantine and recovery.

**Pass criteria:**

- Correct precedence for every scope.
- Device specificity: serial over group over all.
- Cached resolve under 1 ms.
- Settings page load under 200 ms.
- Change propagation under 100 ms.
- Unknown keys preserved on round-trip.
- Corrupt file quarantined; app remains usable.
- Secrets excluded from export.

**Fallback:** Precompute immutable snapshots per scope and invalidate on change; keep JSON editing as the escape hatch.

**Output:** scope matrix, source-map model, schema draft, and performance numbers.

### 21.12 POC-11: renderer host and log detail

**Question:** Can the renderer host select plugin renderers, create them quickly, fall back on failure, and dispose them cleanly without touching the log hot path?

**Method:**

1. Implement built-in log detail, JSON, stack trace, chart, file preview, test result, and data view renderers.
2. Add plugin renderers for log detail, chart, and file preview.
3. Add a renderer that throws in `CanRender` and one that throws in `CreateView`.
4. Switch renderers from the View as menu.
5. Open and close the detail pane repeatedly.
6. Measure creation time, memory, and disposal.

**Pass criteria:**

- Renderer selection respects kind, priority, and user default.
- Creation under 50 ms for built-ins and sample plugins.
- Failure falls back to the built-in view with an error banner.
- Repeated failures disable the renderer.
- No renderer is invoked for every log line by default.
- Disposal releases subscriptions and image resources.
- High contrast and theme resources work.

**Fallback:** Ship built-ins plus plugin log detail, chart, file preview, test result, and data view; defer plugin `LogLine` renderers if performance is unacceptable.

**Output:** renderer contract, selection rules, fallback behavior, and performance report.

### 21.13 POC exit criteria

- All critical POCs pass or have an accepted fallback recorded in an ADR.
- The architecture is updated to match the evidence.
- Performance budgets are validated or revised with data.
- The plugin and catalog contracts have a working sample.
- The portable publish path is proven on a clean machine.
- No unresolved blocker remains for M1.
- Results are summarized in `docs/poc/README.md` and reviewed before M1 starts.


## 22. Milestones and build order

### 22.1 Principles

- Build vertical slices that always run, not horizontal layers that only compile.
- Each milestone ends with a demo on real devices and a written acceptance review.
- Quality gates are part of the milestone, not a later phase.
- Plugin work starts in M0 and is distributed across every milestone.
- No deadline means no scope cuts; milestones are engineering order, not a promise of dates.
- Dogfood DroidDeck on its own sample projects from M1 onward.
- Keep the portable build green from M0 so the app is always usable.

Indicative effort (not a schedule):

| Milestone | Indicative effort | Focus |
|---|---|---|
| M0 | 3-4 weeks | Foundation, POCs, shell, adb, config, publish |
| M1 | 5-6 weeks | Gradle/KMP/APK/custom run loop, logs, sessions |
| M2 | 5-6 weeks | Multi-device, screen, emulator, wireless, toolbox |
| M3 | 7-9 weeks | Flutter, RN/Expo, MAUI, Capacitor/Cordova, Unity, pro tools |
| M4 | 7-9 weeks | Customization, plugin SDK, catalog, renderers, hardening, release |

### 22.2 M0: Foundation and de-risking

**Goal:** Prove the risky parts and build the skeleton every later milestone depends on.

**Deliverables:**

- Solution layout, `global.json`, central package management, build props, analyzers, and CI skeleton.
- WinUI 3 shell with custom title bar, `NavigationView`, theme, DI composition, logging, single-instance activation, and settings storage.
- Process runner with `ArgumentList`, job objects, cancellation, timeouts, ANSI handling, and bounded output.
- adb client, `track-devices` tracker, metadata batching, device list UI, and toolchain doctor.
- Configuration skeleton with defaults, global scope, source map, atomic writes, and DPAPI secrets.
- Plugin abstractions v0 and built-in implementations behind the same contracts.
- Portable self-contained publish pipeline and clean-machine smoke test.
- POC results and ADRs for any changed decisions.
- Test fixtures for adb parsers, process runner, and basic project detection.

**Acceptance criteria:**

- App launches from the portable build on a clean Windows machine.
- A connected device appears in under 300 ms and shows correct metadata.
- Toolchain doctor identifies SDK, adb, JDK, scrcpy, and missing tools with fix actions.
- Settings persist and survive restart; corrupt settings recover.
- No UI-thread blocking on startup or device refresh.
- Unit and integration tests pass in CI; warnings are errors.
- Critical POCs pass or have accepted fallbacks.

**Demo:** Launch the portable app, connect a device, see it appear, open the doctor, change a setting, restart, and confirm persistence.

**Plugin work:** Contracts, DTOs, manifest model, built-in adapters compiled against the contracts, and a loader spike.

### 22.3 M1: Core run loop for Gradle, KMP, APK, and custom projects

**Goal:** Make the daily Android run loop real for the foundation stacks.

**Deliverables:**

- Add-project flow with detection review, evidence, modules, variants, warnings, and custom fallback.
- Gradle and KMP detection and adapters; APK-only projects; custom script projects.
- Project persistence, monorepo project list, refresh detection with diff.
- Run configuration editor basics: build, artifact, install, launch, logs, screen, devices, steps.
- Run pipeline: prepare, build once, locate artifact, inspect, install, launch, attach logcat, monitor, finish.
- Single-device session workspace with step timeline, live logs, filters, search, crash/ANR banner, and actions.
- Session history, persistent logs, export, retention, and notifications.
- Gradle/Kotlin/Java error parsing with clickable locations.
- Built-in features implemented through the plugin contracts.
- Plugin loader with manifest validation, folder install in developer mode, and a sample command or device action.

**Acceptance criteria:**

- Add a Gradle project and run it on a device in under two minutes.
- Incremental run works without a clean build.
- Variants are detected and selectable; manual override exists.
- APK-only install, inspect, and launch work, including split APKs.
- A custom script project can define and run a pipeline with placeholders.
- Crash and ANR are detected and actionable.
- Cancel during build, install, and launch leaves no orphan processes.
- Logs persist and reopen read-only from history.
- Detection never invokes Gradle.
- Tests cover detection, pipeline state, parsers, and failure recovery.

**Demo:** Add the sample Gradle app, run a flavored debug build, watch the timeline, open the mirror, trigger a crash, restart, and export the log.

**Plugin work:** Loader, manifest validation, folder install, sample command/device action, built-in adapters registered through the same registry.

### 22.4 M2: Devices, screen, and multi-device

**Goal:** Make multiple devices and screen control first-class.

**Deliverables:**

- Multi-device run groups, device picker, groups and tags, per-device tabs, aggregate status, partial failure, and retry failed devices.
- Per-device mutation locks and global concurrency limits.
- Emulator list, launch, cold boot, snapshot, wipe data, and stop.
- Wireless ADB pairing, mDNS discovery, connect, remember, and auto-reconnect.
- scrcpy integration with embedded or external windows, pop-out mirror, screenshots, recording, thumbnails, and device buttons.
- Device toolbox: apps, files, shell, ports, permissions, deep links, broadcasts, reboot, performance, and UI inspector entry point.
- Resource guard for many mirrors, recordings, and builds.
- Plugin device actions, device tabs, and toolbar actions.
- Multi-device log fairness and per-device session controls.

**Acceptance criteria:**

- One build installs on three devices with independent logs.
- One failed install produces `PartiallyFailed` and can be retried without rebuilding.
- Mirroring runs at 30-60 fps with input under 150 ms on the reference device.
- Pop-out mirror works across monitors and cleans up on close.
- Wireless pairing works on Android 11+ and survives a reconnect.
- Toolbox actions confirm destructive operations and show exact commands.
- Per-device locks prevent overlapping installs.
- A plugin device action appears in the toolbar and runs safely.

**Demo:** Run one project on the physical device and two emulators, mirror all three, fail one install, retry it, pair a wireless device, and run a plugin action.

**Plugin work:** Device actions, device tabs, toolbar actions, failure isolation, and plugin health reporting.

### 22.5 M3: Stack breadth and professional tools

**Goal:** Support every selected stack honestly and add the daily power tools.

**Deliverables:**

- Flutter adapter with build, install, launch, hot reload, hot restart, stop, and tests.
- React Native and Expo adapters with Metro management, `adb reverse`, reload, and managed-workflow limitations.
- .NET MAUI adapter with workload detection, build, install, launch, and tests.
- Capacitor and Cordova adapters with sync/run paths and multi-device fallback.
- Unity detection, open-in-Unity, APK install, and custom Editor build method template.
- KMP/Compose Multiplatform full Gradle path.
- Variant and build-type support across stacks.
- Test runner with Gradle, Flutter, Jest/custom, .NET, and JUnit XML parsing; results UI and rerun failed.
- Performance metrics: launch time, FPS/jank, CPU, memory, battery, CSV export.
- UI hierarchy inspector with tree, screenshot overlay, and tap element.
- APK drop and inspection flow.
- Command palette, remappable shortcuts, and conflict detection.
- Stack-specific error parsing and toolchain doctor entries.

**Acceptance criteria:**

- Every stack sample either runs through the full path or shows a clear Limited/Custom badge with exact steps.
- Flutter hot reload and hot restart work on a device.
- React Native Metro starts, `adb reverse` is applied, and reload works.
- MAUI builds and launches with the workload present; missing workload shows guidance.
- Unity never pretends to have a generic one-click build.
- Test results show suite/case status, duration, stack trace, and attachments.
- Performance metrics and UI inspector work on the reference device.
- APK drop installs and launches on selected devices.
- Command palette covers run, stop, mirror, screenshot, navigation, and settings.

**Demo:** Run Flutter with hot reload, React Native with Metro, MAUI, and an APK drop; run a test suite; open performance and the UI inspector; use the command palette.

**Plugin work:** Run steps, test runners, log parsers, commands, stack adapters, and per-stack extension samples.

### 22.6 M4: Customization, plugin ecosystem, and release hardening

**Goal:** Deliver the fully customizable, extensible product and ship a quality v1.

**Deliverables:**

- Full configuration layering, source map, scope switching, profiles, presets, feature visibility, JSON editing, import/export, themes, and keybindings.
- Plugin SDK 1.0: manifest, contracts, loader, trust, hash pinning, settings schema, UI pages, home widgets, project/device tabs, session panels, toolbar actions, settings sections, commands, themes, log parsers, test runners, and renderers.
- Plugin manager with install, enable/disable, update, rollback, logs, data folder, safe mode, and diagnostics.
- Plugin templates, sample plugins, API reference, and authoring docs.
- Catalog sources, browse, search, compatibility, install, update, rollback, signatures, offline cache, and audit.
- Renderer SDK with built-in and sample log detail, chart, file preview, test result, and data view renderers.
- Accessibility pass, localization readiness, high contrast, and reduced motion.
- Performance hardening, soak, security review, and release checklist.
- Documentation: user guide, settings reference, plugin SDK, catalog schema, troubleshooting, privacy, and security.

**Acceptance criteria:**

- A plugin can add a detector, run step, device action, command, page, home widget, and renderer through the documented SDK.
- Plugin install requires trust and hash pinning; update and rollback work; safe mode disables plugins.
- Catalog install never auto-trusts or auto-installs; hash mismatch aborts.
- Configuration precedence is correct across all scopes and visible in the UI.
- Profiles and presets can be created, applied, exported, and imported without secrets.
- Unused stacks, toolbox tabs, and home widgets can be hidden without deleting config.
- Themes validate contrast and support high contrast.
- Performance budgets pass on the reference machine.
- Eight-hour soak passes with no meaningful leak.
- Security tests pass and known risks are documented.
- Portable release passes the clean-machine checklist.

**Demo:** Install a sample plugin from a local catalog, trust it, use its detector/action/page/renderer, change profiles, hide unused features, export config, and run the release checklist.

**Plugin work:** Full SDK, catalog, renderers, templates, docs, trust, rollback, and sample plugins.

### 22.7 Plugin work across milestones

| Milestone | Plugin work |
|---|---|
| M0 | Abstractions, DTOs, manifest model, built-in contracts, loader spike |
| M1 | Loader, manifest validation, folder install, sample command/action, built-in adapters |
| M2 | Device actions, device tabs, toolbar actions, failure isolation, health |
| M3 | Run steps, test runners, log parsers, commands, stack adapters |
| M4 | Full UI extensions, settings, themes, catalog, renderers, trust, rollback, SDK docs and templates |

This distribution prevents the common failure mode where the plugin SDK is designed in isolation and does not fit the product.

### 22.8 v1 release criteria

- All selected stacks are detected by the v1 release.
- Gradle/KMP, APK, custom, Flutter, RN/Expo, MAUI, Capacitor/Cordova, and Unity have the documented support level.
- Add project, run, multi-device, logs, crash/ANR, screen, toolbox, APK drop, tests, performance, UI inspector, hot reload, and command palette work.
- Every feature is configurable or explicitly documented as fixed.
- Plugin SDK, catalog, and renderers work end to end.
- Portable build runs on a clean Windows machine.
- Performance budgets pass.
- Security review and privacy documentation are complete.
- Accessibility checks pass for main flows.
- Documentation and sample plugins are complete.
- No blocker or critical bugs remain.
- Release checklist in section 20 is complete.

### 22.9 Post-v1 roadmap

| Item | Status | Notes |
|---|---|---|
| MSIX packaging and App Installer updates | Post-v1 | After identity, signing, and update pipeline stabilize |
| Native C# screen streaming | Post-v1 | scrcpy protocol, Media Foundation, D3D11, multi-device wall |
| In-app JDWP/DAP debugging and IDE handoff | Post-v1 | IDE handoff first, then breakpoints |
| AAB/bundletool support | Post-v1 | APK and split APK first |
| R8 retrace and crash deobfuscation | Post-v1 | Mapping file management and retrace |
| Out-of-process plugin host | Post-v1 | For untrusted or non-UI plugins |
| Plugin signing policy | Post-v1 | Optional signatures first |
| Multi-device mirror wall | Post-v1 | Prefer native backend |
| Remote device farms | Post-v1 | Optional integration, not core |
| Cloud sync and accounts | Not planned | Local-first product |
| iOS support | Not planned | Android-focused |
| macOS/Linux host | Not planned | Windows-first |


## 23. Risks and mitigations

### 23.1 Risk register

| ID | Risk | Likelihood | Impact | Mitigation | Trigger |
|---|---|---|---|---|---|
| R1 | WinUI 3 or Windows App SDK instability blocks shell work | Medium | High | Pin versions, POC-01, isolate shell, keep a packaged fallback, track upstream issues | Crash or layout bug in a core flow |
| R2 | .NET 10 and Windows App SDK 2.5 self-contained publish fails on clean machines | Medium | High | POC-01 and POC-07, pin patches, document prerequisites, test in Sandbox | Missing DLL or runtime prompt |
| R3 | scrcpy embedding is fragile across DPI, focus, and resize | High | Medium | POC-04, external-window fallback, `IScreenSession` abstraction | Lost input, orphan window, crash |
| R4 | scrcpy version or license drift | Medium | Medium | Pin a validated version, bundle notices, check compatibility, document upgrade path | New scrcpy release or license change |
| R5 | Vendor adb quirks and authorization failures | High | Medium | Event-driven tracker, retries, doctor guidance, raw command display, device notes | Device not detected or install fails |
| R6 | Logcat volume overwhelms UI or memory | Medium | High | Bounded buffers, batching, replay tests, disk truth, dropped-line indicator | Frame drops or memory growth |
| R7 | Gradle variant and module parsing is wrong | High | Medium | Heuristics plus manual override, fixtures, refresh diff, show evidence | Wrong APK or variant selected |
| R8 | JDK, Gradle, and AGP mismatches confuse users | High | Medium | Per-project JDK, doctor checks, compatibility warnings, no hard block | Build fails before a task runs |
| R9 | Full-trust plugin compromises the machine or data | Medium | High | Hash pinning, trust prompt, capability disclosure, safe mode, no auto-install, source guidance | Suspicious plugin behavior |
| R10 | Plugin API churns and breaks ecosystem | Medium | High | Versioned contracts, built-ins dogfood the SDK, sample plugins, API review, migration guide | Breaking change after v1 |
| R11 | Catalog supply-chain compromise | Medium | High | HTTPS, mandatory SHA-256, optional signatures, no auto-install, audit, source removal | Hash mismatch or bad publisher report |
| R12 | Scope creep with no deadline delays v1 forever | High | High | Milestones with acceptance criteria, explicit post-v1 list, scope review, no silent additions | New feature request mid-milestone |
| R13 | Performance regressions accumulate | Medium | High | Budgets, CI benchmarks, soak, hot-path checklist, release gate | Budget exceeded by 10% |
| R14 | Windows 10, DPI, or multi-monitor issues | Medium | Medium | Compatibility matrix, minimum build decision, scaling tests, layout fallbacks | Broken layout or unreadable UI |
| R15 | Portable size or startup regresses | Medium | Medium | Size budget, R2R measurement, no trim, scrcpy size review, low-end startup test | Zip over 120 MB or start over 2 s |
| R16 | SmartScreen or antivirus flags the unsigned app | High | Low-Medium | Sign releases, publish checksums, consistent builds, document warnings | Download blocked or quarantined |
| R17 | Missing Flutter, Node, MAUI, or Unity toolchains | High | Medium | Doctor entries, fix guidance, no bundling, clear capability badges | User cannot run a detected stack |
| R18 | Unity has no generic one-click build | High | Medium | Detect, open in Unity, custom Editor template, APK fallback, Custom badge | User expects one-click build |
| R19 | Managed Expo workflow is limited | Medium | Medium | Limited badge, prebuild guidance, no false native debugging promise | User expects full native run |
| R20 | Multi-device work causes resource contention | Medium | Medium | Per-device locks, global concurrency limit, resource guard, cancellation | Slow installs or UI stalls |
| R21 | Config corruption or data loss | Low | High | Atomic writes, backups, quarantine, migrations, import diff | App starts with defaults unexpectedly |
| R22 | Accessibility gaps | Medium | Medium | Checklist, UI automation, Narrator pass, high contrast, 200% scaling | Keyboard or screen reader failure |
| R23 | Localization or RTL issues | Low | Medium | Resources from day one, pseudo-locale test, English first | Text clipping or layout mirroring |
| R24 | Third-party license violation | Low | High | Notices, no SDK/adb bundling, license audit, scrcpy attribution | Release audit finding |
| R25 | Device data exposure through logs or recordings | Medium | High | Local-only storage, warnings, retention, redaction, review before export | Sensitive data in a shared file |
| R26 | Flaky device tests slow delivery | High | Medium | Fake adb, self-hosted runner, quarantine, infra retries only, deterministic fixtures | Intermittent CI failures |
| R27 | Native streaming later is harder than expected | Medium | Medium | Keep `IScreenSession`, ship scrcpy, prototype only after v1 | Native backend misses budgets |
| R28 | Users expect hot plugin unload | High | Low | Document restart requirement, clear UI messaging, no false promise | Disable does not unload instantly |
| R29 | Capability badges confuse users | Medium | Medium | Clickable explanations, consistent labels, no dead buttons, onboarding | Support questions about missing actions |
| R30 | adb server conflicts with Android Studio | Medium | Low | Detect existing server, never kill it silently, restart action, doctor guidance | Device list disagrees with Studio |
| R31 | Long paths or Unicode paths break tools | Medium | Medium | Long-path support, canonicalization, tests, clear errors | Build or install fails on a valid path |
| R32 | Users run untrusted project scripts unknowingly | Medium | High | Explicit run, exact command, repo-shared trust prompt, no auto-run | Script executes on project add |

### 23.2 Risk review cadence

- Review the top five risks at the start and end of every milestone.
- Assign an owner and a measurable trigger to each active risk.
- Update likelihood and impact with evidence, not optimism.
- Escalate any risk that threatens a milestone acceptance criterion.
- Record accepted risks and fallbacks in an ADR.
- Revisit post-v1 risks when planning the next release.


## 24. Assumptions and open decisions

### 24.1 Assumptions

- Windows 11 is the primary target; Windows 10 support is best effort at a documented minimum build.
- x64 is the primary architecture for v1.
- Users have a working Android SDK and adb; DroidDeck detects and uses them but does not bundle them.
- Users have a JDK suitable for their projects; DroidDeck helps select one but does not bundle it.
- Bundling scrcpy with its license notices is acceptable.
- .NET 10 and Windows App SDK 2.5.x remain stable enough for the v1 timeline.
- Plugin authors accept in-process, full-trust execution and document their behavior.
- The plugin catalog is optional and user-configured; no official marketplace ships in v1.
- English is the first UI language; localization infrastructure is in place from day one.
- The product is local-first with no accounts, telemetry, or cloud dependency.
- A physical Android device and at least one emulator are available for testing.
- Users understand that building a project executes its build scripts.
- Gradle projects provide a wrapper; non-wrapper projects can use a configured Gradle.
- Android devices from API 26 through API 35 are the practical test range.
- Core functionality works offline; network is only needed for catalogs, updates, and docs.
- MSIX is post-v1; portable is the primary distribution for v1.
- Plugin authors target the host WinUI and Windows App SDK version.
- AAB/bundletool, in-app JDWP, native streaming, and R8 retrace are post-v1.
- scrcpy mirroring is an acceptable v1 screen experience.
- Users can configure tool paths when auto-detection fails.
- Team-shared project configuration is opt-in and excludes secrets and machine paths.

### 24.2 Open decisions

| ID | Decision | Options | Recommendation | Decision point |
|---|---|---|---|---|
| D1 | Minimum Windows version | Windows 11 only / Windows 10 22H2 / Windows 10 1809 | Windows 11 primary, Windows 10 22H2 best effort | Before M0 release |
| D2 | ARM64 build | x64 only / x64 and ARM64 | x64 only in v1; evaluate ARM64 post-v1 | M0 |
| D3 | Bundled scrcpy version | 5.0.1 / latest 5.x at release | Pin and validate 5.0.1, review before release | M0 |
| D4 | Default mirror mode | Embedded / external / auto | Auto with external fallback, decided by POC-04 | POC-04 |
| D5 | Default log UI buffer | 10k / 20k / 50k lines | 20k lines | M1 |
| D6 | Plugin API v1 surface | Minimal / full locked surface | Full surface described in section 14 | M0 |
| D7 | Catalog signature policy | Optional / required | Optional display, SHA-256 mandatory | M4 |
| D8 | Update check default | Off / weekly | Off | M4 |
| D9 | Portable self-update | None / in-app | None in v1; manual verified download | M4 |
| D10 | MSIX timing | M4 / post-v1 | Post-v1 | After v1 |
| D11 | Localization | English only / multiple languages | English first, resources ready | M4 |
| D12 | Default theme | Dark / system | Dark with system option | M4 |
| D13 | Crash reporting | None / opt-in upload | Manual diagnostics export only | M4 |
| D14 | Bundle adb | No / yes | No | Locked |
| D15 | Bundle JDK | No / yes | No | Locked |
| D16 | UI automation framework | FlaUI / WinAppDriver | FlaUI | M0 |
| D17 | Test framework | xUnit / NUnit / MSTest | xUnit | M0 |
| D18 | Plugin templates distribution | NuGet / repo only | NuGet plus repo samples | M4 |
| D19 | Release signing | Self-signed / CA-issued / unsigned | CA-issued if budget allows; otherwise clearly labeled unsigned | Before first public release |
| D20 | Native screen backend priority | Post-v1 / M4 | Post-v1 | After v1 |
| D21 | IDE handoff protocol | Configurable command / DAP | Configurable command first, DAP later | Post-v1 |
| D22 | AAB support | v1 / post-v1 | Post-v1 | Locked |
| D23 | Remote device farm integration | Post-v1 / not planned | Post-v1 optional | After v1 |
| D24 | Team config sharing default | Off / on | Off, opt-in per project | M4 |
| D25 | Feature flag policy | Hide and preserve / remove | Hide and preserve configuration | M4 |
| D26 | Support policy | Current only / current plus previous minor | Current stable plus previous minor for critical fixes | Release |
| D27 | Docs hosting | Repo only / GitHub Pages | Repo docs plus Pages site | M4 |
| D28 | Issue tracker | GitHub Issues / other | GitHub Issues | M0 |
| D29 | Crash dumps | None / opt-in local | Opt-in local only | M4 |
| D30 | Default catalog | None / official | None | Locked |

### 24.3 Explicitly out of scope for v1

- In-app breakpoints, JDWP debugging, and DAP integration.
- Native C# screen streaming and multi-device mirror walls.
- AAB/bundletool install and inspection.
- R8 retrace and crash deobfuscation.
- Cloud sync, accounts, telemetry, and analytics.
- Remote device farms and CI test-lab orchestration.
- iOS support.
- macOS or Linux host support.
- A full terminal emulator or TTY.
- Replacing Android Studio's editor, refactoring, or Gradle sync UI.
- An official plugin marketplace or hosting service.
- Automatic app updates in v1.
- Bundling adb, Android SDK, platform-tools, or JDK.
- AVD creation in v1; link to Android Studio instead.
- Out-of-process plugins or a plugin sandbox.
- Plugin hot unload without restart.
- Arbitrary remote code execution from catalog metadata.
- Automatic acceptance of device authorization or destructive actions.

### 24.4 Decision log

Locked product decisions are recorded in section 2. Architecture decisions are indexed in Appendix L. Any change to a locked decision requires an ADR with the reason, alternatives, impact, and migration plan.


## 25. Appendices

### A. Example global settings

```json
{
  "$schema": "https://droiddeck.dev/schemas/settings.schema.json",
  "schemaVersion": 1,
  "appearance": {
    "theme": "dark",
    "accent": "system",
    "density": "comfortable",
    "background": "mica",
    "motion": "full",
    "uiFont": "Segoe UI Variable",
    "logFont": "Cascadia Mono",
    "logFontSize": 14
  },
  "tools": {
    "androidSdk": "C:/Users/me/AppData/Local/Android/Sdk",
    "adb": "auto",
    "aapt2": "auto",
    "jdk": "auto",
    "gradle": "wrapper",
    "flutter": "auto",
    "node": "auto",
    "scrcpy": "bundled",
    "unity": "auto"
  },
  "run": {
    "buildOncePerGroup": true,
    "maxParallelInstalls": 3,
    "defaultTimeoutMinutes": 15,
    "confirmDestructiveActions": true
  },
  "logging": {
    "uiBufferLines": 20000,
    "batchIntervalMs": 50,
    "defaultLevel": "debug",
    "wrapLines": false,
    "retentionDays": 14,
    "retentionSizeMb": 2048
  },
  "screen": {
    "backend": "scrcpy",
    "embed": "auto",
    "maxSize": 1024,
    "maxFps": 60,
    "videoBitRate": "8M",
    "audio": false,
    "clipboardAutosync": true,
    "stayAwake": true,
    "thumbnailFps": 1
  },
  "features": {
    "hiddenStacks": [],
    "hiddenToolboxTabs": [],
    "experimental": {
      "nativeScreen": false,
      "autoRerun": false
    }
  },
  "plugins": {
    "enabled": true,
    "developerMode": false,
    "safeMode": false,
    "catalogAutoCheck": false
  },
  "catalog": {
    "sources": []
  },
  "renderers": {
    "logDetail": "builtin.log.json",
    "chart": "builtin.chart.line",
    "filePreview": "builtin.file.text",
    "testResult": "builtin.test.detail",
    "dataView": "builtin.data.tree"
  }
}
```

### B. Example project configuration

```json
{
  "$schema": "https://droiddeck.dev/schemas/project.schema.json",
  "schemaVersion": 1,
  "id": "prj_01",
  "name": "MyApp",
  "root": "C:/src/myapp",
  "kind": "gradle",
  "detection": {
    "detector": "gradle",
    "confidence": 0.98,
    "modules": ["app", "core"],
    "variants": ["devDebug", "prodDebug", "prodRelease"],
    "applicationId": "com.example.myapp",
    "launcherActivity": "com.example.myapp.MainActivity"
  },
  "toolchain": {
    "jdk": "auto",
    "gradleArgs": ["--configuration-cache"],
    "offline": false
  },
  "runConfigurations": ["appDebug"],
  "logFilters": [
    {
      "name": "App only",
      "package": "com.example.myapp",
      "level": "debug"
    }
  ],
  "screen": {
    "maxSize": 1024,
    "maxFps": 60
  },
  "requiredPlugins": [],
  "sharing": {
    "writeToRepo": false
  }
}
```

### C. Example run configuration

```json
{
  "id": "appDebug",
  "name": "appDebug",
  "kind": "gradle",
  "build": {
    "command": "gradlew.bat",
    "args": [":app:assembleDevDebug"],
    "cwd": "${projectDir}",
    "env": {
      "ANDROID_SERIAL": "${deviceSerial}"
    },
    "timeoutMinutes": 15
  },
  "artifact": {
    "apkGlob": "app/build/outputs/apk/dev/debug/*.apk"
  },
  "install": {
    "flags": ["-r", "-t"],
    "grantPermissions": false
  },
  "launch": {
    "package": "${applicationId}",
    "activity": "${launcherActivity}",
    "wait": true
  },
  "logs": {
    "clearBeforeLaunch": true,
    "buffers": ["main", "system", "crash"],
    "pidFilter": true
  },
  "screen": {
    "open": true,
    "mode": "auto"
  },
  "devices": {
    "target": "selected",
    "group": null
  },
  "steps": [
    {
      "name": "Grant camera",
      "type": "adb-shell",
      "command": "pm grant ${applicationId} android.permission.CAMERA",
      "continueOnError": true
    }
  ]
}
```

### D. Plugin manifest and settings schema

```json
{
  "id": "com.example.myplugin",
  "name": "My Plugin",
  "version": "1.0.0",
  "author": "Example",
  "description": "Adds a detector, device action, panel, and renderer.",
  "apiVersion": "1.0",
  "minHostVersion": "1.0.0",
  "entryAssembly": "MyPlugin.dll",
  "entryType": "MyPlugin.Plugin",
  "capabilities": [
    "project-detector",
    "run-step",
    "device-action",
    "ui-page",
    "ui-panel",
    "renderer",
    "theme"
  ],
  "dependencies": [
    {
      "id": "com.example.shared",
      "minVersion": "1.0.0"
    }
  ],
  "icon": "icon.png",
  "homepage": "https://example.com",
  "license": "MIT"
}
```

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "properties": {
    "endpoint": {
      "type": "string",
      "format": "uri",
      "title": "Endpoint"
    },
    "apiKey": {
      "type": "string",
      "format": "secret",
      "title": "API key"
    },
    "verbose": {
      "type": "boolean",
      "default": false,
      "title": "Verbose logging"
    }
  },
  "required": ["endpoint"],
  "additionalProperties": false
}
```

### E. Catalog schema

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://droiddeck.dev/schemas/catalog.schema.json",
  "title": "DroidDeck plugin catalog",
  "type": "object",
  "required": ["schemaVersion", "generatedAt", "plugins"],
  "properties": {
    "schemaVersion": {
      "type": "integer",
      "const": 1
    },
    "generatedAt": {
      "type": "string",
      "format": "date-time"
    },
    "plugins": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/plugin"
      }
    }
  },
  "additionalProperties": false,
  "$defs": {
    "plugin": {
      "type": "object",
      "required": ["id", "name", "publisher", "versions"],
      "properties": {
        "id": {
          "type": "string",
          "pattern": "^[a-z0-9]+([.][a-z0-9-]+)+$"
        },
        "name": {
          "type": "string",
          "minLength": 1,
          "maxLength": 80
        },
        "publisher": {
          "type": "string",
          "minLength": 1,
          "maxLength": 80
        },
        "summary": {
          "type": "string",
          "maxLength": 200
        },
        "description": {
          "type": "string",
          "maxLength": 10000
        },
        "iconUrl": {
          "type": "string",
          "format": "uri"
        },
        "homepage": {
          "type": "string",
          "format": "uri"
        },
        "license": {
          "type": "string",
          "maxLength": 80
        },
        "tags": {
          "type": "array",
          "items": {
            "type": "string",
            "maxLength": 32
          },
          "maxItems": 20
        },
        "versions": {
          "type": "array",
          "minItems": 1,
          "items": {
            "$ref": "#/$defs/version"
          }
        }
      },
      "additionalProperties": false
    },
    "version": {
      "type": "object",
      "required": ["version", "apiVersion", "minHostVersion", "downloadUrl", "sha256", "size"],
      "properties": {
        "version": {
          "type": "string",
          "pattern": "^[0-9]+[.][0-9]+[.][0-9]+([-+][0-9A-Za-z.-]+)?$"
        },
        "apiVersion": {
          "type": "string",
          "pattern": "^[0-9]+[.][0-9]+$"
        },
        "minHostVersion": {
          "type": "string"
        },
        "maxHostVersion": {
          "type": "string"
        },
        "downloadUrl": {
          "type": "string",
          "format": "uri"
        },
        "sha256": {
          "type": "string",
          "pattern": "^[a-f0-9]{64}$"
        },
        "signature": {
          "type": "string"
        },
        "size": {
          "type": "integer",
          "minimum": 1
        },
        "publishedAt": {
          "type": "string",
          "format": "date-time"
        },
        "changelog": {
          "type": "string",
          "maxLength": 20000
        },
        "capabilities": {
          "type": "array",
          "items": {
            "type": "string"
          }
        },
        "dependencies": {
          "type": "array",
          "items": {
            "type": "object",
            "required": ["id", "minVersion"],
            "properties": {
              "id": {
                "type": "string"
              },
              "minVersion": {
                "type": "string"
              }
            },
            "additionalProperties": false
          }
        }
      },
      "additionalProperties": false
    }
  }
}
```

### F. Placeholders and conditions

Placeholders:

| Placeholder | Meaning | Example |
|---|---|---|
| `${projectDir}` | Project root path | `C:/src/myapp` |
| `${projectName}` | Project display name | `MyApp` |
| `${projectKind}` | Detected stack | `gradle` |
| `${deviceSerial}` | adb serial | `9f2c1a7d` |
| `${deviceModel}` | Device model | `POCO F1` |
| `${deviceManufacturer}` | Device manufacturer | `Xiaomi` |
| `${deviceApi}` | Android API level | `35` |
| `${variant}` | Selected build variant | `devDebug` |
| `${apkPath}` | Located APK path | `C:/src/myapp/app/build/...` |
| `${applicationId}` | Android package name | `com.example.myapp` |
| `${launcherActivity}` | Launcher activity | `com.example.myapp.MainActivity` |
| `${sessionId}` | Current session ID | `ses_01` |
| `${runConfig}` | Run configuration name | `appDebug` |
| `${timestamp}` | ISO 8601 timestamp | `2026-10-27T12:00:00Z` |
| `${env:NAME}` | Environment variable | `${env:JAVA_HOME}` |
| `${secret:NAME}` | DPAPI secret | `${secret:CATALOG_TOKEN}` |

Rules:

- Placeholders expand once at step start; no recursive expansion.
- An unknown placeholder fails validation before the run starts.
- Values are passed as argument values, never concatenated into shell code.
- Secrets are masked in previews, logs, exports, and diagnostics.
- Paths are canonicalized; project-relative paths are resolved against the project root.
- The step editor previews the expanded command and working directory.
- A dry run validates paths, placeholders, conditions, and tool availability without executing.

Conditions:

| Field | Type | Example |
|---|---|---|
| `device.api` | number | `device.api >= 30` |
| `device.isEmulator` | boolean | `device.isEmulator == false` |
| `device.model` | string | `device.model contains "Pixel"` |
| `device.manufacturer` | string | `device.manufacturer == "Xiaomi"` |
| `device.abi` | string | `device.abi == "arm64-v8a"` |
| `project.kind` | string | `project.kind == "gradle"` |
| `variant` | string | `variant == "debug"` |
| `os.windowsBuild` | number | `os.windowsBuild >= 22621` |
| `env.NAME` | string | `env.CI == "true"` |

Operators:

| Operator | Meaning |
|---|---|
| `==`, `!=` | Equality and inequality |
| `>`, `>=`, `<`, `<=` | Numeric or version comparison |
| `contains`, `startsWith`, `endsWith` | String operations |
| `in`, `not in` | Membership in a list |
| `and`, `or`, `not` | Boolean composition |

Examples:

```text
device.api >= 30
device.isEmulator == false
project.kind == "gradle"
variant == "debug"
device.model contains "Pixel" and device.api >= 33
```

Rules:

- No arbitrary code in JSON conditions.
- String literals use double quotes; booleans are `true` and `false`; numbers are unquoted.
- Unknown fields or type mismatches fail validation with a clear message.
- Evaluation is short-circuiting and side-effect free.
- A skipped step shows the condition and the reason in the session timeline.
- Complex logic belongs in a script step, which is explicit and separately trusted.

### G. Default keyboard shortcuts

| Action | Default |
|---|---|
| Run | `F5` |
| Stop | `Shift+F5` |
| Rebuild and restart | `Ctrl+Shift+F5` |
| Command palette | `Ctrl+K` |
| Focus logs | `Ctrl+L` |
| Open mirror | `Ctrl+Shift+M` |
| Toggle navigation rail | `Ctrl+B` |
| Next device | `Ctrl+Tab` |
| Previous device | `Ctrl+Shift+Tab` |
| Screenshot | `Ctrl+Shift+S` |
| Toggle recording | `Ctrl+Shift+R` |
| Add project | `Ctrl+O` |
| Settings | `Ctrl+,` |
| Diagnostics | `Ctrl+Shift+D` |
| Help | `F1` |
| Close tab | `Ctrl+W` |
| Reopen closed tab | `Ctrl+Shift+T` |
| Search settings | `Ctrl+F` while in Settings |
| Reset zoom | `Ctrl+0` |

All shortcuts are remappable. Conflict detection prevents duplicate gestures. Keybindings can be exported and imported without secrets.

### H. Command-line reference

```text
DroidDeck.exe [options]

  --data-dir <path>          Use a custom data directory (portable mode)
  --profile <name>           Start with a named profile
  --config <path>            Load an additional configuration file
  --set <key>=<value>        Override a setting for this launch
  --safe-mode                Disable plugins and experimental features
  --no-plugins               Disable plugins without changing saved settings
  --plugin-dev <path>        Load an unpacked plugin folder in developer mode
  --log-level <level>        trace, debug, info, warn, or error
  --test-mode                Use a temporary data directory and fake services
  --diagnostics              Open the diagnostics page
  --version                  Print the version
  --help                     Print help
```

Rules:

- CLI overrides have the highest precedence.
- Secrets are never passed through command-line arguments by default.
- `--test-mode` never touches the real user data directory.
- Headless run and CI orchestration are post-v1; v1 is an interactive desktop app.
- Exit codes: `0` success, `1` general error, `2` invalid arguments, `3` startup failure.

### I. Reference commands

These are illustrative. DroidDeck builds argument lists rather than concatenating shell strings.

| Stack | Build | Install and launch | Logs and tests |
|---|---|---|---|
| Gradle | `gradlew.bat :app:assembleDevDebug` | `adb -s <serial> install -r -t <apk>` and `adb -s <serial> shell am start -W -n <package>/<activity>` | `adb -s <serial> logcat -v threadtime` and `gradlew.bat :app:testDevDebugUnitTest` |
| Flutter | `flutter build apk --debug` | `adb -s <serial> install -r <apk>` and `am start -W` | `flutter run -d <serial>` and `flutter test --reporter json` |
| React Native | `gradlew.bat :app:assembleDebug` | `npx react-native run-android --deviceId <serial>` | `npx react-native start --port 8081` and `adb -s <serial> reverse tcp:8081 tcp:8081` |
| .NET MAUI | `dotnet build -f net10.0-android -c Debug` | `dotnet build -t:Run -f net10.0-android` | `dotnet test` and `adb logcat` |
| Capacitor | `npx cap sync android` | `npx cap run android --target <serial>` | Gradle and adb logs |
| Cordova | `npx cordova build android` | `npx cordova run android --device <serial>` | Gradle and adb logs |
| APK | `aapt2 dump badging <apk>` | `adb install-multiple <apks>` and `cmd package resolve-activity --brief <package>` | `adb logcat` |
| Screen | `scrcpy --serial <serial> --max-size 1024 --max-fps 60 --video-bit-rate 8M` | n/a | scrcpy statistics |
| Device metadata | `adb -s <serial> shell getprop` | n/a | `adb -s <serial> shell wm size` and `dumpsys battery` |
| Wireless | n/a | `adb pair <host>:<port> <code>` and `adb connect <host>:<port>` | `adb mdns services` |
| Screenshot | n/a | n/a | `adb exec-out screencap -p` |
| UI tree | n/a | n/a | `adb shell uiautomator dump /sdcard/window.xml` and `adb pull` |

### J. Error and UX copy guidelines

Principles:

- Say what happened, what was not changed, and what to do next.
- Name the project, device, package, or step involved.
- Show the exact command when execution is relevant.
- Avoid blame, jargon, and vague failure verbs.
- Do not promise a capability the stack does not have.
- Destructive confirmations state the consequence in plain language.
- Errors are actionable: a button, a link, or a copyable command.

| Situation | Avoid | Use |
|---|---|---|
| Build failure | `Build failed.` | `Build failed in :app:assembleDevDebug. 3 Kotlin errors. Open the first error or copy the command.` |
| Device offline | `Device error.` | `POCO F1 is offline. Reconnect USB, authorize debugging on the device, or restart adb.` |
| Unauthorized device | `Unauthorized.` | `POCO F1 has not authorized this computer. Unlock the device and tap Allow on the USB debugging prompt.` |
| Missing tool | `scrcpy not found.` | `scrcpy was not found. Set its path in Settings > Tools, or use the bundled copy.` |
| Install signature mismatch | `INSTALL_FAILED_UPDATE_INCOMPATIBLE.` | `Install failed: the existing app was signed with a different key. Uninstall com.example.myapp or use a matching build.` |
| Plugin load failure | `Plugin error.` | `My Plugin failed to load: missing dependency com.example.shared 1.0.0 or later. Install the dependency or disable the plugin.` |
| Catalog hash mismatch | `Download failed.` | `The plugin download did not match its published SHA-256. Nothing was installed. Try again or contact the catalog publisher.` |
| No devices | `No devices.` | `No Android devices are connected. Connect a device by USB, start an emulator, or pair over Wi-Fi.` |
| Unsupported capability | `Not supported.` | `Unity projects need a custom Editor build method or an existing APK. Open the setup guide.` |
| Mirror fallback | `Embedding failed.` | `The mirror opened in its own window because embedding is unavailable on this device. You can change this in Settings > Screen.` |
| Destructive confirmation | `Are you sure?` | `Uninstall com.example.myapp from POCO F1? This removes the app and its data and cannot be undone.` |
| Corrupt config | `Invalid config.` | `settings.json could not be read. It was moved to settings.json.corrupt-20261027 and defaults were loaded. Open the folder.` |
| Port in use | `Port error.` | `Metro cannot use port 8081 because another process is listening. Stop that process or choose a different port.` |
| Wireless pairing failure | `Pairing failed.` | `Pairing failed. Check that the phone and PC are on the same network, the pairing port is correct, and the code has not expired.` |

### K. Requirements traceability

| Requirement | Plan section | Milestone | Verification |
|---|---|---|---|
| Add a folder and auto-detect Android run commands | 7 | M1 | Detector fixtures, E2E add project |
| Detect connected devices and show state | 8 | M0 | Fake adb, real device |
| Run and debug on a device | 9 | M1 | E2E Gradle, APK, custom |
| View and control the device screen | 10 | M2 | POC-04, device E2E |
| Support multiple devices | 9.11, 8.4 | M2 | POC-06, multi-device E2E |
| Support multiple projects | 7.16, 12.4 | M1, M4 | Monorepo fixtures, UI tests |
| Easy, user-friendly UX | 12 | All | Usability review, onboarding |
| High performance | 17 | All | Budgets, benchmarks, soak |
| Live visibility into actions and logs | 9, 11 | M1 | Session timeline, log tests |
| Full customization and configuration | 13 | M4 | Precedence matrix, UI tests |
| Gradle, Flutter, RN/Expo, MAUI, KMP, Unity, Capacitor/Cordova, APK, custom | 4, 7 | M1, M3 | Stack fixtures and E2E |
| Build variants and flavors | 7.14 | M1, M3 | Variant fixtures |
| Emulator and wireless ADB | 8.5, 8.6 | M2 | Emulator and pairing E2E |
| Device toolbox | 8.7 | M2 | Toolbox E2E |
| APK drop and install | 7.12, 12.10 | M2, M3 | APK fixtures |
| Performance and UI inspector | 8.7 | M3 | Reference device E2E |
| Test runner | 9.8 | M3 | JUnit XML fixtures |
| Hot reload | 9.7 | M3 | Flutter and RN E2E |
| Command palette and shortcuts | 12.9 | M3 | UI automation |
| Plugin SDK | 14 | M0-M4 | Sample plugins, host tests |
| Plugin catalog | 15 | M4 | POC-09, catalog tests |
| Custom renderers | 16 | M4 | POC-11, renderer tests |
| Portable distribution | 20 | M0, M4 | POC-07, clean VM |
| Security, privacy, and safety | 19 | All | Security tests, review |
| Testing and quality | 18 | All | CI, device matrix, release gate |

### L. Architecture decision records

| ADR | Decision | Status |
|---|---|---|
| ADR-001 | Use .NET 10, WinUI 3, and Windows App SDK 2.5.x | Accepted |
| ADR-002 | Ship portable first; MSIX post-v1 | Accepted |
| ADR-003 | Use scrcpy behind `IScreenSession`; native streaming post-v1 | Accepted |
| ADR-004 | In-process, full-trust plugins for v1 | Accepted |
| ADR-005 | Layered configuration with a source map | Accepted |
| ADR-006 | Build once, install many where the artifact is device-agnostic | Accepted |
| ADR-007 | Hybrid ADB client: TCP socket (`127.0.0.1:5037`, `host:track-devices-l`) + CLI process client | Superseded by ADR-021 |
| ADR-008 | Local-first with no telemetry or accounts | Accepted |
| ADR-009 | Catalog is a user-configured feed, no default marketplace | Accepted |
| ADR-010 | Renderer kinds: log detail, log line, chart, file preview, test result, data view | Accepted |
| ADR-011 | Single main window with tabs and pop-out mirrors | Accepted |
| ADR-012 | No in-app breakpoints in v1; IDE handoff post-v1 | Accepted |
| ADR-013 | Deep stack support in waves; detect all stacks from day one | Accepted |
| ADR-014 | Mandatory SHA-256 pinning and trust prompt for plugins | Accepted |
| ADR-015 | Bounded UI log buffer with disk as the source of truth | Accepted |
| ADR-016 | Feature visibility and flags without deleting configuration | Accepted |
| ADR-017 | Unity uses a custom Editor build method or APK fallback | Accepted |
| ADR-018 | Managed Expo workflow is labeled Limited | Accepted |
| ADR-019 | Test pyramid plus a real-device lab | Accepted |
| ADR-020 | Plugin catalog signatures are optional; hashes are mandatory | Accepted |
| ADR-021 | Hybrid ADB client: TCP socket (`127.0.0.1:5037`, `0014host:track-devices-l`) + CLI process execution | Accepted (v2.0) |
| ADR-022 | Persistent multi-buffer logcat (`-v threadtime -v year -v uid`) with host-side UID + `events` PID tracking | Accepted (v2.0) |
| ADR-023 | Host UI Component Kit + `PluginXamlMetadataProvider` + 3-strike circuit breaker for WinUI 3 ALC plugins | Accepted (v2.0) |
| ADR-024 | Split Team-Shared (`.droiddeck/project.json`) vs Local Project config scopes with Repository Script Trust | Accepted (v2.0) |

### M. Glossary

| Term | Meaning |
|---|---|
| **ADB** | Android Debug Bridge, the command-line interface to a device |
| **AAB** | Android App Bundle; not supported in v1 |
| **ANR** | Application Not Responding |
| **APK** | Android application package |
| **AVD** | Android Virtual Device, an emulator configuration |
| **Artifact** | The build output to install, usually an APK or split APK set |
| **Build variant** | A Gradle build type and flavor combination, such as `devDebug` |
| **Capability badge** | A UI label explaining the support level for a stack or project |
| **Catalog** | A user-configured JSON feed of plugins |
| **Device group** | A named set of devices selected manually or by rules |
| **DPAPI** | Windows Data Protection API used for local secrets |
| **Gradle wrapper** | The project-local `gradlew` or `gradlew.bat` launcher |
| **Hot reload** | Applying code changes without a full restart, supported for Flutter and React Native |
| **JDWP** | Java Debug Wire Protocol; in-app debugging is post-v1 |
| **KMP** | Kotlin Multiplatform |
| **Logcat** | Android's system and app log stream |
| **Mirror** | A live view of the device screen with optional input control |
| **MSIX** | Windows packaged app format; post-v1 |
| **Plugin** | A trusted assembly that extends DroidDeck through the SDK |
| **Profile** | A named set of global settings |
| **Preset** | A reusable bundle of settings for a specific area |
| **Renderer** | A plugin or built-in view for logs, charts, files, tests, or data |
| **Run configuration** | A named pipeline for building, installing, launching, and observing a project |
| **Run group** | One run across one or more devices |
| **Run session** | One run on one device |
| **scrcpy** | The screen mirroring and control tool used in v1 |
| **Stack adapter** | The code that turns a detected stack into run configurations and plans |
| **Toolchain** | The SDKs, JDKs, CLIs, and tools needed to build and run a project |
| **Trust pin** | The stored SHA-256 hash a user approved for a plugin |
| **UIAutomator** | Android's UI hierarchy dump tool |
| **Wireless ADB** | Connecting to a device over Wi-Fi instead of USB |
| **WinUI 3** | Microsoft's modern Windows desktop UI framework |
| **Windows App SDK** | The runtime and APIs that support WinUI 3 and other Windows features |

### N. Document change log

| Version | Date | Change |
|---|---|---|
| 1.0 | 2026-10-27 | Complete product and engineering plan: vision, decisions, workflows, scope, architecture, detection, devices, run pipeline, screen, logs, UX, configuration, plugin SDK, catalog, renderers, performance, testing, security, packaging, POCs, milestones, risks, assumptions, and appendices |
| 2.0 | 2026-10-27 | Architectural research and engineering refinement: added Hybrid ADB TCP socket tracker (`127.0.0.1:5037`, `0014host:track-devices-l`), fixed `scrcpy` CLI/env invocation (`ADB` env var, `--no-audio` default, exit code `2` handling, zero-flicker headless recording), added Mode A/B/C window hosting for WinUI 3 DirectComposition airspace, upgraded Logcat to persistent `-v threadtime -v year -v uid` with host-side UID + `events` dynamic PID tracking, added two-tier Gradle discovery via AGP `output-metadata.json`, added Xiaomi `INSTALL_FAILED_USER_RESTRICTED` recovery, split Team-Shared vs Local Project config scopes with Repository Script Trust, and introduced the Host UI Component Kit + `PluginXamlMetadataProvider` + 3-strike circuit breaker for WinUI 3 `AssemblyLoadContext` plugins |

**End of plan.**








