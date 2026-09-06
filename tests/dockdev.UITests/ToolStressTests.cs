using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.Core.Definitions;
using Xunit;
using Xunit.Abstractions;

namespace dockdev.UITests;

/// <summary>
/// The repeat-until-it-breaks suite: paste, transform, copy — fifteen times, in one window, for
/// every tool.
/// <para>
/// <b>Why this exists separately from <see cref="ToolSmokeTests"/>.</b> The smoke suite asks "does
/// this tool work?" and answers it once. The faults this app has actually shipped were not of that
/// shape. They were per-invocation: a <c>Tick</c> handler re-subscribed on every copy, so the tenth
/// copy ran ten resets; a highlight timer left armed against a document that had gone; a drop tip
/// added to the visual tree once per drop and never removed. Every one of those passes a
/// single-shot test by construction, because one iteration is exactly the case where they are
/// indistinguishable from correct code. Only the tenth iteration tells them apart.
/// </para>
/// <para>
/// <b>What counts as a crash here.</b> Not "the process died" — that is the one symptom this app
/// has been deliberately engineered out of. <c>App.UnhandledException</c> now logs and sets
/// <c>Handled = true</c>, so a fault on a timer tick or in an <c>async void</c> handler leaves the
/// window on screen and the process alive, and says so only in <c>%TEMP%\dockdev.log</c>. A soak
/// test that asserted liveness alone would sail straight past the entire class of bug it was
/// written for. So the oracle is four things at once, and the log is the sharpest of them:
/// </para>
/// <list type="number">
///   <item>the process is still running;</item>
///   <item>nothing swallowed a fault into the log while the loop ran (see <see cref="AppHealth"/>);</item>
///   <item>the window still answers automation — its input control is still findable;</item>
///   <item>handles, GDI/USER objects and private bytes are not climbing per iteration.</item>
/// </list>
/// </summary>
[Collection(dockdevCollection.Name)]
public class ToolStressTests(dockdevSession session, ITestOutputHelper output)
{
    /// <summary>
    /// How many paste → transform → copy cycles each tool gets. Fifteen is the top of the range a
    /// person doing this by hand would call "a few times"; it is also comfortably past the point
    /// where a once-per-iteration leak stops being lost in the noise of the first render.
    /// Override with <c>DOCKDEV_SOAK_CYCLES</c> when hunting something that needs longer.
    /// </summary>
    public static int Cycles =>
        int.TryParse(Environment.GetEnvironmentVariable("DOCKDEV_SOAK_CYCLES"), out var n) && n > 0 ? n : 15;

    /// <summary>
    /// Cycles run before the resource baseline is taken. The first pass through a tool pays for
    /// things that are one-time and are not leaks — the tokenizer's first tables, the status bar's
    /// first brushes, the JIT — and counting those as growth would make every row fail. Measuring
    /// from cycle three onwards measures the steady state, which is the only part a leak lives in.
    /// </summary>
    private const int WarmupCycles = 3;

    // ---- Growth ceilings ----------------------------------------------------
    //
    // Deliberately loose. These are not performance budgets; they are the line between "this
    // allocates, as all code does" and "this allocates something it never gives back, once per
    // keystroke-and-click". A real per-iteration leak clears them by an order of magnitude — the
    // Tick-handler bug added a live handler and a timer per copy — so loose ceilings cost nothing
    // in detection and buy the thing a soak suite most needs, which is not crying wolf.

    /// <summary>GDI objects a cycle may add and keep. A brush, pen, font or bitmap that outlives
    /// the operation lands here, and the per-process default quota is 10,000 — a genuine leak of
    /// one per cycle is a ceiling a long session really hits, not a theoretical one.</summary>
    private const int MaxGdiPerCycle = 4;

    /// <summary>USER objects a cycle may add and keep — windows, menus, timers. A
    /// <c>DispatcherQueueTimer</c> created per copy rather than once shows up here.</summary>
    private const int MaxUserPerCycle = 4;

    /// <summary>Kernel handles a cycle may add and keep: clipboard packages, file handles, events.</summary>
    private const int MaxHandlesPerCycle = 8;

    /// <summary>Private bytes a cycle may add and keep. Generous enough to absorb a GC that simply
    /// has not run — a retained visual tree or a retained document is far larger than this.</summary>
    private const long MaxPrivateBytesPerCycle = 2L * 1024 * 1024;

    // ---- The table ----------------------------------------------------------

