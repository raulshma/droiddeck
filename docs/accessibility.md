# Accessibility and languages

DroidDeck is designed to be usable with a keyboard alone, with a screen reader,
at 200% display scaling, in high contrast, without animation, and in more than
one language. This page describes what is guaranteed today and how to verify it.

## Keyboard

Every main flow — navigation, device selection, project search, runs, logs,
mirroring, settings, and plugin installs — can be completed without a mouse.

| Keys | Action |
| --- | --- |
| `F6` / `Shift+F6` | Cycle focus through the shell regions: navigation pane, page content, status bar. |
| `Tab` / `Shift+Tab` | Move focus inside the current region. Focus wraps at the ends of the window instead of escaping to the desktop. |
| `Ctrl+K` | Open the command palette. `Up`/`Down` move the selection, `Enter` runs it, `Escape` closes it. |
| `Alt` | Reveal access keys for the shell. |
| `Alt`+access key | Jump to a navigation destination (for example `Alt+H` for Home). |
| `Alt+I` / `Alt+F` / `Alt+C` | Install APK, finish setup, and copy diagnostics from the status bar. |
| `F5` / `Shift+F5` | Run on the selected devices / stop running sessions. |
| `Escape` | Close the palette, an open dialog, or a flyout. |

Remap single-key and multi-step chords in **Settings → Keyboard shortcuts**. The
shell registers those accelerators at window level, so they work from any page,
and menu items show the effective chord next to the command. Chord steps do not
fire while a text field has focus.

## Narrator and screen readers

- The navigation pane, menu bar, command palette, and status bar expose
  accessible names and automation ids.
- The current page title is a `Level1` heading; each page's sections follow in
  reading order. Titles wrap instead of clipping so they stay readable when
  enlarged.
- Status text, the notification bar, the device count, the install and setup
  results, and the drag-and-drop overlay are live regions
  (`AutomationProperties.LiveSetting`), so Narrator announces state changes
  without you having to move focus.
- Progress, error, and empty states are surfaced as text, not color alone.
- Icons that carry meaning alone have accessible names; decorative glyphs do not
  create noise in the accessibility tree.

## Display scaling

Layouts use `Auto` sizing, minimum row heights, and wrapping text rather than
fixed pixel sizes. At **200%** scaling (Windows **Settings → Display → Scale**):

- Navigation labels, menu items, status text, and dialog content remain fully
  visible; long text trims with an accessible full value where a single line is
  intentional.
- The command palette, notification bar, and session panels are bounded by
  `MaxWidth` and centered rather than pinned to an absolute position.
- Page content scrolls vertically instead of being cut off.

If you find a control that clips at 200%, capture a screenshot and the display
scale used, then [report it](#reporting-a-problem).

## High contrast

When Windows high contrast is active, DroidDeck hands the palette to the system:

- Light, dark, and custom themes no longer set background, text, or accent
  colors, so the system high-contrast colors always win.
- Custom accent overrides are ignored in high contrast.
- A custom font family still applies, because it does not affect contrast.
- Focus rectangles, selection, and disabled states come from the system theme.

The rule lives in `ShellThemePolicy.ResolvePalette` and is covered by
`ThemeTests`. Custom theme contrast is checked when a theme is imported; the
Built-in themes meet WCAG AA contrast for body text and controls in their normal
state.

## Reduced motion

Animations are removed when any of these is true:

- **Settings → Appearance → Reduced motion** is on,
- the active theme requests reduced motion, or
- Windows **Animation effects** is disabled in **Accessibility → Visual effects**.

Transitions between pages are dropped entirely in that case; state changes still
occur immediately.

## Languages

**Settings → Appearance → Language** controls the interface language and applies
without a restart: the shell re-localizes, page resources are reloaded, and the
current page is reopened in the new language.

| Value | Result |
| --- | --- |
| `system` (default) | Follow the Windows display language; English when DroidDeck has no catalog for it. |
| `english` | English (`en-US`). |
| `pseudo` | A generated pseudo-locale (`qps-ploc`) used to test localization. |

The **pseudo-locale** wraps every string in `[!!` and `!!]`, accents letters, and
expands vowels so strings grow by at least 30 percent. Placeholders such as
`{0}` are preserved. Run the app in the pseudo-locale to find hardcoded English
and clipped layouts; every user-visible string should show the markers and the
layout should still fit.

### How localization is structured

- `src/DroidDeck.Core/Localization/en-US.json` is the English catalog embedded in
  the Core assembly. English is the source of truth and the fallback for every
  other culture.
- `Localizer` exposes catalog entries as `Loc.*` resources, which XAML consumes
  with `{StaticResource Loc.page.home.title}`. Code paths use
  `Localizer.Get` / `Format` rather than literal text.
- Layouts are written so that translated text can expand and flow
  right-to-left: nothing depends on left-to-right ordering, and the shell sets
  `FlowDirection` from the active culture so an RTL catalog will mirror the
  window when one is added.

To add a language, add the catalog next to `en-US.json`, map its culture in
`Localizer.Resolve`, and extend the language picker. Architecture tests
(`LocalizationArchitectureTests`) enforce that every `Loc.*` reference in XAML
exists, every page title is a localized wrapping heading, and the shell contains
no hardcoded user-facing text.

## Verifying accessibility before a release

Run these checks on a clean profile:

1. Disconnect the mouse and complete the golden path: connect a device, add a
   project, run, open the session, mirror, and change a setting.
2. Turn on Narrator and confirm the page title, status changes, and errors are
   announced.
3. Set display scaling to 200% and re-check the shell, settings, and session
   pages for clipping.
4. Switch to a high-contrast theme and confirm text remains readable.
5. Disable Windows animation effects and confirm transitions stop.
6. Switch the language to `pseudo` and confirm every page is marked and nothing
   is cut off.

The `DroidDeck.Architecture.Tests` project encodes the structural parts of
checks 2, 3, and 6 so regressions fail CI without a manual pass.

## Reporting a problem

Open an issue with the page, the assistive technology or display setting in use,
and what you expected. If the problem involves an error, attach a diagnostics
report from **Diagnostics** or **About**; the report is redacted locally and
never uploaded automatically. See the [security policy](security.md) for private
disclosure.
