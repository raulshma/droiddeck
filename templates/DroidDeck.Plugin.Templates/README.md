# DroidDeck plugin templates

`dotnet new` templates for DroidDeck plugins. Each template creates a plugin
that builds against the published SDK packages and loads in DroidDeck without
edits.

## Install

The 0.1.0 packages are attached to the
[release](https://github.com/raulshma/droiddeck/releases/tag/v0.1.0), not
NuGet.org. Download the `.nupkg` files into one folder, then install the
template package from it:

```text
dotnet new install <sdk-folder>\DroidDeck.Plugin.Templates.0.1.0.nupkg
```

## Create a plugin

```text
dotnet new droiddeck-plugin-command -n HelloPlugin -o HelloPlugin --pluginId com.example.hello
dotnet new droiddeck-plugin-device-action -n DeviceTools -o DeviceTools --pluginId com.example.devicetools
dotnet new droiddeck-plugin-page -n MetricsPage -o MetricsPage --pluginId com.example.metrics
dotnet new droiddeck-plugin-renderer -n LogInspector -o LogInspector --pluginId com.example.loginspector
```

Use a unique, lowercase `--pluginId` (letters, digits, dots, and dashes). The
default `com.example.myplugin` is only a placeholder.

The generated project pins the SDK packages through the `PluginSdkVersion`
property (and `PluginWindowsAppSdkVersion` for WinUI projects); override either
property on the `dotnet build` command line when consuming a different release.

`droiddeck-plugin-page` and `droiddeck-plugin-renderer` target WinUI. The
command and device action templates target plain `net10.0`.

## Build and install

```text
dotnet build HelloPlugin
```

Copy the build output folder to `<data>\plugins\com.example.hello`, restart
DroidDeck, and approve the plugin in **Extensions**. The full authoring,
packaging, and publishing guides live in the plugin SDK documentation.
