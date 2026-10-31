# Catalog schema

DroidDeck browses plugin catalogs from user-added sources. A source is an HTTPS URL
or a local file or folder, so a team can publish a feed without hosting a server.
Adding a source only records its location; nothing is downloaded or installed until
you refresh and choose a plugin.

- **HTTPS source** — an absolute HTTPS URL that returns the catalog document.
- **Local source** — a path to a catalog JSON file or to a folder containing
  `catalog.json`.

Sources are stored in `<data>\catalog\sources.json`, fetched catalogs are cached in
`<data>\catalog\cache`, and the UI stays browsable offline.

## Schema version 1

```json
{
  "schemaVersion": 1,
  "generatedAt": "2026-10-30T12:00:00Z",
  "plugins": [
    {
      "id": "com.example.device-notes",
      "name": "Device Notes",
      "publisher": "Example",
      "summary": "Keep per-device notes next to the device list.",
      "description": "Adds a notes panel to every connected device.",
      "iconUrl": "https://example.com/icon.png",
      "homepage": "https://example.com/device-notes",
      "license": "GPL-3.0-only",
      "tags": ["notes", "productivity"],
      "versions": [
        {
          "version": "1.2.0",
          "apiVersion": "1.0",
          "minHostVersion": "0.0.2-alpha",
          "maxHostVersion": "1.9.9",
          "downloadUrl": "https://example.com/device-notes-1.2.0.ddplugin",
          "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
          "signature": null,
          "size": 48213,
          "publishedAt": "2026-10-01T09:00:00Z",
          "changelog": "Adds per-device tags.",
          "capabilities": ["device-tab", "plugin-data"],
          "dependencies": [
            { "id": "com.example.core", "minVersion": "1.0.0" }
          ]
        }
      ]
    }
  ]
}
```

### Document

| Field | Required | Notes |
| --- | --- | --- |
| `schemaVersion` | Yes | Must be `1`. Other versions are rejected with a clear message. |
| `generatedAt` | No | Informational timestamp. |
| `plugins` | Yes | Must contain at least one plugin. |

### Plugin

| Field | Required | Notes |
| --- | --- | --- |
| `id` | Yes | Lowercase letters, digits, dots, and dashes; unique in the catalog. |
| `name` | Yes | At most 128 characters. |
| `publisher` | No | At most 128 characters. |
| `summary` | No | At most 512 characters. |
| `description` | No | At most 16,384 characters. |
| `iconUrl` | No | Absolute HTTP or HTTPS URL. |
| `homepage` | No | Absolute HTTP or HTTPS URL. |
| `license` | No | SPDX identifier or short license name shown in the UI. |
| `tags` | No | At most 32 tags, each at most 64 characters. |
| `versions` | Yes | At least one version. |

### Version

| Field | Required | Notes |
| --- | --- | --- |
| `version` | Yes | Semantic version, unique within the plugin. |
| `apiVersion` | Yes | Plugin API version in `major.minor` form. `1.0` is current. |
| `minHostVersion` | No | Semantic version lower bound for the DroidDeck host. |
| `maxHostVersion` | No | Semantic version upper bound; must not be lower than the minimum. |
| `downloadUrl` | Yes | Absolute HTTPS URL of the plugin package. |
| `sha256` | Yes | Exactly 64 hexadecimal characters. The host verifies the download against it. |
| `signature` | No | Optional detached signature, shown in the trust decision. |
| `size` | No | Expected package size in bytes; must not be negative. |
| `publishedAt` | No | Informational timestamp. |
| `changelog` | No | At most 16,384 characters. |
| `capabilities` | No | Capability names from the plugin manifest. |
| `dependencies` | No | Other plugin ids with a minimum semantic version. |

Dependencies are not installed automatically. An install or update is refused when a
dependency is missing or older than the declared minimum.

## Validation

The host validates the whole document and reports every problem at once. A catalog
with any error is not browsable, and the source shows the validation message. When a
refresh fails, the previously cached catalog stays available and the source shows
the last error.

