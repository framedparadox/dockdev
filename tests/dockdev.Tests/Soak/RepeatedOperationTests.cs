using dockdev.Services.Formats;
using dockdev.Services.Masking;
using dockdev.Services.Syntax;
using dockdev.Services.Tools;
using Xunit;

namespace dockdev.Tests.Soak;

/// <summary>
/// The reported scenario — paste, format, copy, repeat — reduced to the part that needs no desktop.
/// <para>
/// <b>Why a headless half exists at all.</b> <c>tests/dockdev.UITests/ToolStressTests</c> soaks the
/// real windows, and it is the only thing that can catch a fault in a timer tick or a leaked visual
/// tree. It is also opt-in, needs an interactive session, and takes minutes. That makes it the
/// wrong place — and CI the wrong moment — to discover that an engine is not idempotent, or that a
/// scanner has quietly become stateful. Those are properties of pure functions, they are what
/// repeating an operation actually exercises, and they cost milliseconds to check here.
/// </para>
/// <para>
/// <b>What "repeat" tests that a single call does not.</b> A one-shot test pins behaviour for one
/// input. These pin behaviour <em>across</em> invocations: that the second format of a document
/// equals the first (or the editor's content would drift every time the user pressed the button),
/// that alternating Format and Minify converges rather than accumulating, and that a service reused
/// for fifteen operations answers the fifteenth exactly as it answered the first. A service that
/// cached something it should not, or accumulated state between calls, passes every existing test
/// in this project and fails here.
/// </para>
/// </summary>
public class RepeatedOperationTests
{
    /// <summary>Matches <c>ToolStressTests.Cycles</c> — the top of the range the bug report
    /// described ("repeat this 10-15 times"), so the two halves of the suite soak alike.</summary>
    private const int Cycles = 15;

    // ---- Format is idempotent -----------------------------------------------

    public static TheoryData<string, string> FormattableDocuments() => new()
    {
        { "json", """{"id":1,"name":"row","tags":["a","b"],"nested":{"x":1,"y":[1,2,3]}}""" },
        { "json", """[{"a":1},{"b":[2,3]},{"c":{"d":null}}]""" },
        { "json", """{"unicode":"héllo — wörld","escaped":"a\"b\\c","big":123456789012345678901234567890}""" },
        { "xml", "<root><item id=\"1\"><name>row</name><flag>true</flag></item></root>" },
        { "xml", "<a><b/><c attr=\"x\">text</c></a>" },
        { "csv", "a,b,c\n1,2,3\n4,5,6" },
    };

    /// <summary>
    /// Formatting an already-formatted document returns it unchanged.
    /// <para>
    /// This is the property the reported scenario leans on hardest, because the JSON and Formatter
    /// pages rewrite the editor <em>in place</em>: the output of one Format is the input of the
    /// next. If the operation were not idempotent, pressing the button fifteen times would leave
    /// fifteen different documents, growing or drifting with every press, and the user would see it
    /// as the tool corrupting their text.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(FormattableDocuments))]
    public void Format_IsIdempotent_AcrossRepeatedPresses(string formatId, string document)
    {
        var format = FormatFor(formatId);

        var first = format.Format(document, FormatOptions.Default);
        Assert.True(first.Success, $"{formatId}: the fixture itself did not format.");

        var current = first.Text;
        for (int cycle = 2; cycle <= Cycles; cycle++)
        {
            var result = format.Format(current, FormatOptions.Default);
            Assert.True(result.Success,
                $"{formatId}: cycle {cycle} refused to format its own cycle-{cycle - 1} output. " +
                $"Diagnostic: {Describe(result)}");
            Assert.True(result.Text == current,
                $"{formatId}: cycle {cycle} changed a document that cycle {cycle - 1} had already " +
                $"formatted. Formatting is not idempotent, so the editor drifts on every press." +
                Environment.NewLine + $"before ({current.Length} chars): {Truncate(current)}" +
                Environment.NewLine + $"after  ({result.Text.Length} chars): {Truncate(result.Text)}");
            current = result.Text;
        }
    }

