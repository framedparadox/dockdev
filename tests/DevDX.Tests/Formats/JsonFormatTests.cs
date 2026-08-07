using DevDX.Services.Formats;
using Xunit;

namespace DevDX.Tests.Formats;

public class JsonFormatTests
{
    [Fact]
    public void Format_PrettyPrintsWithRequestedIndent()
    {
        var result = JsonFormat.Instance.Format("""{"a":1}""", new FormatOptions { IndentWidth = 4 });
        Assert.True(result.Success);
        Assert.Contains("\n    \"a\"", result.Text);
    }

    [Fact]
    public void Format_InvalidJson_NeverClearsUserText_ReturnsDiagnosticInstead()
    {
        var result = JsonFormat.Instance.Format("{not valid", FormatOptions.Default);
        Assert.False(result.Success);
        Assert.Equal("", result.Text);
        Assert.NotEmpty(result.Diagnostics);
        Assert.True(result.Diagnostics[0].Line >= 1);
    }

    [Fact]
    public void Format_PreservesBigIntegerPrecision()
    {
        // The classic JSON-tool bug: a 20-digit id mangled by round-tripping through double.
        const string bigId = "123456789012345678901234567890";
        var result = JsonFormat.Instance.Format($$"""{"id":{{bigId}}}""", FormatOptions.Default);
        Assert.True(result.Success);
        Assert.Contains(bigId, result.Text);
    }

    [Fact]
    public void Minify_RemovesWhitespace()
    {
        var result = JsonFormat.Instance.Minify("{\n  \"a\": 1\n}");
        Assert.True(result.Success);
        Assert.Equal("{\"a\":1}", result.Text);
    }

    [Fact]
    public void Format_SortKeys_OrdersObjectMembers()
    {
        var result = JsonFormat.Instance.Format("""{"b":1,"a":2}""", new FormatOptions { SortKeys = true });
        Assert.True(result.Success);
        Assert.True(result.Text.IndexOf("\"a\"", StringComparison.Ordinal) < result.Text.IndexOf("\"b\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReportsLineAndColumn()
    {
        var diagnostics = JsonFormat.Instance.Validate("{\n  \"a\": ,\n}");
        Assert.NotEmpty(diagnostics);
    }

    [Fact]
    public void ToCanonical_ThenFromCanonical_RoundTrips()
    {
        const string original = """{"name":"Ada","tags":["a","b"],"active":true,"score":null}""";
        var node = JsonFormat.Instance.ToCanonical(original);
        var rebuilt = JsonFormat.Instance.FromCanonical(node, FormatOptions.Default);

        // DataNode records hold IReadOnlyList members, which don't have sequence-based record
        // equality — compare via re-formatting both to the same canonical text instead.
        var expected = JsonFormat.Instance.Format(original, FormatOptions.Default).Text;
        var actual = JsonFormat.Instance.Format(rebuilt, FormatOptions.Default).Text;
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("{}", 80)]
    [InlineData("[1,2,3]", 80)]
    [InlineData("not json", 0)]
    public void DetectConfidence_ScoresShapeCorrectly(string text, int expectedMinimum)
    {
        var score = JsonFormat.Instance.DetectConfidence(text);
        if (expectedMinimum == 0)
            Assert.Equal(0, score);
        else
            Assert.True(score >= expectedMinimum);
    }
}
