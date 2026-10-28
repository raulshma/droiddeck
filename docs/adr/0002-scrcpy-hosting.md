# ADR 0002: Host scrcpy as a docked companion window with an external-window fallback

- **Status:** Accepted
- **Date:** 2026-10-28
- **Decision owners:** DroidDeck desktop
- **Related work:** #28 (POC-04), #29, #30, #31

## Context

DroidDeck needs an embedded-feeling device mirror without taking ownership of
scrcpy's rendering, input, or crash handling. Three hosting options were
evaluated:

1. **Embedded child window** — reparent the scrcpy `SDL_app` window into the
   WinUI workspace with `SetParent`.
2. **Docked companion window** — run scrcpy as its own top-level borderless
   window, positioned and resized over the host's mirror pane.
3. **External window** — let scrcpy open a normal movable window.

The mirror must start quickly, survive pane resizes and DPI changes, keep
keyboard focus predictable, and never leave orphan windows or truncated
recordings.

## Measurements

Environment: Windows 11, 150% display scaling, scrcpy 4.1
(`scrcpy-win64-v4.1`), USB-connected POCO F1 (1080x2246, Android 15), ADB
platform-tools 36.

| Probe | Result |
| --- | --- |
| scrcpy start to a visible top-level window | ~0.9 s |
| Docked launch with `--window-x=100 --window-y=100 --window-width=500 --window-height=800 --window-borderless` | Window appeared at `67,67`, size `256x533`; scrcpy interprets these options as DIPs and preserves the device aspect ratio |
| `SetWindowPos` resize to `700x900` | Applied in <25 ms; window rect was `700x900` after a 400 ms settle wait |
| `WM_CLOSE` to the `SDL_app` window | Process exited in 164 ms, no orphan |
| Headless recorder window hidden off-screen, 5 s of capture, then `WM_CLOSE` | Playable MKV, 137,129 bytes, ffprobe duration 3.743 s (startup accounts for the difference), exit code 0 |
| Force-terminating the recorder without `WM_CLOSE` | MKV remained 0 bytes / unreadable |

Two non-obvious findings drove the implementation:

- **scrcpy coordinates are DIPs, not physical pixels.** The host must pass
  effective pixels (the same units WinUI reports) and let scrcpy apply the
  monitor DPI. A physical-pixel rectangle is misplaced and scaled on a 150%
  display.
- **The recorder needs a window to close gracefully.** `--no-window` leaves no
  handle to request shutdown; killing the process leaves the muxer buffer
  unwritten. The recorder now runs `--no-playback --no-control` with a 1x1,
  off-screen, borderless window that DroidDeck hides immediately. Stop sends
  `WM_CLOSE`, waits for the process to flush, and only then falls back to
  terminating the process tree.
- **Pseudo console windows must be ignored.** scrcpy owns a
  `PseudoConsoleWindow` with no client area alongside the real `SDL_app`
  window. Window lookup prefers the `SDL_app` class and ignores zero-sized
  windows, otherwise close requests target the wrong window.

## Decision

**Default: docked companion window (option 2). Fallback: external window
(option 3). Embedded child-window hosting is a no-go for M2.**

- The host starts scrcpy with `--window-borderless` and the pane rectangle.
  `Win32ScreenWindowController` moves/focuses/closes the native window using
  `SetWindowPos`, `SetForegroundWindow`, and `WM_CLOSE`.
- If docked startup throws or the process exits during the 300 ms startup
  probe, DroidDeck retries once with `HostingMode.ExternalWindow`, clears the
  bounds, and returns a non-fatal notice that the UI displays.
- Pane resizes call `IScreenService.UpdateBoundsAsync`; a failed move leaves
  the external window usable and records a notice instead of failing the
  session.
- Recordings use a second, headless scrcpy process with a hidden companion
  window so starting or stopping a recording never restarts or flickers the
  interactive mirror.
- Every session holds a resource-guard lease. `StopAsync`, device loss, and
  `DisposeAsync` close the window first, wait up to two seconds, then
  terminate the process tree. No path leaves a running scrcpy process.

