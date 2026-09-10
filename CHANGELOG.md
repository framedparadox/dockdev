# Changelog

All notable changes to dockdev are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
`Major.Minor.Build.0` versions — the Store reserves the fourth component, so it stays `0`.

## [1.2.0.0]

### Changed

- **New app icon and logo.** The tile, splash screen, taskbar icon and README branding now use the
  new dockdev mark in place of the old hexagon logo.

## [1.1.0.0]

Stability and reliability release: dockdev is an always-on tray utility, so the guiding rule is that
nothing a tool is handed — however malformed, however large — may take the process down or wedge it.

### Fixed

- **The app no longer closes itself.** A WinUI desktop app quits when its last window closes; the
  dock was normally the only one, so Alt+F4, an unhandled fault in its content, or the
  close-and-recreate a language change performs each ended the whole process — tray icon and global
  shortcuts included — with no window left to explain why. The event loop now outlives every window,
  the dock hides instead of closing on a close request it did not initiate, and a dock that goes
  down anyway is put back rather than leaving a tray icon with nothing behind it.
- **Deeply nested XML can no longer crash the app.** JSON was already depth-capped; XML was not, so a
  sub-100 KB document a few thousand elements deep — well within the size ceiling — could overflow
  the call stack while converting or outlining it (an *uncatchable* fault that kills the process) or
  exhaust memory on an indented format. XML is now refused past a nesting cap with a plain error,
  the same way an oversized or malformed input already was.
- **The Data Masker no longer freezes on a large, PII-dense file.** Pasting a file that is mostly
  personal data — a contacts export, a key dump — could produce hundreds of thousands of findings,
  and the findings list built a row per finding into a non-virtualizing list, freezing the window
  for minutes or exhausting memory. Everything is still masked; the individual-tuning list is now
  capped, with a note for the remainder.
- **Memory leaks behind repeated use.** A replaced dock kept its whole visual tree reachable through
  the item template's bindings; a tool icon decode could pin its bitmap and handlers for the life of
  the process; the dock's inline drop tip accumulated one copy per drop. All now release on cue.
- **Faults are contained, not fatal.** File pickers, tool-command invocation, tool-window launches,
  the regex runner, drop handlers and the battery-saver backdrop callback are guarded at source, with
  an app-wide handler that logs and recovers as the net beneath them rather than the plan.
- **Every shipped language is complete again.** Five interface strings had drifted out of the seven
  non-English tables (and five stale keys lingered in them); all eight languages are back at parity,
  verified in the test suite so it cannot silently rot again.

### Added

- **Settings-button position.** Put the gear at the start of the dock strip instead of the end.
- **Enable-all in Tools settings.** Turn on every tool in one action.
- **Soak and stress test suites.** A headless suite pins the tool engines as pure, idempotent and
  stateless across repeated use; an opt-in UI suite drives every tool through repeated
  paste → transform → copy cycles and fails on a swallowed fault, an unresponsive window, or handle,
  GDI/USER and memory growth per cycle — the exact shapes the leaks above took.

## [1.0.0.0]

- Initial release: a floating dock of offline developer tools for Windows — format and convert
  (JSON, XML, CSV), encode and decode (Base64, URL, JWT, hash/HMAC), mask personal data, and a set
  of text, number and time utilities. Everything runs locally with no network access unless the
  optional update check is switched on.
