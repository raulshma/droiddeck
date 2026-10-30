# Plugin SDK

DroidDeck plugins are .NET libraries that register commands, detectors, device
actions, pages, panels, renderers, and other extension points into the shell. A
plugin is a folder with a `plugin.json` manifest and its assemblies. The host
loads each plugin into its own assembly load context, calls `InitializeAsync`,
and keeps every UI contribution inside a fault boundary.

Plugin API version: **1.0**. The ABI is stable within a major version.

## Documentation map

| Guide | What it covers |
| --- | --- |
| [Authoring guide](authoring.md) | Create a plugin from a template, add every extension point, and test it. |
| [API reference](api-reference.md) | Every contract, data record, enum, and UI kit control. |
| [Manifest and schema reference](manifest.md) | `plugin.json`, capabilities, validation codes, and `settings.schema.json`. |
| [Packaging and publishing](packaging.md) | Build output, `.ddplugin` packaging, hashes, limits, catalog entries, and release checks. |
| [Catalog schema](../catalog.md) | The catalog document DroidDeck browses. |
| [Security](../security.md) | The plugin trust model and threat model. |
| [Settings reference](../settings.md) | How plugin settings appear to users. |

## Requirements

- .NET 10 SDK to build.
- Reference `DroidDeck.Plugin.Abstractions` for every plugin.
- UI plugins also reference `DroidDeck.Plugin.UI` and target
  `net10.0-windows10.0.26100.0`.
- Never reference `DroidDeck.App`; the shell is not part of the plugin contract.