    /// <summary>How a tool's result gets onto the clipboard.</summary>
    public enum CopyStyle
    {
        /// <summary>The tool has no copy affordance of its own — a two-pane tool whose output is a
        /// read-only view (Text Diff, Regex, URL, JWT). The cycle is edit-and-recompute only.</summary>
        None,

        /// <summary>The command bar's copy button, addressed by <c>ToolCommand.Ids.Copy</c>.</summary>
        Command,

        /// <summary>A form-shaped tool's per-row <c>CopyButton</c>; the first one in the window.</summary>
        ResultRow,
    }

    /// <summary>
    /// One tool's soak row: what to type, what to press, and how to copy the result.
    /// <para>
    /// <c>Input</c> takes the cycle number so every iteration types <em>different</em> text.
    /// Retyping one constant would let any cache, any "the text did not change" short-circuit and
    /// any equality guard skip the work entirely, and the test would then be soaking nothing while
    /// reporting that it had.
    /// </para>
    /// </summary>
    /// <param name="Action">The command id to press each cycle, or null for a tool that recomputes
    /// on input change and has no explicit action (Hash, Timestamp, Colour, Cron, the live ones).</param>
    public sealed record StressCase(
        string Kind,
        Func<int, string>? Input,
        string? Action,
        CopyStyle Copy)
    {
        /// <summary>The English label of an in-form action button, for the form-shaped tools whose
        /// declared <c>Commands</c> are never rendered. See <see cref="Press"/>.</summary>
        public string? ActionLabel { get; init; }

        /// <summary>
        /// This tool rewrites its own input pane as the user types, so the editor cannot be
        /// expected to read back what was typed and <see cref="AssertInputTookHold"/> does not
        /// apply. Only the Data Masker does this — see the remarks there.
        /// </summary>
        public bool RewritesOwnInput { get; init; }

        public override string ToString() => Kind;
    }