### Why embedded hosting is a no-go for M2

Reparenting an SDL window into WinUI creates a large surface we cannot verify
within the milestone:

- SDL expects to own its top-level window; `SetParent` changes focus, cursor,
  and DPI behavior, and keyboard shortcuts can leak into the host.
- A host crash leaves a parentless child window owned by a dead process.
- Resize feedback loops and per-monitor DPI transitions require custom
  `WM_DPICHANGED` handling inside code we do not control.
- The docked companion already provides the intended single-workspace feel
  with predictable focus and no rendering ownership.

Revisit embedded hosting only with a dedicated spike that owns a renderer (or
a stable scrcpy embedding API), not by reparenting an SDL window.

## Contract

There is no public `IScreenSession` type; the service contract is:

```csharp
public interface IScreenService : IAsyncDisposable
{
    IReadOnlyList<ScreenSessionInfo> ActiveSessions { get; }
    bool IsAvailable { get; }
    string? AvailabilityError { get; }
    event EventHandler? SessionsChanged;

    Task<ScreenOperationResult> StartAsync(string serial, ScreenStartOptions? options = null, CancellationToken ct = default);
    Task<ScreenOperationResult> StopAsync(string serial, CancellationToken ct = default);
    Task StopAllAsync(CancellationToken ct = default);
    Task<ScreenOperationResult> UpdateBoundsAsync(string serial, ScreenBounds bounds, CancellationToken ct = default);
    Task<ScreenOperationResult> FocusAsync(string serial, CancellationToken ct = default);
    Task<ScreenOperationResult> PressKeyAsync(string serial, DeviceControlKey key, CancellationToken ct = default);
    Task<ScreenOperationResult> ScreenshotAsync(string serial, string? directory = null, CancellationToken ct = default);
    Task<ScreenOperationResult> StartRecordingAsync(string serial, string? directory = null, CancellationToken ct = default);
    Task<ScreenOperationResult> StopRecordingAsync(string serial, CancellationToken ct = default);
}
```

`ScreenSessionInfo` exposes the device serial, process ID, hosting mode,
lifecycle state, exact command line, recording path, window title, and a
non-fatal notice. `IScreenWindowController` isolates all Win32 calls so tests
can verify lifecycle behavior without a desktop.

## Known failure modes

| Failure | Behavior |
| --- | --- |
| scrcpy missing | `IsAvailable` is false; actions return install/settings/USB-debugging guidance |
| scrcpy older than 2.0 | The 2.x-only flags make scrcpy exit during the startup probe; the failure names the 2.0+ requirement and the install command |
| Wrong monitor DPI | Bounds are passed as DIPs; a physical-pixel rectangle must be divided by the scale before calling the service |
| Aspect-ratio mismatch | scrcpy shrinks the requested window to the device aspect; the pane letterboxes rather than distorting the mirror |
| Window not created yet | Focus and bounds calls retry for up to two seconds, then report a retry/use-external-window message; the startup fallback treats an immediate exit as docked failure |
| Focus restrictions | `SetForegroundWindow` may be refused by Windows; focus failure is reported, not treated as session failure |
| Resize during startup | Move failure is non-fatal and leaves the external window usable |
| Recorder window leak | Recorder window is off-screen and hidden after launch; stop closes it before process termination |
| Force-killed recorder | MKV may be truncated; graceful `WM_CLOSE` is attempted first and is the supported path |
| Device disconnect | Monitor observes process exit, finalizes the recording, releases the mirror/recording leases, and removes the session |
| App shutdown | `DisposeAsync` stops every session and recording before exit |

## Consequences

- DroidDeck owns positioning, focus, resize, and shutdown, but not rendering.
- The mirror is a separate top-level window; it can be moved to another
  monitor by the user and is not clipped by the app window.
- Recording adds one scrcpy process and one hidden window per active
  recording, counted by the resource guard.
- Tests cover command construction, startup fallback, stop/cleanup,
  screenshots, key mapping, recording lifecycle, bounds, thumbnails, and
  resource limits without requiring a desktop session.
- No POC/SetParent code remains in shipping paths.
