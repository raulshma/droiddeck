# Packaging and publishing

A plugin package is a zip archive with `plugin.json` at its root. DroidDeck
serves and installs packages as `.ddplugin` files; the extension is a
convention and the host reads the archive contents, so any zip with the same
layout works.

## Build the plugin

Build a Release configuration:

```text
dotnet build HelloPlugin -c Release
```

The output folder must contain the entry assembly and `plugin.json`. The
templates copy `plugin.json` to the output directory and set `entryAssembly` to
the assembly name. If you maintain the project by hand, add:

```xml
<ItemGroup>
  <None Update="plugin.json" CopyToOutputDirectory="PreserveNewest" />
  <None Update="settings.schema.json" CopyToOutputDirectory="PreserveNewest" Condition="Exists('settings.schema.json')" />
  <None Update="icon.png" CopyToOutputDirectory="PreserveNewest" Condition="Exists('icon.png')" />
  <None Update="LICENSE" CopyToOutputDirectory="PreserveNewest" Condition="Exists('LICENSE')" />
</ItemGroup>
```

## Package with the tool

Install the tool once. The 0.1.0 packages are attached to the
[release](https://github.com/raulshma/droiddeck/releases/tag/v0.1.0), not
NuGet.org; download the `.nupkg` files into `<sdk-folder>` first:

```text
dotnet tool install --global DroidDeck.PluginPack --add-source <sdk-folder> --version 0.1.0
```

Package a build output folder:

```text
droiddeck-plugin-pack HelloPlugin/bin/Release/net10.0 --output artifacts
```

Run it from the repository without installing:

```text
dotnet run --project tools/DroidDeck.PluginPack -- HelloPlugin/bin/Release/net10.0 --output artifacts
```

Use `--include-symbols` to keep portable PDBs. The default omits them, because
symbols make packages larger without helping the host run the plugin.

The tool prints the plugin id and version, the absolute package path, the
SHA-256 of the finished package, its size, and every archive entry:

```text
Packed com.example.hello 1.0.0
Package  C:\src\HelloPlugin\artifacts\com.example.hello-1.0.0.ddplugin
SHA-256  6a2f...9c41
Size     18422 bytes
Entries
  HelloPlugin.dll
  HelloPlugin.deps.json
  LICENSE
  plugin.json
```

The output file is named `<pluginId>-<version>.ddplugin`.

### What the tool includes

The tool packages every file under the build output except:

- `.pdb` files, unless `--include-symbols` is passed.
- Assemblies the host already provides. Shipping them would shadow the host's
  copy inside the plugin load context and waste package space.

Host-provided files are:

| File or prefix | Why it is excluded |
| --- | --- |
| `DroidDeck.Plugin.Abstractions.dll`, `DroidDeck.Plugin.UI.dll` | The host shares the SDK assemblies with every plugin. |
| `Microsoft.Extensions.Logging.Abstractions.dll`, `Microsoft.Extensions.DependencyInjection.Abstractions.dll` | The host provides these abstractions. |
| `WinRT.Runtime.dll`, `Microsoft.WindowsAppSDK.*`, `Microsoft.WindowsAppRuntime.*`, `Microsoft.Windows.SDK.NET.*`, `Microsoft.WinUI.*`, `Microsoft.Web.WebView2.*`, `Microsoft.InteractiveExperiences.*` | WinUI and Windows App SDK assemblies come from the host. |

Every other private dependency is included. Keep only the dependencies the
plugin actually uses; the extracted package limit is generous, but a lean
package installs faster and audits more cleanly.

The tool also checks the manifest before writing:

- The plugin id matches the lowercase id pattern.
- The version is semver.
- `entryAssembly` is a plain `.dll` file name and exists in the folder.
- `entryType` is present.
- No two files map to the same archive entry.
- The extracted size is at most 64 MiB.
- The compressed package is at most 8 MiB.

A failure prints `error: ...` and exits with code 1.

### Deterministic output

Entries are sorted by name, timestamps are fixed at 1990-01-01, and the tool
writes the same bytes for the same inputs on the same SDK. This makes hashes
stable across rebuilds of identical sources, which keeps trust pinning and
catalog diffs predictable. Rebuild the package after any source or manifest
change; never patch an archive in place.

### Limits

| Limit | Value | Enforced by |
| --- | --- | --- |
| Compressed package | 8 MiB | Packaging tool and catalog installer. |
| Extracted contents | 64 MiB | Packaging tool and catalog installer. |
| Archive entries | Must stay inside the plugin folder | Catalog installer rejects path traversal and absolute paths. |
| Manifest | `plugin.json` at the archive root | Installer and loader. |

The installer also limits catalog fetches to 8 MiB and plugin downloads to 120
seconds.

## Package layout

```text
com.example.hello-1.0.0.ddplugin
  plugin.json
  HelloPlugin.dll
  HelloPlugin.deps.json
  <private dependencies>.dll
  icon.png                 (optional)
  settings.schema.json     (optional)
  LICENSE
```

Include the plugin's license file. Put the manifest at the archive root, not in
a nested folder. Do not include DroidDeck's own assemblies or the assemblies
the host supplies.

## Install a package

### Manual install

1. Copy the package contents into `<data>\plugins\<plugin-id>`. The folder name
   is the plugin id.
2. Restart DroidDeck.
3. Open **Extensions** and approve the plugin. It stays untrusted until approved.

`<data>` is the DroidDeck data directory. With `portable.marker` beside
`DroidDeck.exe`, it is the app folder; otherwise it is
`%LOCALAPPDATA%\DroidDeck`.

### Catalog install

Publish the package over HTTPS and add a catalog entry. The host downloads,
verifies, asks for trust, then installs. See [Catalog schema](../catalog.md)
for the document format and the full install flow.

```json
{
  "version": "1.0.0",
  "apiVersion": "1.0",
  "minHostVersion": "0.1.0",
  "downloadUrl": "https://example.com/hello/com.example.hello-1.0.0.ddplugin",
  "sha256": "6a2f...9c41",
  "size": 18422,
  "changelog": "First release.",
  "capabilities": ["command"]
}
```

The `sha256` is the hash the packaging tool printed. The host compares the
download against it before anything is unpacked; a mismatch aborts the install
and leaves the package in quarantine. `size` is optional but helps the
installer reject an obviously wrong download early.

Updates keep the previous version under `plugin-backups\<pluginId>` and roll
back if the new install fails. Uninstalling removes the plugin folder.

## Versioning and trust

- Set a new plugin `version` for every package, update the catalog entry, and
  publish the new package.
- Trust is pinned to the content hash. A changed package, even with the same
  version, requires a new trust decision.
- Never reuse a download URL for different bytes. Give every build its own file
  name or path.
- Catalog installs check declared dependencies and their minimum versions
  before installing.
- `apiVersion` and the host version range are checked before the plugin loads;
  a plugin with an incompatible major API version cannot be installed or
  loaded.

## Distributing the SDK packages

The repository builds four NuGet packages that share one version:

| Package | Project | Contents |
| --- | --- | --- |
| `DroidDeck.Plugin.Abstractions` | `src/DroidDeck.Plugin.Abstractions` | Contracts, settings schema types, renderer data. |
| `DroidDeck.Plugin.UI` | `src/DroidDeck.Plugin.UI` | WinUI component kit and renderer contracts. |
| `DroidDeck.Plugin.Templates` | `templates/DroidDeck.Plugin.Templates` | The four `dotnet new` templates. |
| `DroidDeck.PluginPack` | `tools/DroidDeck.PluginPack` | The `droiddeck-plugin-pack` .NET tool. |

Pack them after a Release build:

```text
dotnet build DroidDeck.slnx -c Release
dotnet pack src/DroidDeck.Plugin.Abstractions/DroidDeck.Plugin.Abstractions.csproj -c Release --no-build -p:PackageOutputPath=artifacts/nuget
dotnet pack src/DroidDeck.Plugin.UI/DroidDeck.Plugin.UI.csproj -c Release --no-build -p:PackageOutputPath=artifacts/nuget
dotnet pack templates/DroidDeck.Plugin.Templates/DroidDeck.Plugin.Templates.csproj -c Release --no-build -p:PackageOutputPath=artifacts/nuget
dotnet pack tools/DroidDeck.PluginPack/DroidDeck.PluginPack.csproj -c Release --no-build -p:PackageOutputPath=artifacts/nuget
```

Pass `-p:PackageVersion=<version>` to stamp a release. Publish the SDK packages
before the template package: generated projects restore the SDK package version
passed through `--sdkVersion`. CI builds, tests, and packs the same four
packages, and `DroidDeck.Plugins.Tests` re-packs them into a temporary feed to
verify that every template builds and loads.

## Publishing checklist

1. Bump `version` in `plugin.json` and `settings.schema.json`'s `version` when
   setting shapes change.
2. Build Release, run tests, and package with `droiddeck-plugin-pack`.
3. Install the package on a clean DroidDeck data directory and exercise every
   contributed surface. Check the plugin log for load warnings.
4. Compute the hash from the tool output; do not hash a re-zipped copy.
5. Upload the package over HTTPS and update the catalog entry with its version,
   hash, size, capabilities, and compatibility range.
6. Validate the catalog by adding it as a local source, then as a remote
   source, before announcing it.
7. Keep the previous package available for one release so users can roll back
   if a new version fails.
