using dockdev.Models;
using dockdev.Services.Formats;
using Xunit;

namespace dockdev.Tests.Formats;

public class CsvAndConversionTests
{
    [Fact]
    public void ParseRows_HandlesQuotedFieldsWithEmbeddedCommasAndQuotes()
    {
        var rows = CsvFormat.ParseRows("a,\"b,c\",\"d\"\"e\"\n1,2,3");
        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b,c", "d\"e"], rows[0]);
    }

    [Fact]
    public void WriteField_NeutralizesLeadingFormulaCharacters()
    {
        // CSV/formula injection (design doc §21): a leading =/+/-/@ must be neutralized on export
        // even though dockdev itself never executes anything.
        var node = new ArrayNode([new ObjectNode([("cmd", new ScalarNode("=cmd|' /C calc'!A0", ScalarKind.String))])]);
        var csv = CsvFormat.Instance.FromCanonical(node, FormatOptions.Default);
        Assert.Contains("'=cmd", csv);
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1")]
    [InlineData("-1+1")]
    [InlineData("@SUM(A1)")]
    // The two that a guard checking only the visible characters lets straight through: Excel and
    // LibreOffice both strip a leading tab or carriage return before evaluating what is left.
    [InlineData("\t=cmd|' /C calc'!A0")]
    [InlineData("\r=cmd|' /C calc'!A0")]
    public void EveryFormulaTriggerCharacter_IsNeutralizedOnExport(string dangerous)
    {
        var node = new ArrayNode([new ObjectNode([("cell", new ScalarNode(dangerous, ScalarKind.String))])]);
        var csv = CsvFormat.Instance.FromCanonical(node, FormatOptions.Default);

        // The written cell is the value prefixed with a single quote — the "force text" escape —
        // wherever it appears in the row, quoted or not.
        Assert.Contains("'" + dangerous[0], csv, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryValues_AreNotPrefixed()
    {
        // The guard must not fire on the values people actually export: a negative number is a
        // legitimate leading '-', but it is also genuinely a formula trigger, so the escape is
        // applied and this asserts only that unrelated text is untouched.
        var node = new ArrayNode([new ObjectNode([("name", new ScalarNode("Ada Lovelace", ScalarKind.String))])]);
        var csv = CsvFormat.Instance.FromCanonical(node, FormatOptions.Default);

        Assert.Contains("Ada Lovelace", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("'Ada", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void WrittenRowsUseCrlf_AsRfc4180AndWindowsBothExpect()
    {
        var node = new ArrayNode([new ObjectNode([("a", new ScalarNode("1", ScalarKind.Number))])]);
        var csv = CsvFormat.Instance.FromCanonical(node, FormatOptions.Default);

        Assert.Contains("\r\n", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", csv, StringComparison.Ordinal);

        // And the reader still takes either, so nothing about what dockdev can open changed.
        Assert.Equal(2, CsvFormat.ParseRows(csv).Count);
        Assert.Equal(2, CsvFormat.ParseRows("a\n1").Count);
    }

    [Fact]
    public void ToCanonical_ProducesOneObjectPerDataRow()
    {
        var node = CsvFormat.Instance.ToCanonical("name,age\nAda,30\nGrace,85");
        var array = Assert.IsType<ArrayNode>(node);
        Assert.Equal(2, array.Items.Count);
        var first = Assert.IsType<ObjectNode>(array.Items[0]);
        Assert.Contains(first.Members, m => m.Key == "name" && m.Value is ScalarNode { Raw: "Ada" });
    }

    [Fact]
    public void CsvProjection_FlattenThenRebuild_PreservesNestedShape()
    {
        var original = new ArrayNode(
        [
            new ObjectNode(
            [
                ("id", new ScalarNode("1", ScalarKind.Number)),
                ("address", new ObjectNode([("city", new ScalarNode("Bengaluru", ScalarKind.String))])),
                ("tags", new ArrayNode([new ScalarNode("a", ScalarKind.String), new ScalarNode("b", ScalarKind.String)])),
            ]),
        ]);

        var flat = CsvProjection.Flatten(original);
        var flatRecord = Assert.IsType<ObjectNode>(Assert.IsType<ArrayNode>(flat).Items[0]);
        Assert.Contains(flatRecord.Members, m => m.Key == "address.city");

        var rebuilt = CsvProjection.Rebuild(flat);
        var rebuiltRecord = Assert.IsType<ObjectNode>(Assert.IsType<ArrayNode>(rebuilt).Items[0]);
        var address = Assert.IsType<ObjectNode>(rebuiltRecord.Members.Single(m => m.Key == "address").Value);
        Assert.Equal("Bengaluru", ((ScalarNode)address.Members.Single(m => m.Key == "city").Value).Raw);
    }

    /// <summary>
    /// A CSV header is pasted text with no limit of its own on how many dots it contains — unlike
    /// JSON and XML, whose nesting depth comes from a document structure their own reader already
    /// bounds (JSON's <c>MaxDepth</c>, XML's <c>XmlFormat.GuardDepth</c>). Before
    /// <c>CsvProjection.MaxDepth</c>, a header a few thousand dots deep made <c>Rebuild</c> recurse
    /// once per dot with no cap at all — an uncatchable <see cref="StackOverflowException"/> that
    /// ends the process, exactly the failure class XML's own nesting cap exists to prevent. This is
    /// the CSV-side regression test for it: a header dotted well past the 256-level cap must
    /// convert (as one flat, literal key) rather than crash.
    /// </summary>
    [Fact]
    public void CsvProjection_Rebuild_PathologicallyDottedHeader_DegradesToFlatKey_InsteadOfOverflowingTheStack()
    {
        var deepHeader = string.Join('.', Enumerable.Repeat("a", 5_000));
        var flat = new ArrayNode([new ObjectNode([(deepHeader, new ScalarNode("value", ScalarKind.String))])]);

        var rebuilt = CsvProjection.Rebuild(flat);

        var record = Assert.IsType<ObjectNode>(Assert.IsType<ArrayNode>(rebuilt).Items[0]);
        var member = Assert.Single(record.Members);
        Assert.Equal(deepHeader, member.Key);
        Assert.Equal("value", Assert.IsType<ScalarNode>(member.Value).Raw);
    }

    [Fact]
    public void JsonToXmlToJson_RoundTripsLosslessly()
    {
        const string originalJson = """{"person":{"name":"Ada","tags":["x","y"]}}""";
        var canonical = JsonFormat.Instance.ToCanonical(originalJson);
        var xml = XmlFormat.Instance.FromCanonical(canonical, FormatOptions.Default);

        var backToCanonical = XmlFormat.Instance.ToCanonical(xml);
        var backToJson = JsonFormat.Instance.FromCanonical(backToCanonical, FormatOptions.Default);

        var expected = JsonFormat.Instance.Format(originalJson, FormatOptions.Default).Text;
        var actual = JsonFormat.Instance.Format(backToJson, FormatOptions.Default).Text;
        Assert.Equal(expected, actual);
    }
}
