# dockdev tests

Three suites, in increasing order of cost and decreasing order of how often you should run them.

| Suite | Needs a desktop? | Runtime | What it is for |
|---|---|---|---|
| `dockdev.Tests` | No | ~3 s | Every pure service: formats, tokenizers, masking, text/number engines, and the architecture rules. Run it on every change. |
| `dockdev.Tests/Soak` | No | ~1 s | The repeat scenario at the engine level — idempotence, convergence, statelessness across calls. Part of `dockdev.Tests`; called out here because it is what a "do it fifteen times" bug report reduces to when you strip the UI away. |
| `dockdev.UITests` | **Yes** | minutes | Drives the shipped `dockdev.exe` through UI Automation. Opt-in. The only thing that can catch a fault on a timer tick, a leaked visual tree, or a window that stops answering. |

---

## Running them

### Unit and soak tests — anywhere

```powershell
dotnet test tests/dockdev.Tests/dockdev.Tests.csproj -p:Platform=x64
```

No desktop, no environment variables, no app running. This is what CI runs.

### UI tests — a real, unlocked, logged-in desktop

```powershell
./scripts/run-ui-tests.ps1                       # builds Release, runs everything
./scripts/run-ui-tests.ps1 -SkipBuild            # against whatever is already built
```

They are skipped, not failed, unless `DOCKDEV_UITESTS` is set — a headless agent would fail every
one of them for reasons that say nothing about the code, and a suite that cries wolf gets ignored.
The script sets it for you.

To drive a Debug build by hand:

```powershell
$env:DOCKDEV_UITESTS = '1'
$env:DOCKDEV_EXE = "$PWD\src\dockdev\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\dockdev.exe"
dotnet test tests/dockdev.UITests/dockdev.UITests.csproj -p:Platform=x64 -c Debug
```

While these run they own the keyboard and the foreground window. Don't type.

---

## The soak suite

`ToolStressTests` is the repeat-until-it-breaks suite, and it exists because the faults dockdev has
actually shipped were per-invocation ones: a `Tick` handler re-subscribed on every copy so the tenth
copy ran ten resets; a highlight timer left armed against a document that had gone; a drop tip added
to the visual tree once per drop and never removed. Every one of those passes a single-shot test by
construction. Only the tenth iteration tells them apart.

For each of the nineteen tools it opens **one** window and repeats fifteen cycles of

> clear the input → type fresh text → press the transform command → press copy

then checks four things at once:

1. the process is still running;
2. nothing was swallowed into `%TEMP%\dockdev.log` while the loop ran;
3. the window still answers automation — its input is still findable;
4. handles, GDI/USER objects and private bytes are not climbing per cycle.

### Running one tool at a time

A theory row **cannot** be selected with `dotnet test --filter`. VSTest applies a filter at
discovery, where a theory is still one test case whose `FullyQualifiedName` carries no arguments, so
`--filter "DisplayName~Json"` matches nothing however you spell it. Select at the data source
instead:

```powershell
$env:DOCKDEV_SOAK_TOOLS = 'Json'                 # or 'Json,Xml,DataConverter'
dotnet test tests/dockdev.UITests/dockdev.UITests.csproj -p:Platform=x64 -c Debug `
  --filter "FullyQualifiedName~SurvivesRepeatedEditTransformCopy"
```

A name with no soak row is a hard failure rather than an empty run — a typo that silently soaks
nothing and reports success is the worst thing this suite could do.

### Knobs

| Variable | Default | Effect |
|---|---|---|
| `DOCKDEV_UITESTS` | unset | Must be set, to anything, or every UI case skips. |
| `DOCKDEV_EXE` | newest Release, else Debug | Which binary to drive. Set it; don't let a months-old build be tested by accident. |
| `DOCKDEV_SOAK_TOOLS` | all | Comma-separated `ToolKind` names to soak. |
| `DOCKDEV_SOAK_CYCLES` | 15 | Cycles per tool. Raise it when hunting something slow. |
| `DOCKDEV_LIFETIME_ROUNDS` | 12 | Open/type/close rounds in `CodeEditorLifetimeTests`. |

### Concurrency

`dockdevSession` takes a machine-wide mutex (`Local\dockdev.UITests.Desktop`), so two `dotnet test`
processes queue instead of fighting over one keyboard focus and one clipboard. Within a process
xUnit already serializes the collection; the mutex is what makes *per-tool* runs — several
invocations, possibly from several agents — safe. Expect a run to block for minutes waiting its
turn; that is the mechanism working.

---

## Reading a failure

### "…is holding on to something per cycle"

A leak. The message names which counter moved and by how much per cycle, with the baseline and final
samples. GDI and USER growth are the sharpest signals — they are monotonic and unambiguous, and the
per-process GDI quota is 10,000, so one leaked object per cycle is a ceiling a long session really
reaches. Private-byte growth is the noisiest; a few MB over twelve cycles is a GC that has not run,
not a leak.

### "…logged N swallowed fault(s)"

Something threw where nothing could catch it and `App.UnhandledException` handled it. The app stayed
up; the user saw a command that silently did nothing. The offending lines are in the message and in
`%TEMP%\dockdev.log`.

### The app vanished and the log says nothing

**This is the important one, and it is the reason `AppHealth` exists.**

Since `App.UnhandledException` began setting `Handled = true`, an ordinary managed fault no longer
kills dockdev — it lands in the log. So if the process is *gone* and the log is silent, the fault was
one .NET does not deliver to a `catch (Exception)` at all: an access violation in native code, a
stack overflow, a fail-fast. None of those reach the app's handler, and none of them write to
`dockdev.log`. Look in the Windows Application log instead:

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-15)} |
  Where-Object { $_.Message -match 'dockdev' } |
  ForEach-Object { "=== $($_.TimeCreated) id=$($_.Id) ==="; $_.Message }
```

