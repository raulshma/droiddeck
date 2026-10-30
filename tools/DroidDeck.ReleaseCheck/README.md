# DroidDeck release check

`droiddeck-release-check` runs the measurable release gates described in
[docs/release.md](../../docs/release.md).

## Commands

```powershell
# Portable folder and zip size (250 MiB / 120 MiB)
dotnet run --project tools/DroidDeck.ReleaseCheck -c Release -- `
  size --directory artifacts/publish/win-x64 --zip artifacts/DroidDeck-win-x64.zip `
  --json docs/perf/results/2026-10-31-size.json

# Process start to main window (median 2,000 ms, p95 3,000 ms)
dotnet run --project tools/DroidDeck.ReleaseCheck -c Release -- `
  startup --exe artifacts/publish/win-x64/DroidDeck.exe --runs 10 --warmup-runs 2 `
  --json docs/perf/results/2026-10-31-startup.json

# Eight-hour soak of a running DroidDeck process
dotnet run --project tools/DroidDeck.ReleaseCheck -c Release -- `
  soak --process DroidDeck --duration 08:00:00 --interval 00:00:10 --warmup 00:05:00 `
  --json docs/perf/results/2026-10-31-soak.json
```

Exit codes: `0` pass, `1` budget failure, `2` usage or environment error.

- `size` measures every file in the publish directory. Pass `--zip` to also check
  the distributable zip; a release report must include it. Omitting `--zip` is
  only for a quick folder check.
- `startup` launches the executable with an isolated `--data-dir` and
  `--no-plugins`, measures until the main window handle appears, then closes it.
  Warmup runs are excluded from the samples.
- `soak` samples the working set and handle count of a running process. Growth is
  compared between the medians of the first and last tenth of samples after
  warmup (at least three samples each), so a single GC spike does not decide the
  gate. A run shorter than eight hours is reported with `releaseEligible: false`
  and only validates the harness.

Every command accepts `--label "<version> <commit>"` and `--json <path>`. Reports
include the machine and runtime environment; commit them under
`docs/perf/results` when they back a release decision. See
[docs/perf/results/README.md](../../docs/perf/results/README.md) for the schema and
known measurement noise.

## Tests

```powershell
dotnet test tests/DroidDeck.ReleaseCheck.Tests
```

The tests cover the budget evaluation and growth math; they do not launch the app.
