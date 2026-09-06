# dockdev crash & leak test checklist

For the change that touches a tool page, a shared control, or window lifetime — and for the release
that ships them. Everything here is either automated (run it) or manual (do it), and the manual half
exists only where automation genuinely cannot reach.

Bug reports of the shape *"it crashes after I use it a few times"* are what this document is for.
Work top to bottom; the cheap checks are first on purpose.

---

## 0. Before you start

- [ ] Build clean: `dotnet build src/dockdev/dockdev.csproj -p:Platform=x64 -warnaserror`
- [ ] Know where the evidence lives. There are **two** logs and they catch different things:
  - `%TEMP%\dockdev.log` — everything the app caught and handled. Truncated on every launch.
  - Windows **Application** event log, `.NET Runtime` **id 1026** — the faults that killed the
    process before the app could log anything. An access violation, a stack overflow, a fail-fast.
  - A crash that appears in neither did not happen. A crash in only the second one is the dangerous
    kind: **the app vanishes and leaves no trace of its own.**

---

## 1. Automated — run these

- [ ] `dotnet test tests/dockdev.Tests/dockdev.Tests.csproj -p:Platform=x64`
      *(unit + soak; no desktop; seconds)*
- [ ] `./scripts/run-ui-tests.ps1`
      *(smoke + soak + lifetime; needs an unlocked desktop; minutes)*
- [ ] Read the soak output, don't just read the exit code. Each tool prints its resource growth:
      `Json: growth over 12 measured cycles: private=4MB handles=3 gdi=0 user=0 threads=0`.
      **GDI and USER should be 0 or near it.** A steady climb that stays under the ceiling is still
      a leak; the ceiling is set loose to avoid false alarms, not to bless slow growth.
- [ ] After the UI run, check the Application event log even if every test passed — a crash during
      teardown can be invisible to the assertions:
      ```powershell
      Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-30)} |
        Where-Object { $_.Message -match 'dockdev' } |
        ForEach-Object { "=== $($_.TimeCreated) id=$($_.Id) ==="; $_.Message }
      ```

---

## 2. Manual — the repeat scenario

The soak suite automates this per tool. Do it by hand when you have changed a tool page, because a
human notices *wrongness* the assertions do not check for — a stale pane, a status bar stuck on the
last error, a button that says "Copied" forever.

For each tool you touched, **10–15 times without closing the window**:

- [ ] Paste unformatted input.
- [ ] Press the primary action (Format / Convert / Run / Generate).
- [ ] Press Copy, and actually paste the clipboard somewhere to confirm it changed.
- [ ] Watch for: the copy confirmation resetting each time (not sticking on "Copied"); the status
      bar reflecting *this* press; the output pane matching the input pane; syntax colours redrawn,
      not smeared.
- [ ] Then close the window. **The close is part of the test** — several of these faults only fire
      on teardown, after a debounced timer has been armed and the window has gone.

Then, still by hand:

- [ ] Do it once more, and close the window **within a second of the last keystroke**. That is the
      interval in which a queued re-highlight can outlive its document.
- [ ] Repeat with Alt+F4 rather than the title-bar X.
- [ ] Answer the discard prompt both ways — Discard, and Cancel then close again.
- [ ] Press Alt+F4 **twice quickly** on a dirty window. A second confirmation dialog in one
      `XamlRoot` throws, on an `async void` path with no caller.

---

## 3. Manual — window lifetime

The app is a tray utility whose event loop outlives its windows (`DispatcherShutdownMode.OnExplicitShutdown`).
Every one of these used to end the process:

- [ ] Alt+F4 with the dock focused → the dock hides; the tray icon and shortcuts still work.
- [ ] Close every tool window, then summon from the tray → the dock comes back.
- [ ] Change the language, then open a tool → the rebuilt dock still launches tools.
- [ ] Import a backup → same.
- [ ] Toggle theme with several tool windows open → all of them repaint; none is orphaned.
- [ ] Quit from the tray is the *only* thing that ends the process. Verify it does.

---

## 4. Manual — inputs that surprise a parser

