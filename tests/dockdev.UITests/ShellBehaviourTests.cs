using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace dockdev.UITests;

/// <summary>
/// The two behaviours §24 names alongside the per-tool table, because neither belongs to any one
/// tool: the dirty-close confirmation, and the theme switch reaching an already-open tool window.
/// Both are cross-cutting, both are the kind of thing that silently stops working, and neither can
/// be tested below the UI — a confirmation dialog and a repaint are only observable on screen.
/// </summary>
[Collection(dockdevCollection.Name)]
public class ShellBehaviourTests(dockdevSession session)
{
    /// <summary>
    /// A scratch window with unsaved content asks before discarding it, and Cancel means the window
    /// and its content survive. This is the guard on losing work, so the test asserts the cancel
    /// path — that the window is still there afterwards — rather than only that a dialog appeared.
    /// </summary>
    [UIFact]
    public void DirtyWindow_AsksBeforeClosing_AndCancelKeepsIt()
    {
        var window = session.OpenTool("Json");
        try
        {
            var editor = dockdevSession.Retry(() => window.FindFirstDescendant(
                cf => cf.ByAutomationId("ToolEditorInput")))
                ?? throw new InvalidOperationException("The JSON tool has no editor input.");

            editor.Focus();
            editor.AsTextBox().Text = """{"unsaved":true}""";

            dockdevSession.CloseWindow(window);

            var cancel = dockdevSession.Retry(() => window.FindFirstDescendant(
                cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Cancel"))));
            Assert.True(cancel is not null, "Closing a dirty tool window showed no confirmation.");

            dockdevSession.Press(cancel!);

            // Cancel kept it: the window is still there, and still has the text in it.
            Assert.True(dockdevSession.RetryUntil(() => !window.IsOffscreen),
                "Cancelling the discard prompt closed the window anyway.");
        }
        finally
        {
            ToolSmokeTests.CloseDiscardingChanges(session, window);
        }
    }

    /// <summary>
    /// A window opened before the theme changed has to change with it. Tool windows are built
    /// imperatively and several of their brushes are resolved once at construction, which is
    /// exactly the shape of bug that leaves yesterday's theme on yesterday's window — so this
    /// asserts the pixels, by sampling the window's own rendering before and after.
    /// </summary>
    [UIFact]
    public void ThemeSwitch_RepaintsAnAlreadyOpenToolWindow()
    {
        var window = session.OpenTool("Json");
        Window? settings = null;
        try
        {
            var before = Sample(window);

            settings = OpenSettings();
            SelectSettingsPage(settings, "SettingsNavAppearance");
            ChooseTheme(settings, "Light");

            Assert.True(
                dockdevSession.RetryUntil(() => !Sample(window).SequenceEqual(before)),
                "The open tool window did not repaint when the theme changed.");
        }
        finally
        {
            // This is the one test that changes app-wide state, so it is also the one that has to
            // hand the app back exactly as it found it — theme restored, and both windows actually
            // gone rather than merely asked to go.
            if (settings is not null)
            {
                try
                {
                    ChooseTheme(settings, "Dark");
                    session.CloseAndWait(settings);
                }
                catch { /* the assertion above is what matters */ }
            }
            session.CloseAndWait(window);
        }
    }

    // ---- Driving the settings window ---------------------------------------

    private Window OpenSettings() =>
        session.WaitForNewWindow(
            () =>
            {
                var button = dockdevSession.Retry(() => session.Dock.FindFirstDescendant(
                    cf => cf.ByAutomationId("dockdevSettingsButton")))
                    ?? throw new InvalidOperationException("No settings button on the dock.");
                dockdevSession.Press(button);
            },
            "opening settings");

    private static void SelectSettingsPage(Window settings, string navAutomationId)
    {
        var item = dockdevSession.Retry(() => settings.FindFirstDescendant(cf => cf.ByAutomationId(navAutomationId)))
            ?? throw new InvalidOperationException($"No settings nav item '{navAutomationId}'.");
        item.Click();
    }

    /// <summary>
    /// Picks a theme from the Appearance page's combo box. Found by automation id — WinUI gives a
    /// XAML element its <c>x:Name</c> as its automation id — and the item picked by its English
    /// label, which the fixture's pinned language makes stable.
    /// <para>
    /// The retry is doing real work, not just guarding against slowness: the Appearance panel is
    /// collapsed until its nav item is selected, and a collapsed panel's contents are not in the
    /// automation tree at all. Waiting here is what lets the nav click land first.
    /// </para>
    /// </summary>
    private static void ChooseTheme(Window settings, string themeName)
    {
        var combo = dockdevSession.Retry(() => settings.FindFirstDescendant(
            cf => cf.ByAutomationId("ThemeChoice")))
            ?? throw new InvalidOperationException(
                $"No theme combo box on the Appearance page (wanted '{themeName}').");

        if (combo.AsComboBox().Select(themeName) is null)
            throw new InvalidOperationException($"The theme combo box has no '{themeName}' item.");
    }

    /// <summary>
    /// A coarse fingerprint of how the window currently looks. Comparing whole bitmaps would make
    /// this fail on a cursor blink; a handful of bytes from a capture is enough to answer the only
    /// question being asked — did anything about the rendering change at all?
    /// </summary>
    private static byte[] Sample(Window window)
    {
        using var capture = window.Capture();
        using var stream = new MemoryStream();
        capture.Save(stream, System.Drawing.Imaging.ImageFormat.Bmp);
        var bytes = stream.ToArray();

        // Every 997th byte: a prime stride, so the sample does not accidentally land on one
        // repeating column of a flat background and report "unchanged" for a window that changed.
        return Enumerable.Range(0, bytes.Length / 997).Select(i => bytes[i * 997]).ToArray();
    }
}
