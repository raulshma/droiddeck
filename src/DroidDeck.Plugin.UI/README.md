# DroidDeck.Plugin.UI

The WinUI component kit and renderer contracts for DroidDeck plugins. It
provides `PluginPageLayout`, `PluginCard`, `PluginDataGrid`, `PluginInfoBar`,
`PluginMetricChart`, `PluginFaultBoundary`, and the `IRenderer` contract with
its renderer catalog and runner.

Target `net10.0-windows10.0.26100.0` and set `UseWinUI` to `true`. The host
owns every returned element and shares this assembly with plugins, so plugins
must not ship their own copy.

Documentation: <https://github.com/raulshma/droiddeck/blob/main/docs/plugin-sdk/index.md>

Licensed under the GNU General Public License v3.0.