The attack surface is the pasted text. For each editor-shaped tool:

- [ ] Empty input, then press every command.
- [ ] Whitespace only.
- [ ] Half-typed structure — `{"a":` — then press Format, and leave it half-typed while the syntax
      highlighter runs.
- [ ] Something very large (a few MB) — expect a message and a plain-text fallback, never a hang.
- [ ] Non-ASCII: emoji, RTL text, CJK, combining marks. Surrogate pairs are where character offsets
      and document offsets disagree, and the colouring pass indexes by offset.
- [ ] Very long single line with no newlines.
- [ ] Paste, then immediately paste something else before the 180 ms highlight debounce elapses.

---

## 5. Leak-specific review — the patterns that bite on repeat

When reviewing a tool page or a shared control, these are the five that have actually shipped here:

- [ ] **`+=` inside a method that runs per action.** A handler subscribed on every Copy leaves one
      live handler per copy. It must be subscribed once, in the constructor.
- [ ] **A timer created per invocation** rather than created once and restarted.
- [ ] **A panel's `Children` or an `ItemsSource` appended to per invocation** and never cleared —
      a findings list, an inline tip, a result row.
- [ ] **A `TaskCompletionSource` awaited that nothing is guaranteed to complete**, pinning whatever
      its handlers captured for the life of the process.
- [ ] **A timer, task or callback that outlives the window it targets.** Ask specifically: *what
      stops this if the window closes right now?* `Unloaded` is not a reliable answer — a WinUI
      window closing does not dependably raise it on its content.

---

## 6. Before release

- [ ] Full UI suite green on x64 **and** ARM64 if you ship both.
- [ ] Leave dockdev running for a working day with a tool window open; compare handle and GDI counts
      at the start and end (Task Manager, or `Get-Process dockdev | Select HandleCount`).
- [ ] Open every tool once from the dock, from quick-launch search, and by dropping a file on the
      dock. Three entry points, three code paths.
- [ ] Confirm `%TEMP%\dockdev.log` is empty of `UNHANDLED` and `UNOBSERVED TASK` after all of it.
- [ ] Confirm the Application event log has no `dockdev.exe` id 1026 from the session.

---

## Baseline: last full run

19 tools × 15 cycles, Debug x64, 2026-09-02. Every tool passes **in isolation**. Growth is over the
12 measured cycles (after a 3-cycle warm-up), not per cycle.

| Tool | Result | private | handles | gdi | user |
|---|---|---|---|---|---|
| Json | pass | −1 MB | −1 | 0 | 0 |
| DataFormatter | pass | 0 MB | 1 | 0 | 0 |
| Xml | pass | 2 MB | 1 | 0 | 0 |
| DataConverter | pass | 1 MB | 0 | 0 | 0 |
| Base64 | pass | 2 MB | 25 | 0 | 1 |
| UrlEncoding | pass | 0 MB | 2 | 0 | 0 |
| Jwt | pass | 2 MB | 0 | 0 | 0 |
| Hash | pass | 2 MB | 17 | −2 | −3 |
| **DataMasker** | pass | **15–16 MB** | 22–38 | 0 | 0 |
| TextToolkit | pass | 0 MB | 3 | 0 | 0 |
| TextDiff | pass | 0 MB | 2 | 0 | 0 |
| RegexTester | pass | 0 MB | −2 | 0 | 0 |
| Uuid | pass | 2 MB | 6 | 0 | 1 |
| Password | pass | 0 MB | −2 | 0 | 0 |
| Lorem | pass | 0 MB | 1 | 0 | 0 |
| Color | pass | 2 MB | 13 | 0 | 0 |
| Timestamp | pass | 0 MB | 11 | 0 | −1 |
| NumberBase | pass | 8 MB | 7 | 0 | 1 |
| Cron | pass | 0 MB | 14 | 0 | 0 |

**GDI and USER are flat everywhere.** That is the headline: no tool leaks a brush, pen, font,
window or timer per cycle. The one outlier on managed memory is the Data Masker, for the reason in
item 2 below.

