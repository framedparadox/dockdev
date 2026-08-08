using dockdev.Services.Syntax;
using Xunit;

namespace dockdev.Tests.Syntax;

/// <summary>
/// The property every tokenizer must satisfy (design doc §10.1): concatenating every token's span
/// in order, plus the gaps between them, must reconstruct the input byte for byte. This is the
/// one test that catches the whole class of off-by-one bugs that make a highlighter drop or
/// duplicate characters.
/// </summary>
public class TokenizerReconstructionTests
{
    private static void AssertReconstructs(ITokenizer tokenizer, string text)
    {
        var tokens = tokenizer.Tokenize(text);
        int cursor = 0;
        foreach (var token in tokens.OrderBy(t => t.Start))
        {
            Assert.True(token.Start >= cursor, $"Token at {token.Start} overlaps previous cursor {cursor}");
            cursor = Math.Max(cursor, token.End);
        }
        Assert.True(cursor <= text.Length);

        // Reconstruct: gaps (untokenized) + token spans must equal the original text exactly.
        var rebuilt = new System.Text.StringBuilder();
        int pos = 0;
        foreach (var token in tokens.OrderBy(t => t.Start))
        {
            rebuilt.Append(text, pos, token.Start - pos);
            rebuilt.Append(text, token.Start, token.Length);
            pos = token.End;
        }
        rebuilt.Append(text, pos, text.Length - pos);
        Assert.Equal(text, rebuilt.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"a":1,"b":[1,2,3],"c":{"d":"e\"f"},"g":null,"h":true,"i":-1.5e10}""")]
    [InlineData("not json at all {{{")]
    [InlineData("""{"unterminated": "string)""")]
    public void JsonTokenizer_Reconstructs(string text) => AssertReconstructs(JsonTokenizer.Instance, text);

    [Theory]
    [InlineData("")]
    [InlineData("<a/>")]
    [InlineData("<root attr=\"1\"><child>text &amp; more</child><!-- comment --></root>")]
    [InlineData("<a><![CDATA[<raw>]]></a>")]
    [InlineData("<broken attr=>")]
    public void XmlTokenizer_Reconstructs(string text) => AssertReconstructs(XmlTokenizer.Instance, text);

    [Theory]
    [InlineData("")]
    [InlineData("a,b,c\n1,2,3\n")]
    [InlineData("\"quoted, value\",plain,\"with \"\"escaped\"\" quotes\"\n1,2,3")]
    public void CsvTokenizer_Reconstructs(string text) => AssertReconstructs(CsvTokenizer.Instance, text);

    [Theory]
    [InlineData("")]
    [InlineData("plain text with no structure")]
    public void PlainTokenizer_Reconstructs(string text) => AssertReconstructs(PlainTokenizer.Instance, text);
}
