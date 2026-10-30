# Privacy

DroidDeck is local-first. It has no accounts, no telemetry, no analytics, no cloud
sync, and no crash upload. Settings, logs, sessions, screenshots, recordings,
diagnostics, and plugin data stay on the computer unless you explicitly share them or
use a network feature.

## What DroidDeck stores

| Data | Location | Notes |
| --- | --- | --- |
| Settings and backup | `<data>\settings.json`, `settings.json.bak` | Preferences, window placement, recent selections. |
| Settings profiles | `<data>\profiles` | Named settings layers. |
| Secrets | `<data>\secrets.json` | Protected with Windows DPAPI for the current user. Never exported. |
| Project records | `<data>\projects` | Local project overrides and run configurations. |
| Device records | `<data>\devices` | Notes, tags, and remembered connection details per serial. |
| Sessions | `<data>\sessions` | Run output, bounded by retention settings. |
| Logs | `<data>\logs` | Activity logs and the plugin audit log. |
| Diagnostics exports | `<data>\diagnostics` | Generated locally and redacted. |
| Screenshots and recordings | `<data>\screenshots`, `<data>\recordings` | Or the directories you configure. |
| Plugins and backups | `<data>\plugins`, `<data>\plugin-backups` | Third-party code and its private data. |
| Catalog sources and cache | `<data>\catalog` | Source list and cached catalogs for offline browsing. |
| Quarantine | `<data>\quarantine` | Downloads that failed verification or cleanup. |
| Toolchain snapshot | `<data>\toolchain.json` | Cached detected tool paths and versions. |

`<data>` is `%LocalAppData%\DroidDeck`, the `data` folder beside the executable in
portable mode, or the `--data-dir` path. Portable mode therefore puts all of the
above wherever you copied the app.

Team project files under `<repo>\.droiddeck` are part of the repository and are
shared with anyone who has the repository. Do not put secrets in them; DroidDeck
stores secrets separately in the DPAPI-protected file.

## When DroidDeck uses the network

| Feature | Trigger | Destination | Data sent | Default |
| --- | --- | --- | --- | --- |
| Catalog refresh | You click **Refresh** on Catalog | HTTPS URLs of the sources you added | `GET`, user agent `DroidDeck/<version>` | Sources are empty |
| Plugin install or update | You approve a trust decision | HTTPS `downloadUrl` from the catalog | `GET`, user agent `DroidDeck/<version>` | Only when you install |
| Manual update check | You click **Check for updates** | GitHub releases API | `GET`, user agent `DroidDeck/<version>`, standard GitHub Accept header | Off |
| React Native Metro logs | You run a React Native project with Metro | `127.0.0.1` on the local machine | Local HTTP request | Local only |
| External links | You click a documentation, license, or release link | The link's site | Opened in your default browser | User action |

There are no other outbound requests. DroidDeck does not send identifiers, usage
events, device data, project names, file contents, or logs to any server. The
catalog and installer download only what you ask for; the update check only reads
release metadata and never downloads or executes an update.

ADB traffic goes between the computer and the device over USB or the local network.
DroidDeck does not proxy it through a cloud service.

## Diagnostics and logs

Diagnostics reports are generated locally. Before text is written, the redactor
masks private key blocks, credentials embedded in URLs, bearer tokens, JWTs, and
secret-looking command arguments. Device serials and local paths are useful for
support, so they are retained; review a report before posting it publicly.

Activity and session logs can contain package names, file paths, shell commands, and
device output. Plugin logs are written by third-party code and are not guaranteed to
follow the same conventions. Share logs and reports only with people you trust.

## Plugins

Plugins run in-process with the same permissions as DroidDeck. This is why every
install requires an explicit trust decision, why trust is pinned to the plugin's
content hash, and why capabilities are shown before installation. Capabilities are a
disclosure mechanism, not a sandbox: a plugin can read files, run processes, or make
network requests with your user permissions. Only install plugins you trust.

DroidDeck does not send plugin usage anywhere. A plugin that makes network requests
does so on its own; review its source and documentation before trusting it.

## Your controls

- **Manual updates** are off by default and never run automatically.
- **Catalog sources** are optional; add only sources you trust and remove them at
  any time. Removing a source does not uninstall plugins you already installed.
- **Safe mode** (`--safe-mode`, `--no-plugins`, or `features.safeMode`) starts
  without third-party plugins.
- **Data deletion**: close DroidDeck and delete the data directory. This removes
  settings, logs, sessions, plugin data, and cached catalogs. Portable mode deletes
  with the app folder; `%LocalAppData%\DroidDeck` is not removed by unzipping a new
  build, so delete it explicitly.
- **Export and import** move settings between machines without secrets.

## Changes

This document describes the current release. Material changes to data handling or
network use are documented in the repository and linked from **About**.
