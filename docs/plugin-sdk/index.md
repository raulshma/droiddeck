# Plugin SDK

DroidDeck plugins are .NET libraries that register commands, detectors, device
actions, pages, panels, renderers, and other extension points into the shell. A
plugin is a folder with a `plugin.json` manifest and its assemblies. The plugin host
loads each plugin into its own assembly load context, calls `InitializeAsync`, and
keeps every UI contribution inside a fault boundary.

Plugin API version: **1.0**. The ABI is stable within a major version.

## Requirements

- .NET 10 SDK to build.
- Reference `DroidDeck.Plugin.Abstractions` for every plugin.
- UI plugins also reference `DroidDeck.Plugin.UI` for the host component kit and
  target `net10.0-windows10.0.26100.0`.
- Never reference `DroidDeck.App`; the shell is not part of the plugin contract.

The contracts live in `src/DroidDeck.Plugin.Abstractions` (plain .NET) and
`src/DroidDeck.Plugin.UI` (WinUI component kit). `DroidDeck.Architecture.Tests`
enforces that plugin contracts stay free of shell references.

## Minimal plugin

```csharp
using DroidDeck.Plugin.Abstractions;

namespace HelloPlugin;

public sealed class HelloPlugin : IPlugin
{
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Extensions.AddCommand(new HelloCommand(context.Logger));
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class HelloCommand(Microsoft.Extensions.Logging.ILogger logger) : ICommand
{
    public string Id => "hello.command";
    public string Title => "Say hello";
    public string Description => "Writes a greeting to the plugin log.";
    public IReadOnlyList<string> Keywords => ["sample", "hello"];

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Hello from the plugin.");
        return Task.CompletedTask;
    }
}
```