`CodeEditorLifetimeTests`: 36 close-with-pending-highlight rounds across Json, Xml and
DataFormatter — no crash.

---

## Known open items

Kept here rather than in a tracker so the next person running this checklist sees them.

| # | Item | Evidence | Status |
|---|---|---|---|
| 1 | **The app can be terminated outright by its own syntax highlighter.** `CodeEditor.Highlight` died at `CharacterFormat.ForegroundColor`, called from the 180 ms debounce `Tick`. Neither the `try/catch (Exception)` around that line nor `App.UnhandledException` ran — the signature of a fault .NET does not deliver to a catch clause (an access violation), so widening the `catch` would not help. **The app vanishes leaving nothing in `dockdev.log`.** Seen once, during a soak of the editor-shaped tools; not reproduced since, including 36 targeted rounds. Trigger not isolated — see `CodeEditorLifetimeTests` for exactly what is and is not established. | Windows Application log, .NET Runtime id 1026, 2026-09-01 20:52:46; full stack in `CodeEditorLifetimeTests`'s summary | Open — regression test added, app not changed |
| 2 | **The Data Masker corrupts text as you type.** `MaskerPage.Analyze` runs on every `TextChanged` with no debounce and ends in `Remask` → `Replace`, which assigns `CodeEditor.Text` and so calls `Document.SetText`. That rewrites the editor with the *masked* text mid-typing, and unlike `Highlight` it does not save and restore the insertion point — so what is typed next lands at the **front** of the document. Typing `Contact user5@example.com about card 4111 1111 1111 1111 (ref 5).` yields `).Contact CONTACT_5 about card *** (ref 5).` | `ToolStressTests` DataMasker read-back | Open |
| 3 | The same `Analyze` rebuilds the whole findings list per keystroke — a fresh `Border`+`Grid`+`CheckBox`+`ComboBox` (7 items) + 2 `TextBlock`s **per finding**, with fresh handlers, then a wholesale `ItemsSource` reassignment. It is the only tool with double-digit MB growth, and after its soak the *next* tool window's editor could not be found in the automation tree within 20 s — reproducibly. Whether that last part is app-side realization or UIA provider saturation is not established; either way a screen reader would see it too. | 15–16 MB vs 0–2 MB for its peers; `DataMasker,TextToolkit` batch fails on the second tool, each passes alone | Open |
| 4 | `ToolPage.Commands` is consumed only by `EditorToolPage.InitializeChrome`. Every `FormToolPage` subclass declares commands that are never rendered and never bound to an accelerator: UUID's Generate (`Ctrl+Enter`), Password's and Lorem's Run, Colour's Clear, Cron's Copy and Clear, Hash's Clear. The pages that need a button build their own; the declarations are dead. | `grep -rn "\.Commands" src/dockdev` returns exactly one hit | Open — dead declarations, not a crash |
| 5 | `ConverterPage.Convert` routes **ordinary invalid user input** through `Diag.Log(… " failed: " …)` — the same channel and wording the app uses for genuinely swallowed faults, so pressing Convert on half-typed JSON writes a line indistinguishable from a suppressed crash. `FormatterPage` treats the same condition as data and logs nothing. Secondarily, it reports `StatusBar.SetError(1, 1)` unconditionally while the exception it just caught carries the real position. | `ConverterPage.cs` vs `FormatterPage.cs`; the soak's log assertion fired on it | Open |
| 6 | Number Base has **no way to copy its result**. It declares no commands and, alone among the form-shaped tools, builds its output from bit toggles rather than `FormToolPage.ResultRow`, so it gets no per-row `CopyButton` either. Every other tool in the catalog can reach the clipboard. | `ToolStressTests` NumberBase row; `grep -c CopyButton NumberBasePage.cs` = 0 | Open — UX gap |
| 7 | `StringTableTests.EveryTableCarriesExactlyTheEnglishKeySet` fails: `de` is missing 5 keys and carries 5 stale ones. | `dotnet test tests/dockdev.Tests` | Pre-existing, unrelated to crashes |