    /// <summary>
    /// The rows to run, narrowed by <c>DOCKDEV_SOAK_TOOLS</c> when it is set — a comma-separated
    /// list of <c>ToolKind</c> names, e.g. <c>DOCKDEV_SOAK_TOOLS=Json,Xml</c>.
    /// <para>
    /// A theory row cannot be picked with <c>dotnet test --filter</c>: VSTest applies a filter at
    /// discovery, where a theory is still one test case whose <c>FullyQualifiedName</c> carries no
    /// arguments, so <c>DisplayName~Json</c> matches nothing however it is spelled. Selecting at
    /// the data source is the only thing that works — and it is what makes a per-tool soak run
    /// possible at all, which is how a failure gets attributed to one tool instead of to "the run".
    /// </para>
    /// <para>
    /// An unknown name is a hard failure rather than an empty run: a typo that silently soaks
    /// nothing and reports success is the worst outcome this suite could have.
    /// </para>
    /// </summary>
    public static TheoryData<StressCase> Cases()
    {
        var requested = (Environment.GetEnvironmentVariable("DOCKDEV_SOAK_TOOLS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var rows = All.AsEnumerable();
        if (requested.Length > 0)
        {
            var unknown = requested
                .Where(name => !All.Any(c => c.Kind.Equals(name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (unknown.Count > 0)
                throw new ArgumentException(
                    "DOCKDEV_SOAK_TOOLS names tools with no soak row: " + string.Join(", ", unknown) +
                    ". Known: " + string.Join(", ", All.Select(c => c.Kind)));

            rows = All.Where(c => requested.Contains(c.Kind, StringComparer.OrdinalIgnoreCase));
        }

        var data = new TheoryData<StressCase>();
        foreach (var row in rows)
            data.Add(row);
        return data;
    }

    /// <summary>
    /// Every tool, with the shape of its own paste → transform → copy loop.
    /// <para>
    /// Kept in one table and checked against the catalog by <see cref="EveryToolIsSoaked"/>, for
    /// the same reason <see cref="ToolSmokeTests"/> does it: a tool added later without a row here
    /// is a tool nobody is soaking, and that should be a failing test rather than something to
    /// notice a year on.
    /// </para>
    /// </summary>
    public static readonly StressCase[] All =
    [
        // ---- The scenario as reported: JSON, unformatted in, Format, Copy. -------------------
        // The trailing "ok" key is not decoration: it keeps the literal from ending in two
        // consecutive closing braces, which a $$-interpolated raw string cannot express as content.
        new("Json", i => $$"""{"id":{{i}},"name":"row {{i}}","tags":["a","b"],"nested":{"x":1,"y":[1,2,3]},"ok":true}""",
            "format", CopyStyle.Command),
        new("DataFormatter", i => $$"""{"id":{{i}},"items":[{"k":"v{{i}}"},{"k":"w"}]}""",
            "format", CopyStyle.Command),
        new("Xml", i => $"<root><item id=\"{i}\"><name>row {i}</name><flag>true</flag></item></root>",
            "format", CopyStyle.Command),
        new("DataConverter", i => $$"""{"id":{{i}},"name":"row {{i}}","ok":true}""",
            "convert", CopyStyle.Command),

        // ---- Encode / decode ------------------------------------------------------------------
        new("Base64", i => $"dockdev soak cycle {i} — padding varies with {new string('x', i % 4)}",
            "run", CopyStyle.Command),
        // URL and JWT recompute as the input changes and publish into read-only views.
        new("UrlEncoding", i => $"a b&c={i}?d#e/f g+h", null, CopyStyle.None),
        new("Jwt", i => SampleJwt, null, CopyStyle.None),
        new("Hash", i => $"dockdev soak cycle {i}", null, CopyStyle.ResultRow),

        // ---- Privacy --------------------------------------------------------------------------
        // RewritesOwnInput, because it does — and that is a finding, not a quirk to route around.
        // MaskerPage.Analyze runs on every TextChanged with no debounce and ends in Remask ->
        // Replace, which assigns CodeEditor.Text and therefore calls Document.SetText. So the
        // editor is rewritten with the *masked* text mid-typing, and SetText does not preserve the
        // insertion point the way Highlight carefully does — it resets to the start, and whatever
        // is typed next lands at the front of the document. Typing the fixture below yields
        // ").Contact CONTACT_5 about card *** (ref 5)." — masked, and with its tail moved to the
        // head. The soak still runs; only the "reads back what I typed" contract is lifted.
        new("DataMasker", i => $"Contact user{i}@example.com about card 4111 1111 1111 1111 (ref {i}).",
            null, CopyStyle.Command) { RewritesOwnInput = true },

        // ---- Text -----------------------------------------------------------------------------
        new("TextToolkit", i => $"line one {i}\nline two {i}\nLINE THREE {i}\n  trailing   spaces  ",
            null, CopyStyle.Command),
        new("TextDiff", i => $"alpha {i}\nbravo\ncharlie {i}\ndelta", null, CopyStyle.None),
        new("RegexTester", i => $"match {i} me, and match {i + 1} me too", null, CopyStyle.None),

        // ---- Generate -------------------------------------------------------------------------
        // No input to vary: the whole point of a generator is that pressing it again gives
        // something new, so the cycle is press-and-copy. These three are form-shaped, so the id
        // finds nothing and the label is what actually presses the button — see Press.
        new("Uuid", null, "generate", CopyStyle.ResultRow) { ActionLabel = "Generate" },
        new("Password", null, "run", CopyStyle.ResultRow) { ActionLabel = "Generate" },
        new("Lorem", null, "run", CopyStyle.ResultRow) { ActionLabel = "Generate" },

        // ---- Numbers and time -----------------------------------------------------------------
        new("Color", i => $"#{0x336600 + (i * 0x111):X6}", null, CopyStyle.ResultRow),
        new("Timestamp", i => (1_700_000_000 + (i * 3600)).ToString(), null, CopyStyle.ResultRow),
        // CopyStyle.None because Number Base has no way to copy its result at all: it declares no
        // commands (`Commands => []`), and unlike every other form-shaped tool it builds its output
        // from bit toggles and operator buttons rather than from FormToolPage.ResultRow, so it gets
        // no per-row CopyButton either. Every other tool in the catalog can put its result on the
        // clipboard; this one cannot. Recorded here rather than worked around silently.
        new("NumberBase", i => (1234 + i).ToString(), null, CopyStyle.None),
        // Cron declares a Copy command, but it is form-shaped, so that command is never rendered;
        // its reachable copy is the result row's own button.
        new("Cron", i => $"{i % 60} */{(i % 12) + 1} * * *", null, CopyStyle.ResultRow),
    ];

    /// <summary>A structurally valid HS256 token — the JWT tool decodes header and payload without
    /// verifying, so this only has to parse.</summary>
    private const string SampleJwt =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9." +
        "eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ." +
        "SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

    // ---- The soak -----------------------------------------------------------

    /// <summary>
    /// The reported scenario, generalised: open the tool once, then paste-transform-copy
    /// <see cref="Cycles"/> times without closing it, and check afterwards that nothing broke
    /// quietly. One window for the whole loop is the point — reopening between iterations would
    /// reset exactly the accumulating state this is looking for.
    /// </summary>
    [UITheory]
    [MemberData(nameof(Cases))]
    public void SurvivesRepeatedEditTransformCopy(StressCase testCase)
    {
        var logMark = session.Health.LogMark();
        var window = session.OpenTool(testCase.Kind);
        ResourceSample baseline = default;

        try
        {
            for (int cycle = 1; cycle <= Cycles; cycle++)
            {
                RunOneCycle(window, testCase, cycle);

                Assert.False(session.Health.HasExited,
                    $"dockdev exited during cycle {cycle} of {Cycles} on '{testCase.Kind}' " +
                    $"(exit code {session.Health.ExitCode?.ToString() ?? "unknown"}). " +
                    Log(logMark));

                // Taken after the warm-up, so what follows is measured against a steady state
                // rather than against a cold window.
                if (cycle == WarmupCycles)
                    baseline = session.Health.Sample();
            }

            var final = session.Health.Sample();
            var growth = final.Since(baseline);
            int measured = Cycles - WarmupCycles;
            output.WriteLine($"{testCase.Kind}: {Cycles} cycles. baseline {baseline} -> final {final}");
            output.WriteLine($"{testCase.Kind}: growth over {measured} measured cycles: {growth}");

            AssertStillResponsive(window, testCase);
            AssertNothingFaulted(testCase, logMark);
            AssertNotLeaking(testCase, growth, measured, baseline, final);
        }
        finally
        {
            CloseAndSettle(window, testCase.Kind);
        }
    }

    /// <summary>
    /// Closes a soaked window and does not return until it is actually gone.
    /// <para>
    /// Stronger than <c>ToolSmokeTests.CloseDiscardingChanges</c>, and the difference matters here
    /// in a way it does not there. Every soaked window is dirty, so every close raises the discard
    /// dialog; that dialog is a XAML overlay, so the window stays in the desktop's child list until
    /// it is answered. A single look for the Discard button can miss it — and the next test's
    /// <c>OpenTool</c> then takes its "before" snapshot while a window is still on its way out, and
    /// matches the wrong window as the one it opened. That surfaces as the <em>following</em> tool
    /// failing with "no writable input control", which points at an innocent tool and hides the
    /// real cause.
    /// </para>
    /// </summary>
    private void CloseAndSettle(Window window, string kind)
    {
        nint handle;
        try { handle = window.Properties.NativeWindowHandle.Value; }
        catch (Exception) { return; } // already gone

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (!session.TopLevelWindows().Any(w => HandleOf(w) == handle))
                return;

            dockdevSession.CloseWindow(window);

            // Answer the discard prompt wherever it is. Searched from the window rather than
            // assumed present: a window that was cleared on its last cycle is not dirty and gets
            // no dialog at all.
            var discard = dockdevSession.Retry(
                () => window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button)
                    .And(cf.ByName("Discard"))),
                TimeSpan.FromSeconds(2));

            if (discard is not null)
                dockdevSession.Press(discard);

            dockdevSession.RetryUntil(
                () => !session.TopLevelWindows().Any(w => HandleOf(w) == handle),
                TimeSpan.FromSeconds(3));
        }

        output.WriteLine(
            $"WARNING: the '{kind}' window would not close within 20s. The next tool's window " +
            "lookup may match the wrong window.");
    }

    private static nint HandleOf(Window window)
    {
        try { return window.Properties.NativeWindowHandle.Value; }
        catch (Exception) { return nint.Zero; }
    }

    private static string SafeName(Window window)
    {
        try { return window.Name ?? "<unnamed>"; }
        catch (Exception) { return "<unreadable>"; }
    }

    /// <summary>One paste → transform → copy pass.</summary>
    private static void RunOneCycle(Window window, StressCase testCase, int cycle)
    {
        if (testCase.Input is { } text)
            SetInput(window, testCase, text(cycle));

        if (testCase.Action is { } action)
            Press(window, action, testCase.Kind, testCase.ActionLabel);

        switch (testCase.Copy)
        {
            case CopyStyle.Command:
                Press(window, "copy", testCase.Kind);
                break;
            case CopyStyle.ResultRow:
                CopyFirstResultRow(window, testCase.Kind);
                break;
        }
    }

    /// <summary>
    /// Replaces the tool's primary input with <paramref name="text"/>.
    /// <para>
    /// An editor-shaped tool is addressed by <c>AutomationIds.EditorInput</c>. A form-shaped one
    /// has no such id, so it is addressed as "the first text box that is not read-only" — which is
    /// its input by construction, since every result row on those pages is read-only. Taking simply
    /// the first <c>Edit</c> would sometimes take a result box instead, and the test would then be
    /// writing into a control the tool never reads.
    /// </para>
    /// <para>
    /// <b>Two different mechanisms, because there are two different controls.</b> A form-shaped
    /// tool's input is a plain <c>TextBox</c>, which offers the Value pattern, so assigning
    /// <c>Text</c> replaces its contents outright. An editor-shaped tool's input is the
    /// <c>RichEditBox</c> inside <c>CodeEditor</c>, which offers no Value pattern at all — so
    /// FlaUI's identical-looking assignment silently falls back to focusing and typing, and typing
    /// at the caret <em>appends</em>.
    /// </para>
    /// <para>
    /// That distinction is invisible on the first iteration, where the editor is empty, and it is
    /// exactly what a soak loop gets wrong on every iteration after. It is not hypothetical: the
    /// first run of this suite had the Data Converter row parsing <c>{…}{…}</c> from cycle two
    /// onwards. The tool was correctly reporting invalid JSON and the test was faithfully measuring
    /// its own bug. Nor is <c>Ctrl+A</c> a fix — sent through UI Automation it does not reach this
    /// control, so the following <c>Backspace</c> ate one character instead of the selection. The
    /// tool's own <c>Clear</c> command is the only deterministic reset, and it is a real user
    /// action rather than a synthetic one. <see cref="AssertInputTookHold"/> keeps all of this
    /// honest: if a cycle ever stops replacing the text, the run fails instead of quietly soaking
    /// a document that only grows.
    /// </para>
    /// </summary>
    private static void SetInput(Window window, StressCase testCase, string text)
    {
        var kind = testCase.Kind;
        var editor = dockdevSession.Retry(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("ToolEditorInput")),
            TimeSpan.FromSeconds(2));

        if (editor is null)
        {
            // A plain TextBox: the Value pattern replaces outright, so there is no race to lose.
            var box = dockdevSession.Retry(() => FirstWritableTextBox(window))
                ?? throw new InvalidOperationException(
                    $"'{kind}' has no writable input control. The window this test is driving is " +
                    $"'{SafeName(window)}' and contains ids [{CommandIds(window)}] and buttons " +
                    $"[{ButtonNames(window)}] — if that is not the {kind} tool, the previous test " +
                    "left a window behind and OpenTool matched the wrong one.");
            box.AsTextBox().Text = text;
            AssertInputTookHold(box, kind, text);
            return;
        }

        // A tool that rewrites its own input as you type cannot be asked to read back what was
        // typed. Drive it and move on: the soak is still exercising the full per-keystroke
        // detect-render-remask path, which is the expensive one and the one worth repeating.
        if (testCase.RewritesOwnInput)
        {
            try { window.SetForeground(); } catch (Exception) { /* best effort */ }
            ClearEditor(window, kind);
            editor.Focus();
            TypeInChunks(text);

            Assert.True(
                dockdevSession.Retry(() => SafeText(editor).Length > 0 ? "ok" : null,
                    TimeSpan.FromSeconds(3)) is not null,
                $"'{kind}' rewrites its own input, but after typing {text.Length} characters the " +
                "editor is empty — nothing reached it at all.");
            return;
        }

        // Typing is the fragile half, so it gets attempts rather than one shot. Keystrokes go
        // through SendInput, which delivers to whatever holds the foreground — and over a run of
        // minutes something else on the desktop eventually takes it, at which point the text lands
        // in another window and the editor is left empty. That is an environment failure, not an
        // app one, and retrying is the honest response to it; a run that fell over the first time a
        // notification stole focus would be blamed on the app.
        for (int attempt = 1; attempt <= TypingAttempts; attempt++)
        {
            try { window.SetForeground(); } catch (Exception) { /* best effort */ }

            ClearEditor(window, kind);
            editor.Focus();
            TypeInChunks(text);

            if (InputSettled(editor, text))
                return;
        }

        AssertInputTookHold(editor, kind, text); // one more read, this time to fail with a diagnosis
    }

    /// <summary>How many times to re-clear and re-type before calling it a failure.</summary>
    private const int TypingAttempts = 3;

    /// <summary>
    /// Types in small bursts with a pause between them, rather than handing the whole string to
    /// <c>SendInput</c> at once.
    /// <para>
    /// The pause is not padding. Several pages do real work synchronously on <em>every</em>
    /// <c>TextChanged</c> — <c>RegexPage.Run</c> and <c>MaskerPage.Analyze</c> both do, the latter
    /// running the full PII rule set and rebuilding its findings list per character, where
    /// <c>DiffPage</c> and <c>CodeEditor</c> both debounce instead. Keystrokes injected faster than
    /// that work completes are simply lost, and a test that lost them would report the tool as
    /// broken when what it had actually found was its own typing speed.
    /// </para>
    /// </summary>
    private static void TypeInChunks(string text)
    {
        const int ChunkSize = 8;
        for (int i = 0; i < text.Length; i += ChunkSize)
        {
            Keyboard.Type(text.Substring(i, Math.Min(ChunkSize, text.Length - i)));
            Thread.Sleep(25);
        }
    }

    /// <summary>True once the control holds exactly the text we typed. Polls, because SendInput is
    /// asynchronous — the keystrokes are still draining when this first looks.</summary>
    private static bool InputSettled(AutomationElement input, string expected)
    {
        var want = Normalize(expected);
        return dockdevSession.Retry(
            () => Normalize(SafeText(input)) == want ? "ok" : null,
            TimeSpan.FromSeconds(3)) is not null;
    }

    /// <summary>
    /// Empties an editor-shaped tool through its own Clear command — the one reset that goes
    /// through <c>CodeEditor.Text</c>'s setter, and therefore through <c>Document.SetText</c>,
    /// which genuinely replaces the document rather than editing it at a caret.
    /// </summary>
    private static void ClearEditor(Window window, string kind)
    {
        var clear = dockdevSession.Retry(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("clear")),
            TimeSpan.FromSeconds(2))
            ?? throw new InvalidOperationException(
                $"'{kind}' is editor-shaped but has no Clear command, so its input cannot be " +
                $"deterministically replaced between cycles. It offers: {CommandIds(window)}");
        dockdevSession.Press(clear);
    }

