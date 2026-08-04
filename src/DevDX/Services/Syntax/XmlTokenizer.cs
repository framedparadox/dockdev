namespace DevDX.Services.Syntax;

/// <summary>
/// Hand-written single-pass XML scanner: tags, attributes, comments and CDATA. Text content
/// between tags is left as an untokenized gap (rendered <see cref="TokenKind.Plain"/> by
/// <c>CodeView</c>). Never throws on malformed input.
/// </summary>
public sealed class XmlTokenizer : ITokenizer
{
    public static readonly XmlTokenizer Instance = new();

    public IReadOnlyList<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int i = 0;
        int n = text.Length;

        while (i < n)
        {
            if (text[i] != '<')
            {
                i++;
                continue;
            }

            if (Starts(text, i, "<!--"))
            {
                int end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                int close = end < 0 ? n : end + 3;
                tokens.Add(new Token(i, close - i, TokenKind.Comment));
                i = close;
                continue;
            }

            if (Starts(text, i, "<![CDATA["))
            {
                int end = text.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                int close = end < 0 ? n : end + 3;
                tokens.Add(new Token(i, close - i, TokenKind.CData));
                i = close;
                continue;
            }

            if (Starts(text, i, "<?"))
            {
                int end = text.IndexOf("?>", i + 2, StringComparison.Ordinal);
                int close = end < 0 ? n : end + 2;
                tokens.Add(new Token(i, close - i, TokenKind.Comment));
                i = close;
                continue;
            }

            if (Starts(text, i, "<!"))
            {
                // DOCTYPE or other declaration: treat as a single comment-styled block up to '>'.
                int end = text.IndexOf('>', i + 2);
                int close = end < 0 ? n : end + 1;
                tokens.Add(new Token(i, close - i, TokenKind.Comment));
                i = close;
                continue;
            }

            // A real element tag: '<' ['/'] name (attr='value')* ['/'] '>'
            int tagStart = i;
            tokens.Add(new Token(i, 1, TokenKind.Punctuation));
            i++;
            if (i < n && text[i] == '/')
            {
                tokens.Add(new Token(i, 1, TokenKind.Punctuation));
                i++;
            }

            int nameStart = i;
            while (i < n && IsNameChar(text[i]))
                i++;
            if (i > nameStart)
                tokens.Add(new Token(nameStart, i - nameStart, TokenKind.TagName));

            // Attributes.
            while (i < n)
            {
                while (i < n && char.IsWhiteSpace(text[i]))
                    i++;
                if (i >= n)
                    break;
                if (text[i] == '/' || text[i] == '>')
                    break;

                int attrStart = i;
                while (i < n && text[i] != '=' && !char.IsWhiteSpace(text[i]) && text[i] != '>' && text[i] != '/')
                    i++;
                if (i > attrStart)
                    tokens.Add(new Token(attrStart, i - attrStart, TokenKind.AttributeName));
                else
                {
                    // Stuck on something unexpected (e.g. a stray '='): consume one char as error
                    // to guarantee forward progress.
                    tokens.Add(new Token(i, 1, TokenKind.Error));
                    i++;
                    continue;
                }

                while (i < n && char.IsWhiteSpace(text[i]))
                    i++;
                if (i < n && text[i] == '=')
                {
                    tokens.Add(new Token(i, 1, TokenKind.Punctuation));
                    i++;
                    while (i < n && char.IsWhiteSpace(text[i]))
                        i++;
                    if (i < n && (text[i] == '"' || text[i] == '\''))
                    {
                        char quote = text[i];
                        int valStart = i;
                        i++;
                        while (i < n && text[i] != quote)
                            i++;
                        if (i < n)
                            i++; // consume closing quote
                        tokens.Add(new Token(valStart, i - valStart, TokenKind.AttributeValue));
                    }
                }
            }

            if (i < n && text[i] == '/')
            {
                tokens.Add(new Token(i, 1, TokenKind.Punctuation));
                i++;
            }
            if (i < n && text[i] == '>')
            {
                tokens.Add(new Token(i, 1, TokenKind.Punctuation));
                i++;
            }

            if (i == tagStart)
                i++; // guarantee forward progress on pathological input
        }

        return tokens;
    }

    private static bool Starts(string text, int i, string token) =>
        i + token.Length <= text.Length && string.CompareOrdinal(text, i, token, 0, token.Length) == 0;

    private static bool IsNameChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':';

    public IReadOnlyList<Token> TokenizeRange(string text, LineIndex lines, int first, int last, ScannerState entryState) =>
        TokenizerRangeSupport.TokenizeRangeViaWholeDocument(this, text, lines, first, last);

    public ScannerState StateAfter(string text, LineIndex lines, int line, ScannerState entryState) => ScannerState.Initial;
}
