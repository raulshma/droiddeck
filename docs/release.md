# Release checklist

Run this checklist for every release candidate and final release. Record evidence
under [`docs/perf/results`](perf/results/README.md) and link it from the release
notes. A release does not ship with an open blocker or critical bug, a failed gate,
or an unrecorded accepted risk.

## Pre-1.0 alpha policy

Alpha releases (`0.x`) use a reduced gate. The tag-driven
[release workflow](https://github.com/raulshma/droiddeck/blob/main/.github/workflows/release.yml)
builds from the tag, runs the full unit and template suites, enforces the portable
size budget, and drafts a GitHub release with checksums and an SBOM. Startup is
measured on the reference machine and its JSON is committed under
`docs/perf/results`.

Alphas may ship with these gates deferred, provided the release notes list them
as known limitations: the eight-hour soak, the clean-machine portable pass, and
the hands-on accessibility pass. A failed build, test, or size budget, or an open
security finding still blocks the release. The full checklist below remains the
1.0.0 bar.

## 1. Prepare

- [ ] Bump `DroidDeckVersion` in `Directory.Build.props`, the template
      `sdkVersion` defaults, and the template and sample `minHostVersion` values.
      The release workflow fails when the tag and `DroidDeckVersion` disagree.
- [ ] Confirm the tag will point at a commit on `main`.
- [ ] Draft `docs/releases/<tag>.md`; the workflow uses it as the release body.
- [ ] Update `Licenses/` and the third-party notices for dependency changes.
- [ ] Confirm CI is green on the exact commit being released.
- [ ] Review dependency audit, secret scan, and license audit results.
- [ ] Confirm every issue targeted by the milestone is closed or explicitly
      deferred with an owner.

## 2. Build the candidate

The release workflow runs these steps from the tag; reproduce them locally when
investigating a failure. Never rebuild a distributed artifact from a different
commit.

```powershell
$version = "0.0.2-alpha"
git switch --detach "v$version"
dotnet restore DroidDeck.slnx
dotnet build DroidDeck.slnx -c Release --no-restore
dotnet test DroidDeck.slnx -c Release --no-build --filter "Category!=UI&Category!=Template"
dotnet test tests/DroidDeck.Plugins.Tests/DroidDeck.Plugins.Tests.csproj -c Release --no-build --filter "Category=Template"
dotnet publish src/DroidDeck.App/DroidDeck.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=false -p:Deterministic=true `
  -o "artifacts/publish/DroidDeck-$version-win-x64"
```

ReadyToRun stays off so the portable folder fits the size budget; see
[ADR 0003](adr/0003-ready-to-run-size-trade-off.md). The project already sets
`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`, and
`PublishTrimmed=false`; do not override them globally, because those properties
propagate to class-library project references.

- [ ] Release build, the non-UI/non-template suite, and the template suite pass.
- [ ] Portable publish completes from the tag.

## 3. Size budget

The release workflow creates the zip with a single
`DroidDeck-<version>-win-x64` folder and enforces both budgets:

```powershell
$version = "0.0.2-alpha"
Compress-Archive -Path "artifacts/publish/DroidDeck-$version-win-x64" `
  -DestinationPath "artifacts/DroidDeck-$version-win-x64.zip" -CompressionLevel Optimal

dotnet run --project tools/DroidDeck.ReleaseCheck -c Release -- `
  size --directory "artifacts/publish/DroidDeck-$version-win-x64" `
  --zip "artifacts/DroidDeck-$version-win-x64.zip" `
  --label "$version <commit>" `
  --json "docs/perf/results/<date>-size-$version.json"
```

Budget: extracted folder at most 250 MiB, zip at most 120 MiB. The portable zip
must not contain symbols, test files, caches, or local settings; symbols are
embedded in the assemblies.

- [ ] Extracted size passes.
- [ ] Zip size passes.
- [ ] Any exception is recorded with an owner and rationale.

## 4. Startup budget

Run this on the reference machine with other applications closed. It measures
process start to the first main window with plugins disabled and an isolated data
directory, which is a repeatable proxy for cold start to interactive.

```powershell
$version = "0.0.2-alpha"
dotnet run --project tools/DroidDeck.ReleaseCheck -c Release -- `
  startup --exe "artifacts/publish/DroidDeck-$version-win-x64/DroidDeck.exe" `
  --runs 10 --warmup-runs 2 `
  --label "$version <commit>" `
  --json "docs/perf/results/<date>-startup-$version.json"
```

The release workflow does not gate on startup because hosted runners are too
noisy; this measurement is part of the release evidence and must be run on the
reference machine. The check reads the isolated data directory's log for the
`DroidDeck failed to start.` marker, so a run that only shows the startup
failure window fails the check instead of producing a sample.

Budget: median at most 2,000 ms and p95 at most 3,000 ms. If the machine is busy,
repeat the batch and keep the raw samples; a single outlier batch is not a
release decision.

- [ ] Median passes.
- [ ] p95 passes.
- [ ] First-frame, Home-interactive, and plugin-ready timings are spot-checked
      manually and are consistent with the performance plan (17.3-17.4).

## 5. Eight-hour soak

Deferred for `0.0.2-alpha` under the [pre-1.0 alpha policy](#pre-10-alpha-policy)
and listed as a known limitation in the release notes. The supervised
child-process refactor touches session and mirror paths, so this deferral is also
recorded as an accepted risk; the soak is required again from the next alpha that
changes those paths behaviourally, and for 1.0.0.

Prepare the reference workload: three devices or emulators, continuous logcat at
about 2,000 lines per second, two active mirrors, twenty runs, and repeated
settings changes. Start DroidDeck with a clean data directory, leave the workload
running, then sample:

```powershell
dotnet run --project tools/DroidDeck.ReleaseCheck -c Release -- `
  soak --process DroidDeck `
  --duration 08:00:00 --interval 00:00:10 --warmup 00:05:00 `
  --label "<version> <commit>" `
  --json docs/perf/results/<date>-soak.json
```

Pass criteria:

- [ ] Working-set growth after warmup is under 5 percent.
- [ ] Handle growth is at most 32 between the first and last post-warmup groups;
      the raw samples show no sustained climb.
- [ ] The JSON report has `releaseEligible: true` (a shorter smoke run does not).
- [ ] DroidDeck stays alive for the full eight hours.
- [ ] No orphan `adb` or `scrcpy` processes remain after closing sessions and
      devices.
- [ ] No leaked windows, duplicated tray/status entries, or growing log files.
- [ ] Sessions, mirrors, and settings changes still work at the end of the run.

A short soak validates the harness but is not a release substitute. Attach the
JSON report and describe the workload and hardware in the release notes.

## 6. Security review

Review the candidate against the [release security review](security-review.md)
and rerun the security-sensitive suites:

```powershell
dotnet test tests/DroidDeck.Plugins.Tests -c Release --no-build
dotnet test tests/DroidDeck.Core.Tests -c Release --no-build
```

Checklist:

- [ ] Process execution still uses `UseShellExecute = false` and argument lists;
      no string-built shell commands.
- [ ] Repository scripts require a SHA-256 trust decision and re-prompt on change.
- [ ] Plugin installs show trust before extraction, verify the catalog hash, reject
      unsafe archive entries, and keep the previous version for rollback.
- [ ] Catalog and package downloads are HTTPS-only, including every redirect hop,
      with size limits and schema validation.
- [ ] Secrets use DPAPI and never appear in logs, diagnostics, exports, or crash
      reports.
- [ ] Update checks are manual, HTTPS-only, and never download or install.
- [ ] Safe mode and `--no-plugins` still disable third-party code.
- [ ] No new high or medium finding is open; accepted risks are recorded.

## 7. Accessibility and compatibility

The hands-on Narrator, scaling, and pseudo-locale pass is deferred for
`0.0.2-alpha` under the [pre-1.0 alpha policy](#pre-10-alpha-policy) and listed
as a known limitation. The automated architecture and accessibility guard tests
still run in CI. Windows 10 22H2 remains best effort; Windows 11 24H2 and 25H2
are the supported targets.

Run the manual pass in [accessibility and languages](accessibility.md), then verify:

- [ ] The golden path is completed with the mouse disconnected.
- [ ] Narrator announces page titles, status changes, and errors.
- [ ] 200 percent scaling, high contrast, reduced motion, and the pseudo-locale
      keep the main flows usable and unclipped.
- [ ] Windows 11 24H2 and 25H2 pass; Windows 10 22H2 is checked best effort.
- [ ] 100, 125, 150, and 200 percent scaling, including a mixed-DPI dual-monitor
      setup, pass.
- [ ] Long paths, Unicode user names, paths with spaces, and a non-English locale
      pass.
- [ ] adb and Android Studio are tested both installed and absent; toolchains are
      tested present and absent.

## 8. Clean-machine portable check

Deferred for `0.0.2-alpha` under the [pre-1.0 alpha policy](#pre-10-alpha-policy)
and listed as a known limitation. The alpha publishes the same self-contained
portable folder that the 1.0.0 checklist verifies; run this section before the
first beta.

Use Windows Sandbox or a clean VM with no .NET SDK, no Windows App SDK runtime,
and no DroidDeck data. Copy the portable folder and complete every item:

- [ ] `DroidDeck.exe` launches without an installer and without an administrator
      prompt.
- [ ] First run creates `%LocalAppData%\DroidDeck` and shows onboarding without a
      crash.
- [ ] Theme, language, window placement, and settings persist across restarts.
- [ ] A user-provided adb detects a device or emulator and the device list updates
      on hotplug.
- [ ] A project can be added, detected, and run; the session shows logs and build
      output.
- [ ] APK install works by drag and drop and from the device tools.
- [ ] Mirror, screenshot, and screen recording work with the bundled or configured
      scrcpy.
- [ ] A catalog plugin shows the trust prompt with hash and capabilities; changing
      its files re-prompts; rollback restores the previous version.
- [ ] The diagnostics report is redacted and the About paths are correct.
- [ ] A `portable.marker` file keeps data beside the executable, and the folder
      can be moved to another machine.
- [ ] Removing the folder and data directory is a complete uninstall: no services,
      drivers, firewall rules, or user accounts were created.
- [ ] Any missing Visual C++ runtime requirement is documented or resolved without
      requiring administrator rights.

## 9. Artifacts and publish

The [release workflow](https://github.com/raulshma/droiddeck/blob/main/.github/workflows/release.yml)
builds every artifact from the tag and creates a draft release. Publishing is a
manual step after review.

Release assets:

| Asset | Contents |
| --- | --- |
| `DroidDeck-<version>-win-x64.zip` | Portable folder with a single top-level `DroidDeck-<version>-win-x64` directory. |
| `DroidDeck.Plugin.Abstractions.<version>.nupkg` | Plugin contracts package. |
| `DroidDeck.Plugin.UI.<version>.nupkg` | UI component kit package. |
| `DroidDeck.Plugin.Templates.<version>.nupkg` | `dotnet new` templates package. |
| `DroidDeck.PluginPack.<version>.nupkg` | Packaging tool package. |
| `DroidDeck-<version>.spdx.json` | SPDX 2.2 SBOM generated with `Microsoft.Sbom.DotNetTool` 4.1.13. |
| `SHA256SUMS.txt` | SHA-256 of the zip, SBOM, and four SDK packages. |

- [ ] Portable zip built from the publish output, without symbols, test files, or
      caches; debug symbols stay embedded in the assemblies.
- [ ] The four SDK packages are attached to the release because they are not
      published to nuget.org; the authoring guide installs them from a local feed.
- [ ] SBOM covers the portable publish and is attached unmodified.
- [ ] `SHA256SUMS.txt` lists every attached binary asset and verifies.
- [ ] License and third-party notices included in the portable folder; privacy
      and security docs linked from the release notes.
- [ ] Release notes (`docs/releases/<tag>.md`) list changes, known issues with
      workarounds, requirements, checksums, the security review outcome, and the
      deferred gates from the [pre-1.0 alpha policy](#pre-10-alpha-policy).
- [ ] The release is unsigned; the notes document the SmartScreen warning and how
      to verify the checksum before running.
- [ ] The downloaded artifact is launched and exercised once from the published
      location, not just from the build output.
- [ ] The tag is immutable, and the published artifact is the exact one built and
      verified above.
- [ ] The workflow refuses to modify an already published release; re-running a
      tag only refreshes the draft.
- [ ] The draft is reviewed and published manually; the workflow never publishes.

## 10. Sign-off

For a pre-1.0 alpha, deferred rows must cite this policy and appear as known
limitations in the release notes.

| Gate | Evidence | Owner | Result |
| --- | --- | --- | --- |
| CI green | Release workflow run link | Release engineer | Pending |
| Size | `docs/perf/results/<date>-size-<version>.json` | Release engineer | Pending |
| Startup | `docs/perf/results/<date>-startup-<version>.json` | Release engineer | Pending |
| Eight-hour soak | Deferred by the [pre-1.0 alpha policy](#pre-10-alpha-policy); listed in release notes as an accepted risk | Release engineer | Deferred for 0.0.2-alpha |
| Security review | [Security review](security-review.md) | Security reviewer | Reviewed, accepted risks recorded |
| Accessibility | Deferred by the [pre-1.0 alpha policy](#pre-10-alpha-policy); automated guards in CI | Release engineer | Deferred for 0.0.2-alpha |
| Clean machine | Deferred by the [pre-1.0 alpha policy](#pre-10-alpha-policy); listed in release notes | Release engineer | Deferred for 0.0.2-alpha |
| Release notes | `docs/releases/<tag>.md` | Release manager | Pending |

A gate is complete only when its evidence is attached. Known issues are published
with workarounds; silent omissions are not acceptable. If a release must ship with
a failed gate, record it as an accepted risk with the impact, mitigation, owner,
and the release that will resolve it.

## Rollback

Portable releases have no installer or auto-update. To roll back, keep the
previous release folder, restore the previous data directory if the settings schema
changed, and publish the earlier version with an advisory explaining why. Tags and
published artifacts are never mutated.

The in-app update check reads the latest stable GitHub release, so it ignores
prereleases like `0.0.1-alpha`. Publishing a later prerelease does not prompt
users on the alpha; testers upgrade by replacing the portable folder.

## 0.0.1-alpha release status (2026-10-31)

This records the evidence gathered for `0.0.1-alpha` from source commit
`7bd6731`. It is not a 1.0.0 sign-off; the deferred rows are published as known
limitations in [`docs/releases/v0.0.1-alpha.md`](releases/v0.0.1-alpha.md).
The size and startup reports were regenerated after the portable publish was
fixed to include the compiled XAML resources; the earlier startup report
measured the startup failure window and was discarded.

| Gate | Status | Evidence |
| --- | --- | --- |
| Size | Pass | 235.3 MiB extracted and 91.2 MiB zipped; [report](perf/results/2026-10-31-size-0.0.1-alpha.json). |
| Startup | Pass | Median 1207 ms, p95 1333 ms over eight measured runs after two warmups on the reference machine, with failure-window detection enabled; [report](perf/results/2026-10-31-startup-0.0.1-alpha.json). |
| Automated suites | Pass | Release build with 0 warnings and 1,324 tests passed locally, including the UI automation and plugin template suites. |
| Size, R2R reference | Fail | 284.2 MiB, 34 MiB over budget; R2R stays disabled by [ADR 0003](adr/0003-ready-to-run-size-trade-off.md). |
| Soak | Deferred | The eight-hour soak is deferred by the alpha policy; the two-minute harness smoke remains [here](perf/results/2026-10-31-soak-smoke.json) and is not release evidence. |
| Security review | Complete | No open remediable high/medium finding; R-01 is a high-impact accepted risk among six in the [security review](security-review.md). |
| Accessibility | Deferred | Hands-on Narrator, scaling, and pseudo-locale pass deferred by the alpha policy; automated architecture and accessibility guards pass in CI. |
| Clean machine | Deferred | Deferred by the alpha policy; testers exercise the portable folder, and the check returns before beta. |
| Release notes and artifacts | Ready for tag | [Release notes](releases/v0.0.1-alpha.md) are drafted; the workflow builds and drafts the release from the tag. |

## 0.0.2-alpha release status (2026-10-31)

This records the evidence gathered for `0.0.2-alpha` from source commit
`95c7c41`. It is not a 1.0.0 sign-off; the deferred rows are published as known
limitations in [`docs/releases/v0.0.2-alpha.md`](releases/v0.0.2-alpha.md).

| Gate | Status | Evidence |
| --- | --- | --- |
| Size | Pass | 235.5 MiB extracted and 91.4 MiB zipped; [report](perf/results/2026-10-31-size-0.0.2-alpha.json). |
| Startup | Pass | Median 1112 ms, p95 1320 ms over eight measured runs after two warmups on the reference machine, with failure-window detection enabled; [report](perf/results/2026-10-31-startup-0.0.2-alpha.json). The zip was extracted and launched once; after a first-run scan the extracted copy measured 1342-1354 ms across three runs after one warmup. |
| Automated suites | Pass | Release build with 0 warnings; 1,447 non-UI tests passed, plus the plugin template suite. |
| Security review | Complete | Revalidated for this candidate with no control or accepted-risk changes; plugin, catalog, process, run, and diagnostics suites are green. |
| Soak | Deferred, accepted risk | The supervised child-process refactor touches session and mirror paths, so the eight-hour soak was not run. The release notes record the risk and ask testers to watch for orphaned `adb` or `scrcpy` processes. |
| Accessibility | Deferred | Hands-on Narrator, scaling, and pseudo-locale pass deferred by the alpha policy; automated architecture and accessibility guards pass in CI. |
| Clean machine | Deferred | Deferred by the alpha policy; testers exercise the portable folder, and the check returns before beta. |
| Release notes and artifacts | Ready for tag | [Release notes](releases/v0.0.2-alpha.md) are drafted; the workflow builds and drafts the release from the tag. |