    /// <summary>
    /// The input control now holds exactly what we meant to put in it — no more, and no less.
    /// <para>
    /// A soak test that silently stops driving the tool still passes every liveness check and
    /// reports that fifteen cycles went fine while the app sat idle. This is the guard against
    /// that, and it has to catch failure in <em>both</em> directions:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Too much</b> — the previous cycle's text is still there and this one appended to
    ///   it, so the loop is soaking a document that only grows.</item>
    ///   <item><b>Too little</b> — the keystrokes have not finished arriving. Typing goes through
    ///   <c>SendInput</c>, which is asynchronous, while the command is pressed through the
    ///   <c>Invoke</c> pattern, which is not; nothing sequences the two. The transform then runs
    ///   against a <em>prefix</em> of the input.</item>
    /// </list>
    /// <para>
    /// The second half is not hypothetical either, and an earlier version of this method missed it
    /// by checking only the ceiling: the Data Converter row was converting truncated JSON on nearly
    /// every cycle, the tool was correctly reporting a parse error, and the run looked like an app
    /// fault. Comparing against the full expected text — and retrying until the input drains —
    /// is what makes the loop actually test the tool instead of the race.
    /// </para>
    /// <para>
    /// Compared after normalising line endings and trailing paragraph marks, because a
    /// <c>RichEditBox</c> reports a trailing <c>\r</c> the user did not type and may report
    /// newlines in either convention; an exact ordinal match would fail for reasons that say
    /// nothing about whether the text landed.
    /// </para>
    /// </summary>
    private static void AssertInputTookHold(AutomationElement input, string kind, string expected)
    {
        var want = Normalize(expected);

        var settled = dockdevSession.Retry(
            () => Normalize(SafeText(input)) == want ? "ok" : null,
            TimeSpan.FromSeconds(5));

        if (settled is not null)
            return;

        var actual = Normalize(SafeText(input));
        var diagnosis = actual.Length < want.Length
            ? $"only {actual.Length} of {want.Length} characters arrived — the typed input had not " +
              "drained before the assertion, so the tool would have run against a prefix"
            : actual.Length > want.Length
                ? $"{actual.Length} characters where {want.Length} were typed — the previous cycle's " +
                  "text is still in there, so this loop is growing a document rather than replacing it"
                : "the text is the right length but not the right text";

        Assert.Fail(
            $"'{kind}': the input did not take hold — {diagnosis}." + Environment.NewLine +
            $"wanted: {Truncate(want)}" + Environment.NewLine +
            $"got:    {Truncate(actual)}");
    }