A `.NET Runtime` event **id 1026** carries the managed stack. That is how the crash in
`CodeEditorLifetimeTests`'s summary was found; nothing in the app's own diagnostics knew about it.

---

## Adding a tool

Add a row in **three** places, or a guard test will tell you which one you missed:

1. `dockdevSession.ToolKinds` — the fixture seeds a dock item per entry.
2. `ToolSmokeTests.Transforms` (if it transforms text) — checked by `EveryCatalogToolHasARow`.
3. `ToolStressTests.All` — checked by `EveryToolIsSoaked`.

A soak row needs to know three things about the tool:

- **Input** — a `Func<int, string>` taking the cycle number, so every iteration types *different*
  text. A constant would let a cache or an equality guard skip the work, and the loop would soak
  nothing while reporting that it had. `null` for a generator, which has no input.
- **Action** — the `ToolCommand.Id` to press, or `null` for a tool that recomputes as you type.
  Form-shaped tools have no command bar at all (see below), so they also need `ActionLabel`, the
  English label of their in-form button.
- **Copy** — `Command` for a command-bar copy button, `ResultRow` for a form tool's per-row
  `CopyButton`, `None` for a two-pane tool that has no copy of its own.

### Two page archetypes, two sets of rules

`EditorToolPage` builds a `CommandBar` from `ToolPage.Commands` in `InitializeChrome`.
**`FormToolPage` does not** — and `Commands` is read nowhere else in the app, so a form-shaped
tool's declared commands are never rendered and never bound to an accelerator. That is why the soak
table addresses UUID, Password and Lorem by button label rather than by command id, and why Cron's
copy is a result-row button rather than its declared `Copy` command.

### Typing into an editor is not an assignment

A form tool's input is a plain `TextBox`, which offers the UI Automation Value pattern, so assigning
`Text` replaces its contents. An editor tool's input is the `RichEditBox` inside `CodeEditor`, which
offers **no** Value pattern — so FlaUI's identical-looking assignment silently falls back to focusing
and typing, and typing at the caret *appends*. `Ctrl+A` sent through automation does not reach that
control either. The only deterministic reset is the tool's own **Clear** command, which is what
`SetInput` uses; `AssertInputTookHold` then verifies the text actually replaced, so a loop that stops
driving the tool fails instead of passing quietly.

This is not hypothetical. The first run of this suite had the Data Converter row parsing `{…}{…}`
from cycle two onwards: the tool was correctly reporting invalid JSON and the test was faithfully
measuring its own bug.

Nor is the opposite direction. Keystrokes go through `SendInput`, which is asynchronous, while
commands are pressed through the `Invoke` pattern, which is not — so a naive harness presses Format
while the text is still arriving and the tool transforms a *prefix*. `AssertInputTookHold` therefore
checks for too-little as well as too-much, and `SetInput` retries the whole clear-and-type up to
three times: over a run of minutes something else on the desktop eventually steals the foreground,
and the text lands in another window. That is an environment failure, not an app one.

`TypeInChunks` exists for a related reason that *is* about the app: `MaskerPage.Analyze` and
`RegexPage.Run` both do their full work synchronously on every `TextChanged`, where `DiffPage` and
`CodeEditor` debounce. Keystrokes injected faster than that work completes are simply lost.

### One tool rewrites its own input

The Data Masker masks live, on every keystroke, by assigning `CodeEditor.Text` — so its editor
cannot be asked to read back what was typed, and its row carries `RewritesOwnInput = true` to lift
that assertion. This is a finding, not a quirk: the rewrite does not preserve the insertion point,
so typed text ends up reordered. See item 2 in [TEST-CHECKLIST.md](TEST-CHECKLIST.md).

Running the Data Masker and then another tool in the **same** batch currently fails on the second
tool — its editor is missing from the automation tree. Each passes alone. Use `DOCKDEV_SOAK_TOOLS`
to run per tool until that is resolved.