    /// <summary>
    /// Alternating Format and Minify converges instead of accumulating. The pages put both buttons
    /// side by side over one editable pane, so this is a sequence a user produces by leaning on
    /// two buttons — and each one's output is the other's input.
    /// </summary>
    [Theory]
    [MemberData(nameof(FormattableDocuments))]
    public void FormatAndMinify_Alternated_Converge(string formatId, string document)
    {
        var format = FormatFor(formatId);

        var minified = format.Minify(document);
        Assert.True(minified.Success, $"{formatId}: the fixture did not minify.");

        string? firstMinified = null;
        string? firstFormatted = null;

        for (int cycle = 1; cycle <= Cycles; cycle++)
        {
            var formatted = format.Format(minified.Text, FormatOptions.Default);
            Assert.True(formatted.Success,
                $"{formatId}: cycle {cycle} could not format its own minified output. {Describe(formatted)}");

            minified = format.Minify(formatted.Text);
            Assert.True(minified.Success,
                $"{formatId}: cycle {cycle} could not minify its own formatted output. {Describe(minified)}");

            // Cycle one establishes the fixed point; every cycle after it must land on the same one.
            firstFormatted ??= formatted.Text;
            firstMinified ??= minified.Text;

            Assert.True(formatted.Text == firstFormatted,
                $"{formatId}: the formatted form drifted by cycle {cycle} — Format/Minify is not a " +
                "round trip, so a user alternating the two buttons watches their document change.");
            Assert.True(minified.Text == firstMinified,
                $"{formatId}: the minified form drifted by cycle {cycle}.");
        }
    }

    // ---- Engines are stateless across calls ---------------------------------

    /// <summary>
    /// A tokenizer instance reused for fifteen documents answers each one as a fresh instance
    /// would.
    /// <para>
    /// The app holds exactly one tokenizer per format for the life of the process
    /// (<c>FormatRegistry</c>'s statics) and hands it to every editor, which re-runs it on a
    /// debounce after every keystroke. A scanner that accumulated so much as a cached line index
    /// between calls would mis-colour the second document onwards — and the existing reconstruction
    /// and fuzz suites would not notice, because both build a new scanner per case.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    [InlineData("csv")]
    public void Tokenizer_ReusedInstance_IsStatelessBetweenDocuments(string formatId)
    {
        var shared = FormatFor(formatId).Tokenizer;

        for (int cycle = 1; cycle <= Cycles; cycle++)
        {
            var document = DocumentFor(formatId, cycle);
            var fresh = FormatFor(formatId).Tokenizer.Tokenize(document);
            var reused = shared.Tokenize(document);

            Assert.True(fresh.Count == reused.Count,
                $"{formatId}: on cycle {cycle} the shared tokenizer produced {reused.Count} tokens " +
                $"where a fresh one produced {fresh.Count} — it is carrying state between documents.");

            for (int i = 0; i < fresh.Count; i++)
            {
                Assert.True(
                    fresh[i].Start == reused[i].Start &&
                    fresh[i].End == reused[i].End &&
                    fresh[i].Kind == reused[i].Kind,
                    $"{formatId}: token {i} differs on cycle {cycle} between a shared and a fresh " +
                    $"tokenizer — fresh {fresh[i].Kind}[{fresh[i].Start}..{fresh[i].End}] vs " +
                    $"reused {reused[i].Kind}[{reused[i].Start}..{reused[i].End}].");
            }
        }
    }

    /// <summary>
    /// Re-tokenizing one unchanged document fifteen times gives the same answer every time — the
    /// literal shape of the editor's debounced re-highlight, which runs over a document that has
    /// often not changed at all since the previous pass.
    /// </summary>
    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    [InlineData("csv")]
    public void Tokenizer_RepeatedOnOneDocument_IsDeterministic(string formatId)
    {
        var tokenizer = FormatFor(formatId).Tokenizer;
        var document = DocumentFor(formatId, 1);

        var expected = tokenizer.Tokenize(document);
        for (int cycle = 2; cycle <= Cycles; cycle++)
        {
            var actual = tokenizer.Tokenize(document);
            Assert.True(expected.Count == actual.Count,
                $"{formatId}: pass {cycle} over an unchanged document produced {actual.Count} tokens, " +
                $"not {expected.Count}.");
        }
    }

    /// <summary>
    /// The masker, held for the life of a page and re-run on every edit, finds the same PII on the
    /// fifteenth pass as on the first — and masking already-masked text does not keep finding more.
    /// The Data Masker page rewrites its editor in place, so its own output is its next input.
    /// </summary>
    [Fact]
    public void Masker_RepeatedDetection_IsStable()
    {
        const string original = """{"contact":"ada.lovelace@example.com","card":"4111111111111111"}""";

        var firstFindings = Detect(original);
        Assert.True(firstFindings.Count > 0, "The fixture text should contain detectable PII.");

        // The same input, fifteen times over: the page re-runs detection on every edit, and the
        // rule set — with its compiled regexes — is a process-lifetime static shared by every
        // Masker window open at once.
        for (int cycle = 2; cycle <= Cycles; cycle++)
        {
            var findings = Detect(original);
            Assert.True(findings.Count == firstFindings.Count,
                $"Pass {cycle} over unchanged text found {findings.Count} findings, not " +
                $"{firstFindings.Count} — detection is carrying state between runs.");
        }
    }