    /// <summary>Line endings and the rich-edit document's own trailing paragraph mark, removed, so
    /// two spellings of the same content compare equal.</summary>
    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');

    private static string SafeText(AutomationElement input)
    {
        try { return input.AsTextBox().Text ?? ""; }
        catch (Exception ex) { return $"<unreadable: {ex.Message}>"; }
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";

    private static AutomationElement? FirstWritableTextBox(Window window) =>
        window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
            .FirstOrDefault(e =>
            {
                try { return e.Patterns.Value.PatternOrDefault?.IsReadOnly.ValueOrDefault == false; }
                catch (Exception) { return false; }
            });

    /// <summary>
    /// Presses a tool's action: a command-bar button by its <c>ToolCommand.Id</c>, or — for a
    /// form-shaped tool, which has no command bar at all — an ordinary button by its label.
    /// <para>
    /// The fallback is not belt-and-braces, it is the only thing that works for half the catalog.
    /// <c>ToolPage.Commands</c> is read in exactly one place, <c>EditorToolPage.InitializeChrome</c>,
    /// so a <c>FormToolPage</c>'s declared commands are never built into buttons and never bound to
    /// accelerators; UUID, Password and Lorem each put a real <c>Button</c> in the form instead.
    /// Addressing that button by its English label is safe here for the same reason
    /// <see cref="CopyFirstResultRow"/> is: the fixture seeds <c>Language: en</c>.
    /// </para>
    /// </summary>
    private static void Press(Window window, string commandId, string kind, string? buttonLabel = null)
    {
        var button = dockdevSession.Retry(() =>
            window.FindFirstDescendant(cf => cf.ByAutomationId(commandId))
            ?? (buttonLabel is null ? null : FindButtonByName(window, buttonLabel)))
            ?? throw new InvalidOperationException(
                $"'{kind}' has no command '{commandId}'" +
                (buttonLabel is null ? "" : $" and no button labelled '{buttonLabel}'") +
                $". It offers ids: {CommandIds(window)}; buttons: {ButtonNames(window)}");
        dockdevSession.Press(button);
    }

    private static AutomationElement? FindButtonByName(Window window, string name) =>
        window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(e =>
            {
                try { return string.Equals(e.Name, name, StringComparison.Ordinal); }
                catch (Exception) { return false; }
            });

    /// <summary>
    /// Presses the first per-row copy button on a form-shaped tool.
    /// <para>
    /// Found by automation name rather than by id: <c>FormToolPage.ResultRow</c> builds these in a
    /// loop and gives them names, not ids. The name is the English "Copy output" because the
    /// fixture seeds <c>Language: en</c> — the one place in this suite where an assertion leans on
    /// a translated string, and it holds only because the seeded config pins the language.
    /// </para>
    /// </summary>
    private static void CopyFirstResultRow(Window window, string kind)
    {
        var copy = dockdevSession.Retry(() =>
                window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(e =>
                    {
                        try { return e.Name?.StartsWith("Copy output", StringComparison.Ordinal) == true; }
                        catch (Exception) { return false; }
                    }))
            ?? throw new InvalidOperationException(
                $"'{kind}' has no result-row copy button. Buttons: {ButtonNames(window)}");
        dockdevSession.Press(copy);
    }

    // ---- The oracle ---------------------------------------------------------

    /// <summary>
    /// The window still answers. A page whose handler threw somewhere unrecoverable can leave a
    /// window that is on screen and enumerable but no longer has a usable input — which is what a
    /// user means by "it stopped responding", and is invisible to a liveness check on the process.
    /// </summary>
    private static void AssertStillResponsive(Window window, StressCase testCase)
    {
        // A generator has no input of its own; its liveness is that its action is still there.
        if (testCase.Input is null)
        {
            Assert.True(
                dockdevSession.Retry(() =>
                    window.FindFirstDescendant(cf => cf.ByAutomationId(testCase.Action!))
                    ?? (testCase.ActionLabel is null ? null : FindButtonByName(window, testCase.ActionLabel)))
                    is not null,
                $"'{testCase.Kind}' lost its '{testCase.Action}' action after {Cycles} cycles.");
            return;
        }

        var input = dockdevSession.Retry(() =>
            window.FindFirstDescendant(cf => cf.ByAutomationId("ToolEditorInput"))
            ?? FirstWritableTextBox(window));

        Assert.True(input is not null,
            $"'{testCase.Kind}' has no reachable input control after {Cycles} cycles — the page " +
            "is still on screen but no longer usable.");
    }

    /// <summary>
    /// Nothing threw where nothing could catch it. This is the assertion that earns the suite its
    /// keep: every guard the app added to stop a fault killing the process routes through
    /// <c>Diag.Log</c>, so the log is where a swallowed crash now lives.
    /// </summary>
    private void AssertNothingFaulted(StressCase testCase, long logMark)
    {
        var faults = session.Health.FaultsSince(logMark);
        Assert.True(faults.Count == 0,
            $"'{testCase.Kind}' logged {faults.Count} swallowed fault(s) over {Cycles} cycles. " +
            "The app stayed up because App.UnhandledException handles these — a user would see a " +
            "command that silently did nothing." + Environment.NewLine +
            string.Join(Environment.NewLine, faults.Take(10)));
    }

    private void AssertNotLeaking(
        StressCase testCase, ResourceSample growth, int measured, ResourceSample baseline, ResourceSample final)
    {
        var complaints = new List<string>();

        if (growth.GdiObjects > MaxGdiPerCycle * measured)
            complaints.Add($"GDI objects grew by {growth.GdiObjects} over {measured} cycles " +
                           $"({growth.GdiObjects / (double)measured:0.0}/cycle, ceiling {MaxGdiPerCycle})");

        if (growth.UserObjects > MaxUserPerCycle * measured)
            complaints.Add($"USER objects grew by {growth.UserObjects} over {measured} cycles " +
                           $"({growth.UserObjects / (double)measured:0.0}/cycle, ceiling {MaxUserPerCycle})");

        if (growth.Handles > MaxHandlesPerCycle * measured)
            complaints.Add($"handles grew by {growth.Handles} over {measured} cycles " +
                           $"({growth.Handles / (double)measured:0.0}/cycle, ceiling {MaxHandlesPerCycle})");

        if (growth.PrivateBytes > MaxPrivateBytesPerCycle * measured)
            complaints.Add($"private bytes grew by {growth.PrivateBytes / (1024 * 1024)}MB over " +
                           $"{measured} cycles (ceiling {MaxPrivateBytesPerCycle / (1024 * 1024)}MB/cycle)");

        Assert.True(complaints.Count == 0,
            $"'{testCase.Kind}' is holding on to something per cycle: " +
            string.Join("; ", complaints) + "." + Environment.NewLine +
            $"baseline (after {WarmupCycles} warm-up cycles): {baseline}" + Environment.NewLine +
            $"final (after {Cycles}): {final}");
    }

    // ---- Coverage -----------------------------------------------------------

    /// <summary>
    /// Every launchable tool has a soak row. The same guard <see cref="ToolSmokeTests"/> puts on
    /// its own table, for the same reason: the twentieth tool must not be able to arrive untested.
    /// </summary>
    [UIFact]
    public void EveryToolIsSoaked()
    {
        var soaked = All.Select(c => c.Kind).ToHashSet(StringComparer.Ordinal);
        var missing = dockdevSession.ToolKinds.Where(k => !soaked.Contains(k)).Order().ToList();

        Assert.True(missing.Count == 0,
            "These tools have no row in ToolStressTests.All: " + string.Join(", ", missing));
    }

    // ---- Diagnostics --------------------------------------------------------

    private string Log(long mark)
    {
        var text = session.Health.LogSince(mark);
        return text.Length == 0
            ? "(nothing in the log)"
            : "Log since the test started:" + Environment.NewLine +
              (text.Length <= 4000 ? text : text[^4000..]);
    }

    private static string CommandIds(Window window)
    {
        try
        {
            return string.Join(", ", window.FindAllDescendants()
                .Select(e => e.AutomationId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct());
        }
        catch (Exception ex) { return $"<unreadable: {ex.Message}>"; }
    }

    private static string ButtonNames(Window window)
    {
        try
        {
            return string.Join(", ", window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Select(e => e.Name)
                .Where(n => !string.IsNullOrEmpty(n)));
        }
        catch (Exception ex) { return $"<unreadable: {ex.Message}>"; }
    }
}
