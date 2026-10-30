# Security

DroidDeck runs with your Windows user permissions and drives adb, build tools, and
third-party plugins. This page describes the trust boundaries and the controls that
limit what can happen.

## Reporting a vulnerability

Do not open a public issue for a vulnerability. Use the repository's **Security** tab
and **Report a vulnerability** to open a private advisory:

<https://github.com/raulshma/droiddeck/security/advisories>

Include the affected version, the steps to reproduce, the impact, and any proof of
concept. Do not include real device data, signing keys, or secrets. You will get an
acknowledgment and a fix or mitigation plan; please allow time for a release before
public disclosure.

The current 1.x line receives security fixes. Older builds are not supported.

## Threat model

In scope:

- A malicious or compromised plugin or catalog.
- A malicious project repository that tries to execute commands.
- A hostile or compromised catalog source.
- Local attackers on the same machine or a shared Windows account.
- Network attackers against catalog, package, and update downloads.

Out of scope: a compromised Windows account, a compromised adb or Android device,
and physical access to an unlocked machine. DroidDeck is not a sandbox and does not
elevate privileges.

## Process execution

- Child processes are started with `UseShellExecute = false` and an argument list.
  Arguments are never concatenated into a shell command, so values from projects,
  devices, or settings cannot inject shell syntax.
- The exact command, working directory, and exit code are shown in sessions and
  logs. Output is bounded, with cancellation and timeouts.
- ADB and build tools are the ones detected by the toolchain doctor or chosen by an
  explicit override. Keep overrides pointed at trusted binaries.
- Project detection only reads files. It never invokes Gradle, Flutter, or Metro.
  Builds and runs happen only when you start them.
- Repository scripts are pinned by SHA-256 in `<data>\trust.json` by the repository
  script trust service. Because project run configurations can still start commands,
  treat a cloned repository as untrusted code: review its run configurations and
  scripts before running them.

## Plugins

Plugins load in-process and are not sandboxed. The controls are:

- **Explicit trust.** Every install shows the id, publisher, version, capabilities,
  hash, signature, and source. Nothing is extracted before approval.
- **Hash pinning.** Trust is tied to the plugin's content hash. Changed files require
  a new decision, and catalog reinstalls verify the published SHA-256.
- **Capabilities are disclosure.** They tell you what the plugin contributes and are
  shown before trust. They do not restrict file, process, or network access.
- **Isolation and health.** A plugin that throws is contained by the host and marked
  unhealthy; its pages and panels are withheld instead of taking down the shell.
- **Safe mode.** `--safe-mode`, `--no-plugins`, or `features.safeMode` starts without
  third-party plugins.
- **Audit and rollback.** Installs, updates, rollbacks, refusals, and failures are
  appended to `logs\plugin-audit.jsonl`. Updates keep the previous version for
  rollback.

Only install plugins whose publisher and code you trust, and review the warnings on
plugins that request shell-command or plugin-data capabilities.

## Catalogs and downloads

- Catalog sources accept absolute HTTPS URLs or local files and folders. HTTPS is
  required for remote sources.
- The catalog parser validates the whole schema before a source is browsable, with
  length limits on every text field.
- Downloads reject a final URL that is not HTTPS, including after redirects. Each
  hop is checked by the installer before the next request is sent.
- Packages are limited to 8 MiB compressed and 64 MiB extracted, downloaded into
  quarantine, and verified against the catalog's SHA-256 before extraction.
- Archive entries that escape the target directory are rejected, as are entries that
  do not resolve under the staging folder.
- Dependency minimum versions, API compatibility, and host version ranges are
  checked before install.
- A failed update leaves the installed version untouched.

## Secrets and data at rest

- Named secrets are protected with Windows DPAPI under the current user
  (`secrets.json`) and are never exported or written to logs.
- Diagnostics and exported sessions pass through a redactor that masks private keys,
  URL credentials, bearer tokens, JWTs, and secret-looking arguments.
- Other data — settings, logs, sessions, screenshots, and recordings — is stored in
  plain files under your user profile or the portable folder. Use BitLocker or an
  equivalent disk-encryption control on shared or portable machines.
- Logs and screenshots can contain sensitive app content. Delete the data directory
  when retiring a machine.

## Update checking

Manual update checking is off by default. When enabled and invoked, it:

- uses HTTPS only and rejects non-HTTPS redirects;
- sends only a `DroidDeck/<version>` user agent and standard GitHub headers;
- reads at most 512 KiB with a 15-second timeout;
- compares versions and shows a link to an HTTPS release page;
- never downloads, executes, or installs an update automatically.

## Network hardening

- Catalog fetches: 20-second timeout, 8 MiB limit, HTTPS final-URL check.
- Package downloads: 120-second timeout, 8 MiB download limit, per-hop HTTPS check.
- Update checks: 15-second timeout, 512 KiB limit, manual redirects.
- Metro log streaming connects only to `127.0.0.1` on the local machine.

## Appearance settings

Themes, density, motion, and accessibility-related appearance settings never
transmit data. See [Privacy](privacy.md) for the full data inventory and network-call
table.
