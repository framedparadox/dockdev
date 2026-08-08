namespace dockdev.Controls;

/// <summary>
/// The <c>AutomationId</c>s the code-built controls carry, as constants.
/// <para>
/// These are a contract with <c>tests/dockdev.UITests</c> (design doc §24), not decoration. A UI test
/// has to address a control by <em>something</em>, and the alternatives are worse: an automation
/// <em>name</em> is a translated string, so a test keyed on one passes in English and fails in
/// German; a position in the visual tree changes the first time anyone reorders a panel. An id is
/// the only handle that says "this control, whatever it is called and wherever it sits".
/// </para>
/// <para>
/// XAML-declared controls set theirs inline (see <c>SettingsWindow.xaml</c>'s
/// <c>SettingsNav*</c>/<c>Settings*Panel</c> ids). This class is for the ones built in code, where
/// a literal sprinkled at the construction site would drift from the test that reads it without
/// anything failing to compile.
/// </para>
/// </summary>
public static class AutomationIds
{
    /// <summary>The editable pane of an editor-shaped tool (<see cref="CodeEditor"/>).</summary>
    public const string EditorInput = "ToolEditorInput";

    /// <summary>The read-only result pane (<see cref="CodeView"/>), where a tool has one.</summary>
    public const string EditorOutput = "ToolEditorOutput";

    /// <summary>The status bar's validation chip — "Valid", or the line and column of the fault.</summary>
    public const string StatusChip = "ToolStatusChip";

    /// <summary>The status bar's character/byte counts.</summary>
    public const string StatusCounts = "ToolStatusCounts";

    /// <summary>The dock strip itself. Its items carry <see cref="DockItem"/>.</summary>
    public const string DockStrip = "DockStrip";

    /// <summary>One launchable icon on the dock, suffixed with the tool's <c>ToolKind</c> —
    /// <c>DockItem.Json</c>, <c>DockItem.Base64</c>. Built by <see cref="DockItem"/>.</summary>
    public static string DockItemFor(Models.ToolKind kind) => "DockItem." + kind;

    /// <summary>Prefix of <see cref="DockItemFor"/>, for tests that enumerate the strip.</summary>
    public const string DockItem = "DockItem";
}
