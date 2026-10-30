# Manifest and schema reference

Every plugin folder contains a `plugin.json` manifest at its root. The host
reads the manifest before it loads any assembly, so manifest errors are visible
even when the plugin code would not run.

## `plugin.json`

```json
{
  "id": "com.example.hello",
  "name": "Hello Plugin",
  "version": "1.0.0",
  "author": "Example",
  "description": "Registers one command.",
  "apiVersion": "1.0",
  "minHostVersion": "0.0.1-alpha",
  "maxHostVersion": null,
  "entryAssembly": "HelloPlugin.dll",
  "entryType": "HelloPlugin.HelloPlugin",
  "capabilities": ["command"],
  "dependencies": [],
  "icon": "icon.png",
  "homepage": "https://example.com/hello",
  "license": "GPL-3.0-only"
}
```

Properties are camelCase. Unknown properties are ignored; a manifest that
declares a misspelled required property still fails validation.

| Field | Required | Rules |
| --- | --- | --- |
| `id` | Yes | Lowercase letters, digits, dots, and dashes; matches `^[a-z0-9]+(?:[.-][a-z0-9]+)*$`. Unique across installed plugins. |
| `name` | Yes | Non-empty display name. |
| `version` | Yes | Semantic version, for example `1.2.0` or `1.2.0-beta.1`. |
| `author` | No | Publisher shown in the plugin manager. |
| `description` | No | Shown in **Extensions**. |
| `apiVersion` | Yes | `major.minor`; the major must match the host API major. `1.0` is current. |
| `minHostVersion` | No | Semantic version; the host must be this version or newer. |
| `maxHostVersion` | No | Semantic version; the host must be this version or older. |
| `entryAssembly` | Yes | A `.dll` file name at the plugin folder root. Paths are rejected. |
| `entryType` | Yes | Fully qualified type name implementing `IPlugin`. |
| `capabilities` | No | Capability names. Unknown names warn and are ignored. |
| `dependencies` | No | Other plugins the plugin needs. See [Dependencies](#dependencies). |
| `icon` | No | Image file name at the plugin folder root. |
| `homepage` | No | Absolute HTTP or HTTPS URL. |
| `license` | No | SPDX identifier or short license name. |

The entry assembly, icon, and any other referenced file must stay inside the
plugin folder. The host rejects an entry assembly that resolves outside it.

### Dependencies

```json
"dependencies": [
  { "id": "com.example.core", "minVersion": "1.0.0" }
]
```

Dependency ids use the same rules as plugin ids. A plugin cannot depend on
itself, list the same dependency twice, or declare an invalid minimum version.
Dependencies are not installed automatically: the host refuses to load a plugin
whose dependency is missing or older than the minimum.

### Capabilities

Capabilities are disclosed in the trust prompt and shown in the plugin manager.
They describe the plugin's intended contributions; they are not a sandbox. A
plugin can still use files, processes, and the network with the user's
permissions.

| Capability name | Contribution |
| --- | --- |
| `command` | Command palette and menu command (`ICommand`). |
| `project-detector` | Project detection (`IProjectDetector`). |
| `stack-adapter` | Reserved for a future stack adapter extension point. |
| `run-step` | Run pipeline step (`IRunStep`). |
| `device-action` | Device action (`IDeviceAction`). |
| `device-tab` | Device detail tab (`IDeviceTabProvider`). |
| `toolbar-action` | Session and device toolbar action (`IDeviceToolbarAction`). |
| `ui-page` | Shell navigation page (`INavigationPage`). |
| `ui-panel` | Project or session panel (`IProjectTab`, `ISessionPanel`). |
| `home-widget` | Home widget (`IHomeWidget`). |
| `project-tab` | Project page tab (`IProjectTab`). |
| `session-panel` | Session page panel (`ISessionPanel`). |
| `settings-section` | Settings page section (`ISettingsSection`). |
| `status-bar-item` | Status bar item (`IStatusBarItem`). |
| `plugin-dialog` | Host-rendered dialog (`IPluginDialog`). |
| `renderer` | Custom renderer (`IRenderer`). |
| `log-parser` | Log line parser (`ILogParser`). |
| `highlight-rule-provider` | Log highlight rules (`IHighlightRuleProvider`). |
| `test-runner` | Test runner (`ITestRunner`). |
| `shell-commands` | Runs shell commands. |
| `plugin-data` | Reads and writes plugin-private data. |
| `theme` | Reserved for a future theme extension point. |

Declare a capability when the plugin contributes the matching extension. The
trust prompt uses the list to tell the user what the plugin can do.

## Validation codes

Manifest problems are reported as a list, not one at a time. Errors block the
load; warnings do not.

| Code | Severity | Meaning |
| --- | --- | --- |
| `manifest.missing` | Error | There is no `plugin.json` in the folder. |
| `manifest.empty` | Error | The manifest is empty. |
| `manifest.invalidJson` | Error | The manifest is not valid JSON. |
| `manifest.unreadable` | Error | The manifest could not be read. |
| `manifest.id.required` | Error | `id` is missing. |
| `manifest.id.invalid` | Error | `id` contains unsupported characters or uppercase letters. |
| `manifest.name.required` | Error | `name` is missing. |
| `manifest.version.invalid` | Error | `version` is not semver. |
| `manifest.apiVersion.invalid` | Error | `apiVersion` is not a version. |
| `manifest.apiVersion.majorMismatch` | Error | The plugin targets a different major API version. |
| `manifest.apiVersion.minorMismatch` | Warning | The minor versions differ; the plugin still loads. |
| `manifest.minHostVersion.invalid` | Error | `minHostVersion` is not semver. |
| `manifest.minHostVersion.newerThanHost` | Error | The host is older than `minHostVersion`. |
| `manifest.maxHostVersion.invalid` | Error | `maxHostVersion` is not semver. |
| `manifest.maxHostVersion.olderThanHost` | Error | The host is newer than `maxHostVersion`. |
| `manifest.entryAssembly.required` | Error | `entryAssembly` is missing. |
| `manifest.entryAssembly.invalid` | Error | `entryAssembly` is not a plain `.dll` file name. |
| `manifest.entryAssembly.missing` | Error | The entry assembly file is not in the folder. |
| `manifest.entryType.required` | Error | `entryType` is missing. |
| `manifest.capabilities.unknown` | Warning | A capability name is not recognized. |
| `manifest.dependencies.invalidId` | Error | A dependency id is invalid. |
| `manifest.dependencies.self` | Error | The plugin depends on itself. |
| `manifest.dependencies.duplicate` | Error | A dependency is listed twice. |
| `manifest.dependencies.invalidVersion` | Error | A dependency minimum version is not semver. |
| `manifest.icon.invalid` | Warning | `icon` is not a plain file name. |
| `manifest.icon.missing` | Warning | The icon file is not in the folder. |
| `manifest.homepage.invalid` | Warning | `homepage` is not an absolute HTTP(S) URL. |

Load-time problems add these codes:

| Code | Severity | Meaning |
| --- | --- | --- |
| `plugin.directory.missing` | Error | The plugin folder does not exist. |
| `plugin.id.duplicate` | Error | Another installed plugin has the same id. |
| `plugin.entryType.missing` | Error | The entry type is not in the assembly. |
| `plugin.entryType.invalid` | Error | The entry type does not implement `IPlugin`. |
| `plugin.entryType.creationFailed` | Error | The entry type has no usable parameterless constructor. |
| `plugin.load.failed` | Error | The assembly could not be loaded. |
| `plugin.dependencies.unresolved` | Error | A declared dependency is not installed. |
| `plugin.dependencies.incompatible` | Error | A dependency is older than the declared minimum. |
| `plugin.settingsSchema.invalid` | Warning | The schema is invalid; the plugin uses the raw settings store. |
| `plugin.settingsSchema.noSecretStore` | Warning | The schema uses secrets but no secret store is available; the plugin uses the raw settings store. |

## Settings schema

A plugin can ship `settings.schema.json` beside its manifest. The host uses it
to generate the settings UI, validate values before they are persisted, and
migrate stored values between schema versions. The file is optional; without it
the plugin keeps the raw `IPluginSettings` store.

The schema is a deliberately small, safe JSON Schema subset. Anything outside
the subset is rejected with an error so the plugin fails visibly instead of
getting a settings UI it did not expect. A schema that fails to parse is
reported as a warning and the plugin falls back to the raw store.

### Document keys

| Key | Type | Notes |
| --- | --- | --- |
| `version` | Integer | Stored settings migrate to this version. Defaults to `1`; values from 1 to 1,000,000 are accepted. |
| `title` | String | Settings section title. |
| `description` | String | Settings section description. |
| `properties` | Object | Field definitions keyed by setting key. Keys must be unique, may not be empty, and may not be `settingsVersion`, `values`, or `secrets`. |
| `required` | Array of strings | Field keys that must have a value. Every name must be declared in `properties`. |
| `migrations` | Array | Migration steps. See [Migrations](#migrations). |

No other document keys are recognized.

### Field keys

| Key | Applies to | Notes |
| --- | --- | --- |
| `type` | All | `string` (default), `number`, `integer`, `boolean`, `array` (list), or `object` (group). |
| `format` | Strings | `secret` stores the value encrypted and never displays it; `path` marks a file or directory path. |
| `enum` | Strings | A non-empty array of unique strings; turns the field into a choice. |
| `default` | All | Value applied when the setting is absent. |
| `title` | All | User-facing label; falls back to the key. |
| `description` | All | User-facing explanation. |
| `minimum`, `maximum` | Numbers | Inclusive numeric bounds. |
| `minLength`, `maxLength` | Strings | Inclusive length bounds. |
| `pattern` | Strings | Regular expression the value must match. |
| `items` | Lists | Item type: `string`, `number`, `integer`, or `boolean` (default `string`). |
| `properties` | Groups | One level of nested field definitions. |
| `required` | Groups | Required nested field names. |

Groups are one level deep. Lists hold scalar values. Secrets cannot be
defaulted to a plain-text value.

Example:

```json
{
  "version": 2,
  "title": "Deploy settings",
  "description": "Applied to every deploy from this plugin.",
  "properties": {
    "environment": {
      "type": "string",
      "title": "Environment",
      "enum": ["dev", "staging", "production"],
      "default": "dev"
    },
    "retries": {
      "type": "integer",
      "title": "Retries",
      "default": 2,
      "minimum": 0,
      "maximum": 10
    },
    "apiToken": {
      "type": "string",
      "format": "secret",
      "title": "API token",
      "minLength": 16
    },
    "notify": {
      "type": "object",
      "title": "Notifications",
      "properties": {
        "enabled": { "type": "boolean", "default": true },
        "channel": { "type": "string", "default": "#builds" }
      }
    }
  },
  "required": ["environment"],
  "migrations": [
    {
      "fromVersion": 1,
      "renames": { "env": "environment" },
      "defaults": { "retries": 2 },
      "removed": ["legacyFlag"]
    }
  ]
}
```

### Stored values

The host stores plugin settings as `settings.json` in the plugin folder:

```json
{
  "settingsVersion": 2,
  "values": {
    "environment": "staging",
    "retries": 3,
    "notify": { "enabled": true, "channel": "#builds" }
  },
  "secrets": ["apiToken"]
}
```

`values` contains every value, including unknown keys, which are preserved
across writes. Secret values never appear in the file; only their names are
listed under `secrets`, and the values live in the host's DPAPI-backed secret
store under `<pluginId>.<fieldKey>`.

Writes through `IPluginSettings.SetAsync` are validated against the schema. An
invalid write throws `PluginSettingsValidationException`, which carries one
`PluginSettingsFieldError(Field, Code, Message)` per invalid field. The settings
UI shows the same errors without throwing.

### Migrations

Stored settings carry the `settingsVersion` that was current when they were
written. When the schema declares a higher version, migration steps with a
`fromVersion` at or above the stored version run in order:

| Key | Meaning |
| --- | --- |
| `fromVersion` | The stored version this step upgrades. Must be unique and from 1 to 1,000,000. |
| `renames` | Object mapping old keys to new keys. |
| `defaults` | Object of values written when the target key is absent. |
| `removed` | Array of keys to delete. |

Values whose keys are not mentioned in a step are left untouched, including
unknown keys. After all applicable steps run, the document is written with the
schema's current `version`.

### Validation codes

| Code | Meaning |
| --- | --- |
| `settingsSchema.unreadable` | The file could not be read. |
| `settingsSchema.invalidJson` | The file is not valid JSON. |
| `settingsSchema.unknownKey` | The schema or a field uses a key outside the supported subset. |
| `settingsSchema.version.invalid` | `version` is not an integer from 1 to 1,000,000. |
| `settingsSchema.properties.invalid` | `properties` is not an object. |
| `settingsSchema.field.invalid` | A field definition is not an object. |
| `settingsSchema.key.invalid` | A field key is empty or otherwise invalid. |
| `settingsSchema.key.duplicate` | Two fields use the same key. |
| `settingsSchema.key.reserved` | A field key collides with a reserved document key. |
| `settingsSchema.enum.invalid` | `enum` is not a non-empty array of unique strings on a string field. |
| `settingsSchema.default.invalid` | A default does not match the field type. |
| `settingsSchema.items.invalid` | List `items` is not a supported scalar type. |
| `settingsSchema.pattern.invalid` | `pattern` is not a valid regular expression. |
| `settingsSchema.required.invalid` | `required` is not an array of declared field names. |
| `settingsSchema.migrations.invalid` | A migration step is malformed. |

Value validation codes include `settings.value.required`, `.type`, `.enum`,
`.integer`, `.minimum`, `.maximum`, `.minLength`, `.maxLength`, and `.pattern`.

The user-facing behavior of schema-driven plugin settings is described in the
[settings reference](../settings.md).

## Versioning rules

- `apiVersion` is a `major.minor` plugin API version. A different major is
  rejected; a different minor loads with a warning. The API is currently `1.0`.
- `minHostVersion` and `maxHostVersion` restrict which DroidDeck versions may
  load the plugin.
- Bump the plugin `version` for every package. Trust is pinned to the package
  content hash, so changed contents always require a new approval.
- Dependencies use semantic versions; the host loads plugins in dependency
  order and shuts them down in reverse order.
