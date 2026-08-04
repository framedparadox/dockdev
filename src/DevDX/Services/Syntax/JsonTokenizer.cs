namespace DevDX.Services.Syntax;

/// <summary>
/// Hand-written single-pass JSON scanner. Never throws on malformed input — an unrecognized
/// character becomes a one-character <see cref="TokenKind.Error"/> token and scanning continues,
/// so a highlighter never drops or duplicates characters (see the reconstruction property test).
/// </summary>
public sealed class JsonTokenizer : ITokenizer
{
    public static readonly JsonTokenizer Instance = new();

    private enum Container { Object, Array }

    public IReadOnlyList<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var stack = new Stack<(Container Kind, bool ExpectingKey)>();
        int i = 0;
        int n = text.Length;

        while (i < n)
        {
            char c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '{':
                    tokens.Add(new Token(i, 1, TokenKind.Punctuation));
                    stack.Push((Container.Object, true));
                    i++;
                    continue;
                case '[':
                    tokens.Add(new Token(i, 1, TokenKind.Punctuation));
                    stack.Push((Container.Array, false));
                    i++;
                    continue;
                case '}':
                case ']':
                    tokens.Add(new Token(i, 1, TokenKind.Punctuation));
                    if (stack.Count > 0)
                        stack.Pop();
                    i++;
                    continue;
                case ':':
                    tokens.Add(new Token(i, 1, TokenKind.Punctuation));
                    i++;
                    continue;
                case ',':
                    tokens.Add(new Token(i, 1, TokenKind.Punctuation));
                    if (stack.Count > 0 && stack.Peek().Kind == Container.Object)
                    {
                        var top = stack.Pop();
                        stack.Push((top.Kind, true));
                    }
                    i++;
                    continue;
                case '"':
                {
                    int start = i;
                    i = ScanString(text, i);
                    bool isKey = stack.Count > 0 && stack.Peek().Kind == Container.Object && stack.Peek().ExpectingKey;
                    tokens.Add(new Token(start, i - start, isKey ? TokenKind.PropertyName : TokenKind.String));
                    if (isKey)
                    {
                        var top = stack.Pop();
                        stack.Push((top.Kind, false));
                    }
                    continue;
                }
                default:
                    if (c == '-' || char.IsAsciiDigit(c))
                    {
                        int start = i;
                        i = ScanNumber(text, i);
                        if (i == start)
                        {
                            tokens.Add(new Token(i, 1, TokenKind.Error));
                            i++;
                        }
                        else
                        {
                            tokens.Add(new Token(start, i - start, TokenKind.Number));
                        }
                        continue;
                    }
                    if (MatchKeyword(text, i, "true") || MatchKeyword(text, i, "false"))
                    {
                        int len = text[i] == 't' ? 4 : 5;
                        tokens.Add(new Token(i, len, TokenKind.Boolean));
                        i += len;
                        continue;
                    }
                    if (MatchKeyword(text, i, "null"))
                    {
                        tokens.Add(new Token(i, 4, TokenKind.Null));
                        i += 4;
                        continue;
                    }
                    tokens.Add(new Token(i, 1, TokenKind.Error));
                    i++;
                    continue;
            }
        }

        return tokens;
    }

    private static bool MatchKeyword(string text, int i, string keyword) =>
        i + keyword.Length <= text.Length && string.CompareOrdinal(text, i, keyword, 0, keyword.Length) == 0;

    /// <summary>Scans a JSON string starting at an opening quote; returns the index just past the
    /// closing quote (or end of text if the string is unterminated).</summary>
    private static int ScanString(string text, int start)
    {
        int i = start + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }
            if (c == '"')
                return i + 1;
            i++;
        }
        return i; // unterminated: consume to end rather than throw
    }

    private static int ScanNumber(string text, int start)
    {
        int i = start;
        if (i < text.Length && text[i] == '-')
            i++;
        int digitsStart = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
            i++;
        if (i == digitsStart)
            return start; // not actually a number (bare '-')

        if (i < text.Length && text[i] == '.')
        {
            int fracStart = i + 1;
            int j = fracStart;
            while (j < text.Length && char.IsAsciiDigit(text[j]))
                j++;
            if (j > fracStart)
                i = j;
        }
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            int expStart = i + 1;
            int j = expStart;
            if (j < text.Length && (text[j] == '+' || text[j] == '-'))
                j++;
            int digitsAfterSign = j;
            while (j < text.Length && char.IsAsciiDigit(text[j]))
                j++;
            if (j > digitsAfterSign)
                i = j;
        }
        return i;
    }

    public IReadOnlyList<Token> TokenizeRange(string text, LineIndex lines, int first, int last, ScannerState entryState) =>
        TokenizerRangeSupport.TokenizeRangeViaWholeDocument(this, text, lines, first, last);

    public ScannerState StateAfter(string text, LineIndex lines, int line, ScannerState entryState) => ScannerState.Initial;
}
