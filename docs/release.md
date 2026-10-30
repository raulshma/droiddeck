# Release checklist

Run this checklist for every release candidate and final release. Record evidence
under [`docs/perf/results`](perf/results/README.md) and link it from the release
notes. A release does not ship with an open blocker or critical bug, a failed gate,
or an unrecorded accepted risk.

## 1. Prepare

- [ ] Bump `DroidDeckVersion` in `Directory.Build.props` and stamp the same version
      in the release notes.
- [ ] Update `Licenses/` and the third-party notices for dependency changes.
- [ ] Confirm CI is green on the exact commit being released.
- [ ] Review dependency audit, secret scan, and license audit results.
- [ ] Confirm every issue targeted by the milestone is closed or explicitly
      deferred with an owner.

## 2. Build the candidate

Build once from the tag; never rebuild a distributed artifact from a different
commit.

```powershell
git switch --detach <tag>
dotnet restore DroidDeck.slnx
dotnet build DroidDeck.slnx -c Release --no-restore
dotnet test DroidDeck.slnx -c Release --no-build --filter "Category!=UI&Category!=Template"
dotnet publish src/DroidDeck.App/DroidDeck.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=false -p:Deterministic=true `
  -o artifacts/publish/win-x64
```

ReadyToRun stays off so the portable folder fits the size budget; see
[ADR 0003](adr/0003-ready-to-run-size-trade-off.md). The project already sets
`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`, and
`PublishTrimmed=false`; do not override them globally, because those properties
propagate to class-library project references.

- [ ] Release build and the non-UI, non-template test suite pass.
- [ ] Portable publish completes from the tag.

## 3. Size budget

```powershell
Compress-Archive -Path artifacts/publish/win-x64/* `
  -DestinationPath artifacts/DroidDeck-win-x64.zip -CompressionLevel Optimal

dotnet run --project tools/DroidDeck.ReleaseCheck -c Release -- `
  size --directory artifacts/publish/win-x64 `
  --zip artifacts/DroidDeck-win-x64.zip `
  --label "<version> <commit>" `
  --json docs/perf/results/<date>-size.json
```

Budget: extracted folder at most 250 MiB, zip at most 120 MiB. The portable zip
must not contain PDBs, test files, caches, or local settings.

- [ ] Extracted size passes.
- [ ] Zip size passes.
- [ ] Any exception is recorded with an owner and rationale.

## 4. Startup budget

Run this on the reference machine with other applications closed. It measures
process start to the first main window with plugins disabled and an isolated data
directory, which is a repeatable proxy for cold start to interactive.

```powershell
dotnet run --project tools/DroidDeck.ReleaseCheck -c Release -- `
  startup --exe artifacts/publish/win-x64/DroidDeck.exe `
  --runs 10 --warmup-runs 2 `
  --label "<version> <commit>" `
  --json docs/perf/results/<date>-startup.json
```

Budget: median at most 2,000 ms and p95 at most 3,000 ms. If the machine is busy,
repeat the batch and keep the raw samples; a single outlier batch is not a
release decision.

- [ ] Median passes.
- [ ] p95 passes.
- [ ] First-frame, Home-interactive, and plugin-ready timings are spot-checked
      manually and are consistent with the performance plan (17.3-17.4).

## 5. Eight-hour soak

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

- [ ] Portable zip built from the publish output, without symbols, test files, or
      caches.
- [ ] Symbols published separately and contain SourceLink information.
- [ ] SHA-256 checksums generated for the zip and the executable.
- [ ] License, third-party notices, and privacy/security docs included.
- [ ] Release notes list changes, known issues with workarounds, requirements,
      checksums, and the security review outcome.
- [ ] The downloaded artifact is launched and exercised once from the published
      location, not just from the build output.
- [ ] The tag is immutable, and the published artifact is the exact one built and
      verified above.

## 10. Sign-off

| Gate | Evidence | Owner | Result |
| --- | --- | --- | --- |
| CI green | Pending run link | Release engineer | Pending |
| Size | `docs/perf/results/<date>-size*.json` | Release engineer | Pending |
| Startup | `docs/perf/results/<date>-startup.json` | Release engineer | Pending |
| Eight-hour soak | `docs/perf/results/<date>-soak.json` | Release engineer | Pending |
| Security review | [Security review](security-review.md) | Security reviewer | Reviewed, accepted risks recorded |
| Accessibility | [Accessibility pass](accessibility.md) | Release engineer | Pending |
| Clean machine | Section 8 checklist | Release engineer | Pending |
| Release notes | Draft link | Release manager | Pending |

A gate is complete only when its evidence is attached. Known issues are published
with workarounds; silent omissions are not acceptable. If a release must ship with
a failed gate, record it as an accepted risk with the impact, mitigation, owner,
and the release that will resolve it.

## Rollback

Portable releases have no installer or auto-update. To roll back, keep the
previous release folder, restore the previous data directory if the settings schema
changed, and publish the earlier version with an advisory explaining why. Tags and
published artifacts are never mutated.

## 1.0.0 candidate status (2026-10-31)

This records what was actually run while building the release gates. It is not a
release sign-off; the pending rows must be completed on the reference machine and
a clean VM before publishing.

| Gate | Status | Evidence |
| --- | --- | --- |
| Size, no R2R | Pass | 232.8 MiB extracted and 90.5 MiB zipped; [report](perf/results/2026-10-31-size-nor2r.json). |
| Size, R2R | Fail | 284.2 MiB, 34 MiB over budget; R2R disabled by [ADR 0003](adr/0003-ready-to-run-size-trade-off.md). |
| Startup | Pass | No-R2R batches: [batch 1](perf/results/2026-10-31-startup-nor2r.json) median 564 ms/p95 648 ms, [batch 2](perf/results/2026-10-31-startup-nor2r-batch2.json) median 513 ms/p95 696 ms. R2R was faster but over size; see [ADR 0003](adr/0003-ready-to-run-size-trade-off.md). |
| Automated budgets | Pass | Plugin load, UI/renderer creation, and logcat replay suites pass. |
| Soak | Smoke only | Two-minute harness smoke passed with -0.1 percent memory and +1 handle ([report](perf/results/2026-10-31-soak-smoke.json)); `releaseEligible` is false. The eight-hour release soak is pending the reference workload. |
| Security review | Complete | No open remediable high/medium finding; R-01 is a high-impact accepted risk among six in the [security review](security-review.md). |
| Accessibility | Structural pass | Architecture tests pass; the hands-on Narrator, scaling, and pseudo-locale pass in [accessibility](accessibility.md) is pending a UI session. |
| Clean machine | Pending | Requires Windows Sandbox or a clean VM with no .NET SDK; complete Section 8 there. |
| Release notes and artifacts | Pending | Complete Sections 2 and 9 at tag time. |

The eight-hour soak and the clean-machine check are environment-bound and were not
run in the development session that created these gates. They remain release
blockers until evidence is attached; the short soak is not an accepted substitute.
