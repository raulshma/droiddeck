# POC-02: adb device tracker

**Status:** Partial — event-driven tracking and recovery are implemented and covered
against FakeAdb; physical-device latency and idle-CPU measurements are still open.

## Method exercised

- `host:track-devices-l` over a persistent loopback socket with 4-hex framing.
- Automatic `adb start-server` when the socket is refused.
- CLI polling fallback while the socket is unavailable, with automatic recovery.
- Sectioned metadata batch (`getprop`, `wm`, `dumpsys battery`, `ip`, `df`) parsed
  into one device record.
- State mapping for device, offline, unauthorized, authorizing, recovery, sideload,
  bootloader, connecting, and disconnected.
- Manual restart action through `adb kill-server` + `adb start-server`, then reconnect.

## Evidence

- Socket client, tracker, parser, metadata, and service suites pass.
- FakeAdb scenarios cover ready, offline, unauthorized, hotplug, metadata failure,
  and socket failure.
- The UI automation test hotplugs a fake device over the socket and observes it in
  the device list with state `Ready`.
- Unauthorized devices expose actionable authorization guidance rather than a dead
  row.
- The tracker reuses an existing adb server; restart is explicit.

## Not yet verified

- Hotplug p50 under 300 ms on a physical device and emulator pair.
- Metadata completion under 500 ms per physical device.
- Idle CPU under 1% over a sustained session.
- Recovery against a real `adb kill-server` under load.

The <300 ms and <500 ms budgets are enforced by design and fake-transport tests can
be instrumented to assert them, but no physical-device measurement is recorded yet.
