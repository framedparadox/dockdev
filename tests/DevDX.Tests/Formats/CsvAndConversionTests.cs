using DevDX.Models;
using DevDX.Services.Formats;
using Xunit;

namespace DevDX.Tests.Formats;

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
        // even though DevDX itself never executes anything.
        var node = new ArrayNode([new ObjectNode([("cmd", new ScalarNode("=cmd|' /C calc'!A0", ScalarKind.String))])]);
        var csv = CsvFormat.Instance.FromCanonical(node, FormatOptions.Default);
        Assert.Contains("'=cmd", csv);
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
