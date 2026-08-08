using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace dockdev.UITests;

/// <summary>
/// Design doc §24's table-driven smoke test: one row per tool, driven through the real UI.
/// <para>
/// Table-driven rather than nineteen hand-written cases, and the reason is maintenance, not
/// brevity. A hand-written test per tool rots — someone adds the twentieth tool and nobody
/// remembers there was a file to add it to. A row is obviously missing when the row list and the
/// catalog disagree, and <see cref="EveryCatalogToolHasARow"/> makes that a failure rather than a
/// thing to notice.
/// </para>
/// </summary>
[Collection(dockdevCollection.Name)]
public class ToolSmokeTests(dockdevSession session)
{
    // ---- Every tool opens ---------------------------------------------------

    public static TheoryData<string> AllTools()
    {
        var data = new TheoryData<string>();
        foreach (var kind in dockdevSession.ToolKinds)
            data.Add(kind);
        return data;
    }

    /// <summary>
    /// The cheapest test with the highest yield: does clicking this icon produce a working window?
    /// A tool page is built imperatively in its constructor, so a null resource key, a missing
    /// glyph or a bad cast lands as an exception during construction — and this is what catches it.
    /// </summary>
    [UITheory]
    [MemberData(nameof(AllTools))]
    public void OpensFromTheDockAndCloses(string kind)
    {
        var window = session.OpenTool(kind);
        try
        {
            Assert.True(dockdevSession.RetryUntil(() => window.FindAllDescendants().Length > 3),
                $"'{kind}' opened a window with almost nothing in it — its page probably failed to build.");
        }
        finally
        {
            session.CloseAndWait(window);
        }
    }

    /// <summary>
    /// The row list and the shipped catalog have to agree, or a tool added later is silently
    /// untested. Read from the app's own string table, which carries one <c>Tool.&lt;X&gt;.Name</c>
    /// per catalog entry — no reference to dockdev needed, and it fails the day someone adds a tool
    /// without adding a row.
    /// </summary>
    [UIFact]
    public void EveryCatalogToolHasARow()
    {
        var stringTable = Path.Combine(RepositoryRoot(), "src", "dockdev", "Strings", "en.json");
        var keys = System.Text.Json.JsonDocument.Parse(File.ReadAllText(stringTable))
            .RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Where(k => k.StartsWith("Tool.", StringComparison.Ordinal) && k.EndsWith(".Name", StringComparison.Ordinal))
            .Select(k => k["Tool.".Length..^".Name".Length])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The catalog's key names and the ToolKind names differ in a few places — the table below
        // maps the ones that do, so the comparison is about coverage rather than spelling.
        var covered = dockdevSession.ToolKinds.Select(NameKeyFor).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = keys.Where(k => !covered.Contains(k)).Order().ToList();
        Assert.True(missing.Count == 0,
            "These catalog tools have no row in dockdevSession.ToolKinds: " + string.Join(", ", missing));
    }

    private static string NameKeyFor(string kind) => kind switch
    {
        "DataFormatter" => "Formatter",
        "DataConverter" => "Converter",
        "DataMasker" => "Masker",
        "TextDiff" => "Diff",
        "RegexTester" => "Regex",
        "UrlEncoding" => "Url",
        _ => kind,
    };

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "dockdev")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    // ---- Editor-shaped tools actually transform their input -----------------

    /// <summary>One row: paste this, press that, expect the result to contain this.</summary>
    public sealed record TransformCase(string Kind, string Input, string CommandId, string ExpectedFragment)
    {
        public override string ToString() => $"{Kind}/{CommandId}";
    }

    public static TheoryData<TransformCase> Transforms() =>
    [
        // Minified in, indented out: the newline is the whole point of Format, so asserting on a
        // fragment that only appears once it has run beats asserting the text merely changed.
        new("Json", """{"a":1,"b":[2,3]}""", "format", "\"a\": 1"),
        new("Xml", "<r><a>1</a></r>", "format", "  <a>1</a>"),
        new("DataFormatter", """{"a":1}""", "format", "\"a\": 1"),
        // Convert defaults to JSON -> XML.
        new("DataConverter", """{"a":1}""", "convert", "<a>"),
    ];

    /// <summary>
    /// Types a fixture into the editor, invokes the tool's primary command by its stable id, and
    /// looks for the result. Covers the path that matters — input reaches the engine and the
    /// engine's output reaches the screen — end to end, through the same controls a user touches.
    /// </summary>
    [UITheory]
    [MemberData(nameof(Transforms))]
    public void TransformsTheInput(TransformCase testCase)
    {
        var window = session.OpenTool(testCase.Kind);
        try
        {
            var editor = dockdevSession.Retry(() => window.FindFirstDescendant(
                cf => cf.ByAutomationId("ToolEditorInput")))
                ?? throw new InvalidOperationException($"'{testCase.Kind}' has no editor input.");

            editor.Focus();
            editor.AsTextBox().Text = testCase.Input;

            var command = dockdevSession.Retry(() => window.FindFirstDescendant(
                cf => cf.ByAutomationId(testCase.CommandId)))
                ?? throw new InvalidOperationException(
                    $"'{testCase.Kind}' has no command '{testCase.CommandId}'.");
            dockdevSession.Press(command);

            Assert.True(
                dockdevSession.RetryUntil(() => VisibleText(window).Contains(testCase.ExpectedFragment, StringComparison.Ordinal)),
                $"'{testCase.Kind}' never showed \"{testCase.ExpectedFragment}\" after '{testCase.CommandId}'. " +
                $"Window text was: {Truncate(VisibleText(window))}");
        }
        finally
        {
            CloseDiscardingChanges(session, window);
        }
    }

    /// <summary>
    /// Everything the window is currently showing, concatenated. Coarse on purpose: a tool's result
    /// lands in the editor for the single-pane tools and in a read-only view for the two-pane ones,
    /// and a test that had to know which would be asserting the layout rather than the behaviour.
    /// </summary>
    private static string VisibleText(Window window)
    {
        var parts = window.FindAllDescendants()
            .Where(e => e.ControlType is ControlType.Text or ControlType.Edit or ControlType.Document)
            .Select(e =>
            {
                try { return e.AsTextBox().Text ?? e.Name; }
                catch { return e.Name; }
            });
        return string.Join("\n", parts);
    }

    private static string Truncate(string text) =>
        text.Length <= 400 ? text : text[..400] + "…";

    /// <summary>
    /// Closes a tool window that has unsaved content, answering the discard prompt if it appears.
    /// Every editor-shaped tool is dirty once something has been typed into it, so a test that just
    /// called Close would leave a modal dialog on screen for the next test to trip over.
    /// </summary>
    internal static void CloseDiscardingChanges(dockdevSession session, Window window)
    {
        session.CloseAndWait(window);

        var discard = dockdevSession.Retry(
            () => window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button)
                    .And(cf.ByName("Discard"))),
            TimeSpan.FromSeconds(3));

        if (discard is not null)
            dockdevSession.Press(discard);

        // Wait for it to be gone: the next test enumerates windows to spot the one it opens, and a
        // window still closing is a window it can mistake for that one.
        session.CloseAndWait(window);
    }
}
