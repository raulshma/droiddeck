# Release security review

**Version reviewed:** 1.0.0 release candidate (`21d5ff4` and later hardening
changes)
**Review date:** 2026-10-31
**Scope:** process execution, plugins, catalog and package downloads, secrets and
redaction, update checking, and the portable distribution.
**Method:** source review of the trust boundaries plus the existing automated
suites. The review does not replace a third-party audit.

## Result

No remediable high or medium severity finding remains open in the release
candidate. The controls described in [Security](security.md) are implemented at
the boundaries reviewed below. Six residual risks are accepted with the
mitigations recorded in this document; R-01 has high impact but is an explicit
trust-boundary decision, and none is a blocker for the portable 1.0.0 release.

## Controls verified

| Area | Control | Result |
| --- | --- | --- |
| Process execution | `ProcessStartInfo.UseShellExecute = false` with `ArgumentList` in `ProcessRunner`/`ProcessLauncher`; no shell concatenation. | Pass |
| Process execution | Repository scripts are pinned by SHA-256; a changed script requires a new decision; paths outside the repository are rejected. | Pass |
| Process execution | Builds and runs are user-initiated; detection only reads files. | Pass |
| Plugins | Trust is shown before extraction; content hash pinning; changed contents re-prompt; a corrupt trust file fails closed. | Pass |
| Plugins | Packages are limited (8 MiB compressed, 64 MiB extracted); traversal, absolute paths, drive letters, and reserved device names are rejected. | Pass |
| Plugins | Install, update, rollback, uninstall, and trust decisions are audited; failed updates keep the previous version. | Pass |
| Plugins | A throwing plugin is contained by the fault boundary; safe mode and `--no-plugins` disable third-party code. | Pass |
| Catalog | Remote sources require HTTPS; every redirect hop is checked; the final URL is re-checked. | Pass |
| Catalog | The whole catalog schema is validated with length limits before it is browsable; downloads are size-bounded. | Pass |
| Catalog | Packages require a 64-hex SHA-256 that matches the downloaded bytes; manifest id and version must match the catalog. | Pass |
| Secrets | Named secrets are DPAPI-protected under the current user; plaintext exists only in the in-memory cache. | Pass |
| Redaction | Diagnostics, exported sessions, and logs pass through the redactor for keys, URL credentials, bearer tokens, JWTs, and secret-looking arguments. | Pass |
| Updates | Checks are manual and opt-in, HTTPS-only with redirects disabled, bounded to 512 KiB and 15 seconds, and never download or install. | Pass |
| Distribution | The portable build is unpackaged, self-contained, unprivileged, and does not write to its install folder unless portable mode is enabled. | Pass |

Verified by the suites in `tests/DroidDeck.Plugins.Tests/Catalog`,
`tests/DroidDeck.Core.Tests/Security`, `tests/DroidDeck.Core.Tests/Diagnostics`,
and the process/run tests. See the commands in the [release checklist](release.md).

## Findings fixed before this review closed

The following issues were found and fixed during release hardening; the fixes are
in the candidate:

1. **Installer redirect handling.** The installer's HTTP client could follow a
   redirect chain and hand the final response to the per-hop check. The client is
   now built with redirects disabled, and the installer follows hops itself so
   every address is checked before a request is sent.
2. **Package identity mismatch.** The catalog installer now rejects a package
   whose manifest id or version does not match the catalog entry, so a trusted
   hash cannot be confused with a different plugin identity.
3. **Archive path and device-name edges.** Extraction rejects reserved Windows
   device names (`CON`, `NUL`, `COM1`, and similar) in addition to traversal and
   absolute paths, turning a raw extraction failure into a clear rejection.
4. **Trust file corruption.** A corrupt plugin trust record now loads as an empty
   store, failing closed, instead of risking an implicit approval.

## Accepted risks

| ID | Risk | Impact | Mitigation and decision |
| --- | --- | --- | --- |
| R-01 | Plugins run in-process with full trust; a malicious approved plugin has the user's file, process, and network access. | High if a user approves a malicious plugin. | Explicit trust prompt with publisher, hash, and capabilities; hash pinning; safe mode; fault containment; audit log. Out-of-process isolation is deferred past 1.0.0 by design. |
| R-02 | Catalog signatures are displayed but not cryptographically verified. | A compromised publisher account could publish a malicious package with a valid-looking signature. | HTTPS, required SHA-256 pinning, dependency checks, and the trust prompt remain the actual controls. Signatures are disclosure only; documentation says so. Revisit when a signing key and verification contract exist. |
| R-03 | A trusted plugin or a user-run project command executes with the current user's privileges. | Same as any developer tool run by the user. | Repository script trust, per-run command preview, and the documented warning that cloned repositories are untrusted code. DroidDeck never elevates. |
| R-04 | The DPAPI plaintext cache keeps decrypted secrets in process memory while the app runs. | A process memory dump or debugger attached as the same user could read them. | The cache is required for request signing and is cleared on dispose; DPAPI protects the file at rest. Shared-account compromise is out of scope. |
| R-05 | The redactor ignores known secret values shorter than four characters and cannot recognize an arbitrary secret that never appears in the secret store. | A short or unrecognized secret could survive in logs or diagnostics. | Documented in [Security](security.md); users are told to keep secrets in the named secret store, and four characters is the minimum that avoids over-redacting ordinary text. |
| R-06 | A repository script can be replaced by a junction or hard link whose approved hash matches the script the user saw. | The approved contents are what runs, so this does not bypass the trust decision, but the displayed relative path may not reveal the physical target. | The decision is pinned to content, not path, and prompts show the repository-relative path and hash. Users are told to inspect cloned repositories before approving scripts. |

## Residual verification

- Static checks: warnings as errors, analyzers, architecture tests, and the
  localization/accessibility invariants.
- Security regression suites are part of the release test command in the
  [release checklist](release.md).
- A clean-machine portable smoke test and the eight-hour soak are required before
  publishing; their evidence is recorded under
  [`docs/perf/results`](perf/results/README.md).

## Out of scope

Compromise of the Windows account, a compromised adb server or Android device,
physical access to an unlocked machine, supply-chain attacks against NuGet
packages or build runners, and Windows kernel or driver vulnerabilities.
