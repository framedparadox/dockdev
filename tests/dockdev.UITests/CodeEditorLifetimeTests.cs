using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using Xunit;
using Xunit.Abstractions;

namespace dockdev.UITests;

/// <summary>
/// Regression cover for the one fault this suite has actually caught in the wild: a
/// <see cref="Microsoft.UI.Xaml.Controls.RichEditBox"/> document being coloured from a timer tick
/// after the window that owns it has gone.
/// <para>
/// <b>The observed crash.</b> A soak run over the editor-shaped tools ended with dockdev gone from
/// the desktop and <em>nothing</em> in <c>%TEMP%\dockdev.log</c>. The Windows Application log had
/// it, from the .NET Runtime rather than from the app:
/// </para>
/// <code>
/// Description: The process was terminated due to an unhandled exception.
/// Stack:
///    at ABI.Microsoft.UI.Text.ITextCharacterFormat.set_ForegroundColor(Windows.UI.Color)
///    at dockdev.Controls.CodeEditor.Highlight()
///    at dockdev.Controls.CodeEditor..ctor_b__24_0(DispatcherQueueTimer, object)
///    at WinRT...Do_Abi_Invoke(IntPtr, IntPtr, IntPtr)
///    at Microsoft.UI.Xaml.Application.Start(ApplicationInitializationCallback)
///    at dockdev.Program.Main(string[])
/// </code>
/// <para>
/// <b>Why the app's own guards did not stop it.</b> Both <c>ForegroundColor</c> assignments in
/// <c>Highlight</c> sit inside a <c>try { … } catch (Exception)</c>, and <c>App.UnhandledException</c>
/// sets <c>Handled = true</c>. Neither ran. That is the signature of an access violation in the
/// native rich-edit document: .NET does not deliver an <c>AccessViolationException</c> to a
/// <c>catch (Exception)</c>, it fails the process — so this is the one fault class that still ends
/// dockdev outright, and the only place it is visible is the Windows event log.
/// </para>
/// <para>
/// <b>What is established, and what is not.</b> Established: the process was terminated at that
/// call, on that timer, during a repeated edit/transform/copy soak of the editor-shaped tools; and
/// neither guard fired. <em>Not</em> established: which of the possible triggers it was. The
/// obvious candidate is a document destroyed by a window closing with a colouring pass still queued
/// against it — <c>CodeEditor</c> stops the debounce from <c>Unloaded</c>, and a WinUI window
/// closing does not dependably raise <c>Unloaded</c> on its content. But the crash is at
/// <c>set_ForegroundColor</c>, and the <c>GetText</c> and <c>GetRange</c> calls that precede it on
/// the same document had already succeeded, which is not what a fully torn-down document looks
/// like. An out-of-range or stale range is as good a candidate on the evidence available.
/// </para>
/// <para>
/// So this test probes the closing-window trigger specifically, because it is the cheapest to
/// drive and the most likely: it repeatedly closes a window inside the 180 ms debounce and asserts
/// the process survives. Passing does <b>not</b> clear <c>Highlight</c> — it narrows the search.
/// If it ever fails, it has caught the real thing, and the message says where to look.
/// </para>
/// </summary>
[Collection(dockdevCollection.Name)]
public class CodeEditorLifetimeTests(dockdevSession session, ITestOutputHelper output)
{
    /// <summary>
    /// Open/type/close rounds. The race is timing-dependent, so one round proves nothing; this is
    /// enough attempts to make a real window of vulnerability show up, and short enough to stay a
    /// test rather than an endurance run. Override with <c>DOCKDEV_LIFETIME_ROUNDS</c>.
    /// </summary>
    private static int Rounds =>
        int.TryParse(Environment.GetEnvironmentVariable("DOCKDEV_LIFETIME_ROUNDS"), out var n) && n > 0 ? n : 12;

    /// <summary>Every editor-shaped tool builds its input from <c>CodeEditor</c>, but they differ in
    /// which tokenizer they arm, and the crash was in the colouring pass — so this covers the three
    /// that colour with a real tokenizer from the first keystroke.</summary>
    public static TheoryData<string> EditorTools() => ["Json", "Xml", "DataFormatter"];

    [UITheory]
    [MemberData(nameof(EditorTools))]
    public void ClosingAWindowWithAHighlightPending_DoesNotKillTheApp(string kind)
    {
        var logMark = session.Health.LogMark();

        for (int round = 1; round <= Rounds; round++)
        {
            var window = session.OpenTool(kind);

            var editor = dockdevSession.Retry(() =>
                window.FindFirstDescendant(cf => cf.ByAutomationId("ToolEditorInput")))
                ?? throw new InvalidOperationException($"'{kind}' has no editor input.");

            editor.Focus();

            // Short enough to type quickly, long enough to give the tokenizer real work — the
            // crash was inside the per-token colouring loop, so an empty document would not
            // reach it.
            Keyboard.Type($"{{\"round\":{round},\"a\":[1,2,3],\"b\":\"text\"}}");

            // No settle here, deliberately. CodeEditor.HighlightDelay is 180 ms from the last
            // keystroke; closing now is what puts the close and the queued colouring pass in the
            // same 180 ms, which is the whole point of the test. Waiting would let the highlight
            // complete and turn this into an ordinary close.
            dockdevSession.CloseWindow(window);
            DismissDiscardPrompt(window);

            Assert.False(session.Health.HasExited,
                $"dockdev was terminated on round {round} of {Rounds} closing a '{kind}' window " +
                "with a highlight pending. Check the Windows Application log for a .NET Runtime " +
                "event id 1026 — an access violation in the rich-edit document does not reach " +
                "App.UnhandledException and leaves nothing in dockdev.log. " + Diagnostics(logMark));

            // Settle before the next round so the window count is stable when OpenTool takes its
            // before-snapshot; otherwise it can mistake this round's closing window for the next
            // round's new one.
            dockdevSession.RetryUntil(
                () => !session.TopLevelWindows().Any(w => SameWindow(w, window)),
                TimeSpan.FromSeconds(5));
        }

        output.WriteLine($"{kind}: survived {Rounds} close-with-pending-highlight rounds.");

        var faults = session.Health.FaultsSince(logMark);
        Assert.True(faults.Count == 0,
            $"'{kind}' logged {faults.Count} swallowed fault(s) while closing with highlights " +
            "pending:" + Environment.NewLine + string.Join(Environment.NewLine, faults.Take(10)));
    }

    /// <summary>
    /// Answers the discard confirmation, which every one of these windows gets because typing made
    /// it dirty. Pressed as soon as it appears rather than after a settle: the sooner the window
    /// actually goes, the closer the close lands to the pending tick.
    /// </summary>
    private static void DismissDiscardPrompt(Window window)
    {
        var discard = dockdevSession.Retry(
            () => window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button)
                .And(cf.ByName("Discard"))),
            TimeSpan.FromSeconds(5));

        if (discard is not null)
            dockdevSession.Press(discard);
    }

    private static bool SameWindow(Window candidate, Window original)
    {
        try { return candidate.Properties.NativeWindowHandle.Value == original.Properties.NativeWindowHandle.Value; }
        catch (Exception) { return false; }
    }

    private string Diagnostics(long logMark)
    {
        var text = session.Health.LogSince(logMark);
        return text.Length == 0
            ? "The app log has nothing since this test began, which is itself consistent with a " +
              "process-terminating access violation."
            : "App log since the test began:" + Environment.NewLine +
              (text.Length <= 3000 ? text : text[^3000..]);
    }
}
