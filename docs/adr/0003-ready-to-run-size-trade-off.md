# ADR 0003: Keep ReadyToRun off in the portable release

**Status:** Accepted (2026-10-31)

## Context

The release publish can enable ReadyToRun (R2R) to reduce startup work at the cost
of a larger artifact. Plan 20.3 asks for a measurement every release: keep R2R
only when the startup win is meaningful, and keep the extracted portable size
under 250 MB and the zip under 120 MB.

The 1.0.0 release candidate was measured on the primary development machine
(Windows 11, 14-core CPU with 18 logical processors, 16 GB RAM, NVMe SSD) with
the release-check timing defined as process start to the first main window,
plugins disabled. Two batches of ten runs each, with the first two runs discarded
as warmups, were taken because a developer machine is noisy:

| Publish | Extracted size | Startup median (batch 1) | Startup median (batch 2) |
| --- | --- | --- | --- |
| `PublishReadyToRun=true` | 284.2 MiB | 347 ms (p95 526 ms) | 323 ms (p95 388 ms) |
| `PublishReadyToRun=false` | 232.8 MiB | 564 ms (p95 648 ms) | 513 ms (p95 696 ms) |

Both batches favor R2R by roughly 200 ms, so the startup win is real and
reproducible. It still adds 51 MiB and pushes the extracted folder 34 MiB over
the 250 MiB hard budget. Both publishes pass the startup budget in every batch,
so the size gate decides the trade-off.

## Decision

Publish the portable release with `PublishReadyToRun=false`. The size budget is a
release gate; the startup budget still passes comfortably without R2R. The
`0.0.1-alpha` release ships with R2R disabled on this basis.

## Consequences

- The extracted portable folder stays near 233 MiB and the zip near 91 MiB.
- Startup remains well inside the 2.0 s p50 and 3.0 s p95 budgets on the
  reference machine, at the cost of about 200 ms compared with R2R.
- Revisit R2R when the R2R publish fits under 250 MiB (currently 34 MiB over),
  the payload shrinks accordingly, or the size budget is formally revised.
- The timing above excludes plugin loading and measures first-window visibility,
  not full Home interactivity; both publishes are compared with the same method.

## Alternatives considered

- **Keep R2R and raise the size budget:** rejected because the 250 MiB budget is a
  distribution constraint, not a preference.
- **R2R for a subset of assemblies:** may recover part of the startup win without
  the full size cost; not attempted for 1.0.0 because the current startup already
  passes and subset tuning adds release complexity.
- **Composite R2R:** larger than per-assembly R2R and not justified for a single
  executable.

## Verification

`tools/DroidDeck.ReleaseCheck` enforces the size and startup budgets. The
measurements above are recorded under `docs/perf/results/`, and the release
checklist requires both gates before publishing.