Build the project, then place the output next to a `plugin.json` (see
[Packaging](#packaging)). Copy the folder into `<data>\plugins\<plugin-id>` and restart
DroidDeck. The plugin appears in **Extensions** as untrusted until you approve it.

## Manifest

`plugin.json` uses camelCase properties:

```json
{
  "id": "com.example.hello",
  "name": "Hello Plugin",
  "version": "1.0.0",
  "author": "Example",
  "description": "Registers one command.",
  "apiVersion": "1.0",
  "minHostVersion": "1.0.0",
  "maxHostVersion": null,
  "entryAssembly": "HelloPlugin.dll",
  "entryType": "HelloPlugin.HelloPlugin",
  "capabilities": ["command"],
  "dependencies": [],
  "icon": "icon.png",
  "homepage": "https://example.com/hello",
  "license": "MIT"
}
```

| Field | Required | Notes |
| --- | --- | --- |
| `id` | Yes | Lowercase letters, digits, dots, and dashes; unique across installed plugins. |
| `name` | Yes | Display name. |
| `version` | Yes | Semantic version. |
| `author` | No | Publisher shown in the plugin manager. |
| `description` | No | Shown in Extensions. |
| `apiVersion` | Yes | `major.minor`; the major must match the host. |
| `minHostVersion` / `maxHostVersion` | No | Restrict which DroidDeck versions may load the plugin. |
| `entryAssembly` | Yes | A `.dll` file name inside the plugin folder. |
| `entryType` | Yes | Fully qualified type implementing `IPlugin`. |
| `capabilities` | No | Capability names; unknown names produce a warning. |
| `dependencies` | No | Other plugin ids with a minimum version. |
| `icon` | No | Image file name inside the plugin folder. |
| `homepage`, `license` | No | Metadata for the plugin manager. |

The manifest, entry assembly, and optional icon must stay inside the plugin folder.
An entry assembly that resolves outside the folder is rejected.

## Capabilities

Capabilities are disclosed in the trust prompt. They are not a sandbox: a plugin can
still use files, processes, and the network with your user permissions.

| Capability name | Contribution |
| --- | --- |
| `command` | Command palette and menu command (`ICommand`). |
| `project-detector` | Project detection (`IProjectDetector`). |
| `run-step` | Extra run pipeline step (`IRunStep`). |
| `device-action` | Device action (`IDeviceAction`). |
| `device-tab` | Device detail tab (`IDeviceTabProvider`). |
| `toolbar-action` | Session and device toolbar action (`IDeviceToolbarAction`). |
| `ui-page` | Shell navigation page (`INavigationPage`). |
| `ui-panel` | Project or session panel (`IProjectTab`, `ISessionPanel`). |
| `home-widget` | Home widget (`IHomeWidget`). |
| `settings-section` | Settings section (`ISettingsSection`). |
| `status-bar-item` | Status bar item (`IStatusBarItem`). |
| `plugin-dialog` | Host-rendered dialog (`IPluginDialog`). |
| `renderer` | Custom renderer (`IRenderer` in `DroidDeck.Plugin.UI.Rendering`; register with `registry.AddRenderer<T>()`). |
| `log-parser` | Log line parser (`ILogParser`). |
| `highlight-rule-provider` | Log highlight rules (`IHighlightRuleProvider`). |
| `test-runner` | Test runner (`ITestRunner`). |
| `shell-commands` | Runs shell commands. |
| `plugin-data` | Reads and writes plugin-private data. |
| `stack-adapter`, `theme` | Reserved capability names for future extension points. |

Register contributions through the `IPluginContext`:

| Service | Purpose |
| --- | --- |
| `Extensions` | `AddCommand`, `AddProjectDetector`, `AddDeviceAction`, `AddDeviceTab`, `AddToolbarAction`, `AddRunStep`, `AddLogParser`, `AddHighlightRuleProvider`, `AddTestRunner`, `AddNavigationPage`, `AddProjectTab`, `AddHomeWidget`, `AddSessionPanel`, `AddSettingsSection`, `AddStatusBarItem`, `AddDialog`. |
| `Logger` | Plugin-scoped logger, visible in the plugin's **Logs**. |
| `Settings` | Plugin-private key/value store. |
| `DataDirectory` | Plugin-private folder. |
| `Dialogs` | Shows plugin-registered dialogs on the UI thread. |
| `Info` | Plugin id, name, version, API version, install path. |

## UI contributions

UI contributions return host UI elements, usually composed from the
`DroidDeck.Plugin.UI` component kit. The host owns the returned element and mounts it
inside a fault boundary, so a plugin page, tab, widget, panel, or section that throws
is replaced with a fallback card instead of breaking the shell. Create views lazily;
the host calls `CreateViewAsync` when the surface is first shown. Session panels also
receive `SessionPanelSnapshot` updates on the UI thread.

`DroidDeck.Plugin.UI` targets WinUI, so UI plugins must target
`net10.0-windows10.0.26100.0`. Non-UI plugins can stay on `net10.0`.

## Settings

A plugin can ship `settings.schema.json` beside its manifest. The host generates the
settings UI from the schema, validates values, and migrates stored values between
schema versions. Supported field shapes are text, number, whole number, boolean,
enum, path, secret, list, and one-level groups, with defaults, bounds, patterns, and
required fields. Secrets are stored by the host and never displayed.

Plugins can also read and write raw values through `IPluginContext.Settings`.

## Packaging

A plugin package is a zip archive with `plugin.json` at the root:

```text
hello-plugin-1.0.0.zip
  plugin.json
  HelloPlugin.dll
  <dependencies>.dll
  icon.png                (optional)
  settings.schema.json    (optional)
  LICENSE
```

Rules:

- Put the manifest at the archive root, not in a nested folder.
- Include every private dependency; the host resolves plugin dependencies from the
  plugin folder in an isolated load context and falls back to the host assemblies.
- Do not include DroidDeck's own assemblies.
- Keep the compressed package under 8 MiB and the extracted contents under 64 MiB.
- Do not use path traversal or absolute paths in archive entries.
- Include a license file.

## Installing

- **Manual:** copy the plugin folder into `<data>\plugins\<plugin-id>`, restart
  DroidDeck, then approve it in **Extensions**.
- **Catalog:** publish the package and a catalog entry (see
  [Catalog schema](../catalog.md)); installs are hash-verified and trust-gated.

Enable and disable take full effect after a restart. Uninstalling removes the plugin
folder; plugins installed from a catalog keep a rollback backup until the next
update.

## Versioning and compatibility

- The plugin API is `1.0`. A plugin with a different major API version is rejected;
  a different minor version loads with a warning.
- `minHostVersion` and `maxHostVersion` restrict the DroidDeck versions that may load
  the plugin.
- Bump the plugin's `version` for every package. Trust is pinned to the content
  hash, so changed contents always require a new approval.
- Catalog installs also check declared dependencies and their minimum versions.

## Samples

The repository contains working samples under `tests/DroidDeck.PluginSamples`:

| Sample | Demonstrates |
| --- | --- |
| `SampleCommandPlugin` | Command, device action, device tab, toolbar action, run step, log parser, highlight rules, and test runner. |
| `SampleUiPlugin` | Navigation page, project tab, home widget, session panel, settings section, status bar item, and dialogs. |
| `SampleRendererPlugin` | Custom renderer registration. |
| `SamplePerfPlugin1`–`5` | Minimal plugins used by the startup budget tests. |

Use them as the reference for project layout and manifest shape. Dotnet templates
for new plugins are planned with the authoring guide in this SDK.

## Security

Plugins run in-process and are not sandboxed. Only install code you trust, review
the trust prompt's capabilities and hash, and prefer plugins that ship source. See
[Security](../security.md) for the full model.