The contracts live in `src/DroidDeck.Plugin.Abstractions` (plain .NET) and
`src/DroidDeck.Plugin.UI` (WinUI component kit). Both ship as NuGet packages.
The 0.0.1-alpha packages are attached to the
[release](https://github.com/raulshma/droiddeck/releases/tag/v0.0.1-alpha), not
NuGet.org; download them into one folder and add it as a package source.
`DroidDeck.Architecture.Tests` enforces that plugin contracts stay free of shell
references.

## Quick start

Download the four `.nupkg` assets into `<sdk-folder>`, install the template
package, and create a plugin. Add `<sdk-folder>` to `NuGet.config` (see
[adding the SDK](authoring.md#adding-the-sdk-to-an-existing-project)) before
building:

```text
dotnet new install <sdk-folder>\DroidDeck.Plugin.Templates.0.0.1-alpha.nupkg
dotnet new droiddeck-plugin-command -n HelloPlugin -o HelloPlugin --pluginId com.example.hello
dotnet build HelloPlugin -c Release
```

The template creates the project, the `IPlugin` entry point, one contribution,
and a matching `plugin.json`. A generated plugin builds and loads without
edits.

Package the build output:

```text
dotnet tool install --global DroidDeck.PluginPack --add-source <sdk-folder> --version 0.0.1-alpha
droiddeck-plugin-pack HelloPlugin/bin/Release/net10.0 --output artifacts
```

Copy the output folder into `<data>\plugins\com.example.hello`, restart
DroidDeck, and approve the plugin in **Extensions**. Or serve the printed
`.ddplugin` file from a catalog. See the [authoring guide](authoring.md) for the
full walkthrough and the [packaging guide](packaging.md) for catalog
publication.

## Templates

Four `dotnet new` templates ship with the SDK:

| Template | Creates | Target framework |
| --- | --- | --- |
| `droiddeck-plugin-command` | One command plus its manifest | `net10.0` |
| `droiddeck-plugin-device-action` | One device action plus its manifest | `net10.0` |
| `droiddeck-plugin-page` | One navigation page plus its manifest | `net10.0-windows10.0.26100.0` |
| `droiddeck-plugin-renderer` | One renderer plus its manifest | `net10.0-windows10.0.26100.0` |

Use a unique, lowercase `--pluginId` (letters, digits, dots, and dashes). The
generated projects pin the SDK version through `PluginSdkVersion` and, for UI
templates, `PluginWindowsAppSdkVersion`; override either on the command line.

## Extension points

| Area | Contracts | Capability |
| --- | --- | --- |
| Command palette | `ICommand` | `command` |
| Project detection | `IProjectDetector` | `project-detector` |
| Run pipeline | `IRunStep` | `run-step` |
| Device actions | `IDeviceAction`, `IDeviceToolbarAction` | `device-action`, `toolbar-action` |
| Device tabs | `IDeviceTabProvider` | `device-tab` |
| Navigation | `INavigationPage` | `ui-page` |
| Project and session panels | `IProjectTab`, `ISessionPanel` | `ui-panel` |
| Home page | `IHomeWidget` | `home-widget` |
| Settings UI | `ISettingsSection` | `settings-section` |
| Status bar | `IStatusBarItem` | `status-bar-item` |
| Dialogs | `IPluginDialog` | `plugin-dialog` |
| Renderers | `IRenderer` | `renderer` |
| Logs | `ILogParser`, `IHighlightRuleProvider` | `log-parser`, `highlight-rule-provider` |
| Tests | `ITestRunner` | `test-runner` |

The [API reference](api-reference.md) documents every member, and the
[authoring guide](authoring.md) shows each extension point in code.

Register contributions through the `IPluginContext`:

| Service | Purpose |
| --- | --- |
| `Extensions` | Adds commands, detectors, actions, steps, logs, tests, UI surfaces, and dialogs. |
| `Logger` | Plugin-scoped logger, visible in the plugin's **Logs**. |
| `Settings` | Plugin-private settings store, schema-validated when the plugin ships a schema. |
| `DataDirectory` | Plugin-private folder that survives updates. |
| `Dialogs` | Shows plugin-registered dialogs on the UI thread. |
| `Info` | Plugin id, name, version, API version, install path. |

## UI contributions

UI contributions return host UI elements, usually composed from the
`DroidDeck.Plugin.UI` component kit. The host owns the returned element and
mounts it inside a fault boundary, so a plugin page, tab, widget, panel, or
section that throws is replaced with a fallback card instead of breaking the
shell. Create views lazily; the host calls `CreateViewAsync` when the surface is
first shown. Session panels receive `SessionPanelSnapshot` updates on the UI
thread.

`DroidDeck.Plugin.UI` targets WinUI, so UI plugins must target
`net10.0-windows10.0.26100.0`. Non-UI plugins can stay on `net10.0`.

## Settings

A plugin can ship `settings.schema.json` beside its manifest. The host generates
the settings UI from the schema, validates values, and migrates stored values
between schema versions. Supported field shapes are text, number, whole number,
boolean, enum, path, secret, list, and one-level groups, with defaults, bounds,
patterns, and required fields. Secrets are stored encrypted and never
displayed.

Plugins can also read and write raw values through `IPluginContext.Settings`;
writes are validated through the schema when one exists.

## Samples

The repository contains working samples under `tests/DroidDeck.PluginSamples`:

| Sample | Demonstrates |
| --- | --- |
| `SampleCommandPlugin` | Command, device action, device tab, toolbar action, run step, log parser, highlight rules, and test runner. |
| `SampleDetectorPlugin` | Project detector with file evidence. |
| `SampleDeviceActionPlugin` | Confirmed device action that writes into the plugin data folder. |
| `SampleUiPlugin` | Navigation page, project tab, home widget, session panel, settings section, status bar item, and dialogs. |
| `SampleRendererPlugin` | Log detail and chart renderers. |
| `SamplePerfPlugin1`–`5` | Minimal plugins used by the startup budget tests. |

Use them as the reference for project layout and manifest shape. The
`dotnet new` templates cover the same project setup with a single extension.

## Versioning and compatibility

- The plugin API is `1.0`. A plugin with a different major API version is
  rejected; a different minor version loads with a warning.
- `minHostVersion` and `maxHostVersion` restrict the DroidDeck versions that may
  load the plugin.
- Bump the plugin's `version` for every package. Trust is pinned to the content
  hash, so changed contents always require a new approval.
- Catalog installs also check declared dependencies and their minimum versions.

The exact rules and validation codes are in the
[manifest reference](manifest.md#validation-codes).

## Security

Plugins run in-process and are not sandboxed. Only install code you trust,
review the trust prompt's capabilities and hash, and prefer plugins that ship
source. The host isolates faults, validates manifests before loading any
assembly, verifies package hashes, and records plugin actions in an audit log.
See [Security](../security.md) for the full model.
