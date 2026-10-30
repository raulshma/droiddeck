# Performance results

Every run of `tools/DroidDeck.ReleaseCheck` can write a JSON report here. Keep the
file name dated and descriptive, for example
`2026-10-31-startup-nor2r.json`, and commit the reports that back a release
decision. Do not overwrite a previous release's evidence.

## Report shape

All reports include:

| Field | Meaning |
| --- | --- |
| `command` | `size`, `startup`, or `soak`. |
| `timestampUtc` | When the measurement started. |
| `label` | Free-form build or commit label supplied with `--label`. |
| `passed` | Whether every budget for that command passed. |
| `environment` | Machine name, OS version, processor count, .NET version, architecture. Committed reports use `reference-development-pc` and repository-relative paths. |
| `failures` | Human-readable budget failures; empty on pass. |

Command-specific fields:

- `size`: `extractedBytes`, `zipBytes`, and the budgets in bytes. A release
  report must include a non-null `zipBytes`.
- `startup`: per-run samples in milliseconds, timed-out runs, failed startup
  runs, median, p95, max, and the budgets. The measurement is process start to
  the first main window with plugins disabled and an isolated data directory.
  The startup failure window is also a main window, so a run that shows it is
  recorded under `failedStartupRuns` and fails the check instead of counting as
  a sample.
- `soak`: process id and name, duration, interval, warmup, memory and handle
  growth, the baseline and final medians, every raw sample, and `releaseEligible`.
  Only a run of at least eight hours is release evidence; a short smoke run has
  `releaseEligible: false` even when its measured budgets pass.

## Budgets

| Check | Budget |
| --- | --- |
| Extracted portable folder | 250 MiB |
| Portable zip | 120 MiB |
| Startup median | 2,000 ms |
| Startup p95 | 3,000 ms |
| Eight-hour memory growth | under 5 percent |
| Handle growth after warmup | at most 32 handles |

The startup budget in the performance plan is defined as cold start to
interactive. The tool measures first-window visibility, which is a cheaper and
more repeatable proxy; it is documented so results are comparable across
releases. Full interactivity and plugin loading are checked manually during the
release run.

A cached-resolution unit test in `DroidDeck.Core.Tests` uses a 5-second ceiling
so it survives parallel solution test runs; that guard is not a product budget.
The product budgets above are measured separately by the release-check tool.

## Known noise

A developer desktop is not a quiet benchmark machine. The 2026-10-31 results
show startup medians ranging from roughly 320 ms to 770 ms across individual
runs. Record several batches, keep the raw samples, and only treat a difference
as meaningful when it appears across batches. Size, memory growth, and handle
growth are deterministic enough to gate directly.

## Results

| Date | Command | Result | Notes |
| --- | --- | --- | --- |
| 2026-10-31 | size, 0.0.1-alpha | Pass (232.8 MiB extracted, 90.5 MiB zip) | [Report](2026-10-31-size-0.0.1-alpha.json); source commit `3a9f35c`. |
| 2026-10-31 | startup, 0.0.1-alpha | Pass (median 562 ms, p95 627 ms) | [Report](2026-10-31-startup-0.0.1-alpha.json); ten runs including two warmups at `3a9f35c`. |
| 2026-10-31 | size, no R2R | Pass (232.8 MiB extracted, 90.5 MiB zip) | 1.0.0-candidate reference measurement; see ADR 0003. |
| 2026-10-31 | size, R2R | Fail (284.2 MiB) | 34 MiB over the extracted budget; R2R disabled. |
| 2026-10-31 | startup, no R2R | Pass (batch 1 median 564 ms, p95 648 ms; batch 2 median 513 ms, p95 696 ms) | Two batches of ten runs. |
| 2026-10-31 | startup, R2R | Pass (batch 1 median 347 ms, p95 526 ms; batch 2 median 323 ms, p95 388 ms) | Consistently faster, but over the size budget. |
| 2026-10-31 | automated budgets | Pass | Plugin load, UI/renderer creation, and logcat replay suites. |
| 2026-10-31 | soak smoke | Pass (2 minutes, memory -0.1 percent, handles +1) | Harness validation only; `releaseEligible` is false. |