| Code | Meaning |
| --- | --- |
| `catalog.invalidJson` | The document is not valid JSON. |
| `catalog.empty` | The document was empty. |
| `catalog.schemaVersion.unsupported` | The schema version is not supported. |
| `catalog.plugins.required` | No plugins are listed. |
| `catalog.plugin.id.required` | A plugin has no id. |
| `catalog.plugin.id.invalid` | The id contains unsupported characters. |
| `catalog.plugin.duplicateId` | The same id appears twice. |
| `catalog.plugin.name.required` | A plugin has no display name. |
| `catalog.plugin.name.tooLong` | The name is too long. |
| `catalog.plugin.publisher.tooLong` | The publisher is too long. |
| `catalog.plugin.summary.tooLong` | The summary is too long. |
| `catalog.plugin.description.tooLong` | The description is too long. |
| `catalog.plugin.iconUrl.invalid` / `homepage.invalid` | The URL is not absolute HTTP(S). |
| `catalog.plugin.tags.tooMany` / `tag.invalid` | There are too many tags or one is empty or too long. |
| `catalog.plugin.versions.required` | A plugin lists no versions. |
| `catalog.version.invalid` | The version is not semantic. |
| `catalog.version.duplicateVersion` | The same version appears twice. |
| `catalog.version.apiVersion.invalid` | The API version cannot be parsed. |
| `catalog.version.minHostVersion.invalid` | The minimum host version is invalid. |
| `catalog.version.maxHostVersion.invalid` | The maximum host version is invalid. |
| `catalog.version.hostRange.invalid` | The host range ends before it starts. |
| `catalog.version.downloadUrl.required` | The download URL is missing. |
| `catalog.version.downloadUrl.invalid` | The download URL is not absolute. |
| `catalog.version.downloadUrl.insecure` | The download URL does not use HTTPS. |
| `catalog.version.sha256.invalid` | The hash is not 64 hexadecimal characters. |
| `catalog.version.size.invalid` | The size is negative. |
| `catalog.version.changelog.tooLong` | The changelog is too long. |
| `catalog.version.dependency.invalidId` | A dependency id is invalid. |
| `catalog.version.dependency.duplicate` | A dependency is listed twice. |
| `catalog.version.dependency.invalidVersion` | A dependency minimum version is invalid. |

## Compatibility

A version is **compatible** when the plugin API major version matches the host and
the host version is inside the declared host range. A **partial** match (same major,
different minor API) still loads but the plugin manager warns about it. A version
outside the host range or with a different API major is **incompatible** and cannot
be installed from the catalog.

Pre-release versions are hidden unless the source's *Include pre-release* option is
on. The version picker always prefers the highest compatible version.

## Install and update flow

1. **Refresh** fetches each enabled source with a versioned user agent, a 20-second
   timeout, and an 8 MiB response limit. GitHub-style HTTPS redirects are followed;
   non-HTTPS results are rejected.
2. **Plan** resolves the selected version, checks API and host compatibility, checks
   that declared dependencies are installed, and compares hashes.
3. **Download** goes to the quarantine folder with a 120-second timeout, a manual
   HTTPS check on every redirect hop, an 8 MiB compressed limit, and a 64 MiB
   extracted limit.
4. **Verify** compares the SHA-256 hash to the catalog before anything is unpacked.
   A mismatch aborts the install and the package stays in quarantine.
5. **Trust** shows the plugin id, publisher, version, capabilities, hash, signature,
   and source. Nothing is extracted until the decision is approved. Trust is pinned
   to the content hash; a new hash requires a new decision.
6. **Install** moves the unpacked folder into the plugins directory. Updates keep
   the previous version under `plugin-backups\<pluginId>` and roll back on failure.
7. **Audit** appends the action, versions, source, result, and hash to
   `logs\plugin-audit.jsonl`.

Packages are `.ddplugin` archives: zip files containing a `plugin.json` manifest
and the plugin files at the root. Archive entries that escape the target folder
are rejected. The [packaging guide](plugin-sdk/packaging.md) covers build output,
packing, hashes, and limits.

## Publishing checklist

1. Build and package the plugin with `droiddeck-plugin-pack` as described in the
   [packaging guide](plugin-sdk/packaging.md).
2. Use the SHA-256 the packaging tool prints and set it in the catalog.
3. Use an HTTPS URL for `downloadUrl`; local testing can use a local source.
4. Declare `apiVersion` and, when the plugin depends on a specific host, the host
   range.
5. Validate the feed by adding it as a local source before uploading it.
6. Keep the catalog small: it is downloaded as a single JSON document.
7. Publish a new catalog document for every package; never reuse a URL with
   different bytes under the same hash.
