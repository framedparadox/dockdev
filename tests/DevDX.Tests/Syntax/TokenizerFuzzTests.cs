using System.Text;
using DevDX.Services.Syntax;
using Xunit;

namespace DevDX.Tests.Syntax;

/// <summary>
/// The reconstruction property (§10.1) proved against generated input rather than a hand-written
/// corpus. This matters more than it used to: <c>CodeEditor</c> now feeds token offsets straight
/// into a <c>RichEditBox</c> document range on every pause in typing, so the tokenizers run against
/// <b>half-typed</b> documents constantly — an unterminated string, a tag with no closing bracket,
/// an attribute stopped mid-word — and a token that runs past the end of the text or overlaps its
/// neighbour is no longer a wrong colour but a bad range applied to a live document.
/// <para>
/// Every prefix of a valid document is exactly what the user types on the way to writing it, so
/// prefixes are the generator. Mutations cover the rest: pasted text that was never valid.
/// </para>
/// </summary>
public class TokenizerFuzzTests
{
    private const string Json =
        """{"name":"a\"b","n":-1.5e10,"ok":true,"nil":null,"list":[1,2,{"deep":"x"}],"empty":{}}""";

    private const string Xml =
        """<?xml version="1.0"?><root a="1" b='2'><child>text &amp; more</child><!-- c --><![CDATA[<raw>]]><self/></root>""";

    private const string Csv =
        "id,name,note\n1,\"quoted, value\",\"with \"\"escaped\"\" quotes\"\n2,plain,\n";

    public static TheoryData<string, string> Corpus() => new()
    {
        { "json", Json },
        { "xml", Xml },
        { "csv", Csv },
    };

    private static ITokenizer For(string name) => name switch
    {
        "json" => JsonTokenizer.Instance,
        "xml" => XmlTokenizer.Instance,
        "csv" => CsvTokenizer.Instance,
        _ => PlainTokenizer.Instance,
    };

    /// <summary>Every prefix of a valid document — i.e. every intermediate state of typing it.</summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void EveryPrefixTokenizesCleanly(string name, string document)
    {
        var tokenizer = For(name);
        for (int length = 0; length <= document.Length; length++)
            AssertWellFormed(tokenizer, document[..length]);
    }

    /// <summary>
    /// Randomly mangled documents: characters dropped, duplicated and swapped for the delimiters
    /// each grammar cares about. Seeded, so a failure reproduces exactly.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void MutatedDocumentsTokenizeCleanly(string name, string document)
    {
        var tokenizer = For(name);
        var random = new Random(20260801);
        const string Interesting = "<>/\\\"'{}[],:=&;-!?\n\r\t ";

        for (int iteration = 0; iteration < 400; iteration++)
        {
            var text = new StringBuilder(document);
            int edits = random.Next(1, 6);
            for (int e = 0; e < edits && text.Length > 0; e++)
            {
                int at = random.Next(text.Length);
                switch (random.Next(3))
                {
                    case 0: text.Remove(at, 1); break;
                    case 1: text.Insert(at, Interesting[random.Next(Interesting.Length)]); break;
                    default: text[at] = Interesting[random.Next(Interesting.Length)]; break;
                }
            }
            AssertWellFormed(tokenizer, text.ToString());
        }
    }

    /// <summary>Shapes a scanner is most likely to walk off the end of: everything truncated
    /// mid-construct, and the degenerate inputs that have no construct at all.</summary>
    [Theory]
    [InlineData("<")]
    [InlineData("<a")]
    [InlineData("<a ")]
    [InlineData("<a b")]
    [InlineData("<a b=")]
    [InlineData("<a b= ")]
    [InlineData("<a b=\"")]
    [InlineData("<a b=\"unclosed")]
    [InlineData("<!--")]
    [InlineData("<![CDATA[")]
    [InlineData("<?")]
    [InlineData("<!")]
    [InlineData("</")]
    [InlineData("<<<<")]
    [InlineData("{")]
    [InlineData("{\"")]
    [InlineData("{\"a")]
    [InlineData("{\"a\"")]
    [InlineData("{\"a\":")]
    [InlineData("{\"a\":\"unterminated")]
    [InlineData("{\"a\":tru")]
    [InlineData("-")]
    [InlineData("-.")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("\"\\")]
    [InlineData("\"")]
    [InlineData("\"a\"\"")]
    [InlineData(",,,")]
    [InlineData("\r")]
    [InlineData("\n\n")]
    [InlineData("\uD83D")] // a lone high surrogate: a valid char, half a rune
    public void TruncatedConstructsTokenizeCleanly(string text)
    {
        foreach (var tokenizer in new ITokenizer[]
                 { JsonTokenizer.Instance, XmlTokenizer.Instance, CsvTokenizer.Instance, PlainTokenizer.Instance })
        {
            AssertWellFormed(tokenizer, text);
        }
    }

    /// <summary>
    /// The contract a token list has to satisfy for <c>CodeEditor</c> and <c>CodeView</c> to be
    /// able to apply it: in bounds, ordered, non-overlapping, no empty spans, and covering the text
    /// exactly once when the gaps between tokens are counted.
    /// </summary>
    private static void AssertWellFormed(ITokenizer tokenizer, string text)
    {
        var tokens = tokenizer.Tokenize(text);

        int cursor = 0;
        foreach (var token in tokens)
        {
            Assert.True(token.Length > 0, $"Empty token at {token.Start} in {Quote(text)}");
            Assert.True(token.Start >= cursor,
                $"Token at {token.Start} overlaps or precedes cursor {cursor} in {Quote(text)}");
            Assert.True(token.End <= text.Length,
                $"Token [{token.Start},{token.End}) runs past length {text.Length} in {Quote(text)}");
            cursor = token.End;
        }

        var rebuilt = new StringBuilder();
        int pos = 0;
        foreach (var token in tokens)
        {
            rebuilt.Append(text, pos, token.Start - pos);
            rebuilt.Append(text, token.Start, token.Length);
            pos = token.End;
        }
        rebuilt.Append(text, pos, text.Length - pos);
        Assert.Equal(text, rebuilt.ToString());
    }

    private static string Quote(string text) =>
        "\"" + text.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
