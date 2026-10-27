# ADR 0001: Unpackaged, self-contained WinUI 3 portable build

**Status:** Accepted (2026-10-28)

## Context

DroidDeck must run as a portable folder with no installer, no administrator rights,
and no separately installed .NET or Windows App SDK runtime. The shell also needs
custom title-bar behavior, Mica with fallback, navigation, single-instance
activation, and a predictable data location.

## Decision

- Use WinUI 3 on `net10.0-windows10.0.26100.0` with the Windows App SDK pinned through
  central package management.
- Ship unpackaged (`WindowsPackageType=None`).
- Publish self-contained and Windows App SDK self-contained for win-x64.
- Resolve data under `%LOCALAPPDATA%\DroidDeck`, with a portable `data` folder beside
  the executable when `portable.marker` exists, and an explicit `--data-dir` override.
- Keep the shell as the highest dependency layer; core, device, and plugin
  abstractions stay free of WinUI references.

## Consequences

- Users can launch `DroidDeck.exe` from any folder with no prerequisites.
- The artifact is large (about 229 MB extracted) and must be measured against the
  250 MB budget every release.
- Windows App SDK is serviced through NuGet updates and CI, not the Microsoft Store.
- Packaging (MSIX) remains available later if distribution requires it.

## Alternatives considered

- **MSIX packaging:** cleaner updates and OS integration, but adds signing,
  installation, and administrator friction that conflict with the portable goal.
- **Framework-dependent publish:** smaller output, but requires a .NET runtime on the
  target machine.
- **Separate Windows App SDK runtime install:** smaller app folder, but breaks the
  clean-machine requirement.

## Verification

- Debug/Release builds and portable publish run in CI.
- The UI automation tests launch the local unpackaged app build against FakeAdb.
- Clean-machine execution is tracked in `docs/poc/07-portable.md`.