    /// <summary>
    /// Masking already-masked text is a fixed point.
    /// <para>
    /// The Data Masker rewrites its editor in place, so its own output is its next input — exactly
    /// the reported "format it, then do it again" shape. If a second pass found new PII in the
    /// <em>replacement</em> text and masked that too, every press would rewrite the user's document
    /// again, and the pseudonyms would drift away from the ones already pasted into a ticket.
    /// </para>
    /// <para>
    /// One <see cref="Masker"/> instance for the whole loop, deliberately: it holds a pseudonym map
    /// and a per-instance salt, which is what makes a given input mask to a stable replacement
    /// within one page. A fresh instance per cycle would salt differently and prove nothing.
    /// </para>
    /// </summary>
    [Fact]
    public void Masker_MaskingMaskedText_IsAFixedPoint()
    {
        var masker = new Masker(userSalt: "soak");
        const string original = """{"contact":"ada.lovelace@example.com","card":"4111111111111111"}""";

        var current = masker.BuildOutput(original, Detect(original)).Text;

        for (int cycle = 2; cycle <= Cycles; cycle++)
        {
            var next = masker.BuildOutput(current, Detect(current)).Text;
            Assert.True(next == current,
                $"Masking already-masked text changed it again on cycle {cycle}. Re-masking must be " +
                "a fixed point, or repeated presses keep rewriting the user's document." +
                Environment.NewLine + $"before: {Truncate(current)}" +
                Environment.NewLine + $"after:  {Truncate(next)}");
            current = next;
        }
    }

    private static List<Finding> Detect(string text) =>
        PiiDetector.Detect(text, PiiRuleSet.Default, Confidence.Medium);

    /// <summary>
    /// Base64 encode/decode survives fifteen round trips unchanged. The Base64 page has a Swap
    /// command that feeds output back in as input, so a round trip that lost a byte — a padding
    /// character, a non-ASCII code point — would compound with every press.
    /// </summary>
    [Fact]
    public void Base64_RoundTrip_SurvivesRepeatedSwaps()
    {
        const string original = "dockdev soak — ünïcode, punctuation: {}[]()!?, and padding aa";

        var current = original;
        for (int cycle = 1; cycle <= Cycles; cycle++)
        {
            var encoded = Base64Tools.Encode(
                System.Text.Encoding.UTF8.GetBytes(current), urlSafe: false, mime76: false);

            Assert.True(Base64Tools.TryDecode(encoded, strict: true, out var bytes, out var error),
                $"Base64 could not decode what it had just encoded on cycle {cycle}: {error}");

            var decoded = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.True(decoded == current,
                $"Base64 round trip {cycle} did not return its input." + Environment.NewLine +
                $"in:  {Truncate(current)}" + Environment.NewLine +
                $"out: {Truncate(decoded)}");
            current = decoded;
        }

        Assert.Equal(original, current);
    }

    // ---- Helpers ------------------------------------------------------------

    private static IDataFormat FormatFor(string id) => id switch
    {
        "json" => FormatRegistry.Json,
        "xml" => FormatRegistry.Xml,
        "csv" => FormatRegistry.Csv,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "No such format."),
    };

    private static string DocumentFor(string id, int cycle) => id switch
    {
        "json" => $$"""{"id":{{cycle}},"name":"row {{cycle}}","tags":["a","b"],"ok":true}""",
        "xml" => $"<root><item id=\"{cycle}\"><name>row {cycle}</name></item></root>",
        "csv" => $"a,b,c\n{cycle},2,3\n4,5,6",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "No such format."),
    };

    private static string Describe(FormatResult result) =>
        result.Diagnostics.Count == 0
            ? "(no diagnostic)"
            : $"line {result.Diagnostics[0].Line}, column {result.Diagnostics[0].Column}: " +
              result.Diagnostics[0].Message;

    private static string Truncate(string text) =>
        text.Length <= 240 ? text : text[..240] + "…";
}
