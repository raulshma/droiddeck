# DroidDeck.Plugin.Abstractions

Contracts for building DroidDeck plugins: `IPlugin`, the extension interfaces
for commands, detectors, device actions, run steps, logs, tests, and UI
surfaces, plus plugin settings and renderer data types.

Target `net10.0` for non-UI plugins. Add `DroidDeck.Plugin.UI` when the plugin
contributes pages, panels, widgets, settings sections, dialogs, or renderers.

The plugin API is version 1.0. The ABI is stable within a major version.

Documentation: <https://github.com/raulshma/droiddeck/blob/main/docs/plugin-sdk/index.md>

Licensed under the MIT license.
