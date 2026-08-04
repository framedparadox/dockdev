namespace DevDX.Services.Syntax;

/// <summary>
/// RFC 4180 field scanner for display colouring: the header row's fields render as
/// <see cref="TokenKind.PropertyName"/>, numeric-looking data fields as <see cref="TokenKind.Number"/>,
/// everything else as <see cref="TokenKind.String"/>. Delimiters, quotes and newlines are left as
/// untokenized gaps.
/// </summary>
public sealed class CsvTokenizer : ITokenizer
{
    public static readonly CsvTokenizer Instance = new();

    public IReadOnlyList<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int i = 0;
        int n = text.Length;
        bool firstRow = true;

        while (i < n)
        {
            bool rowEmpty = true;
            while (i < n && text[i] != '\n' && text[i] != '\r')
            {
                rowEmpty = false;
                if (text[i] == '"')
                {
                    int start = i;
                    i++;
                    while (i < n)
                    {
                        if (text[i] == '"')
                        {
                            if (i + 1 < n && text[i + 1] == '"')
                            {
                                i += 2;
                                continue;
                            }
                            i++;
                            break;
                        }
                        i++;
                    }
                    tokens.Add(new Token(start, i - start, firstRow ? TokenKind.PropertyName : TokenKind.String));
                }
                else if (text[i] == ',')
                {
                    i++; // delimiter: gap
                }
                else
                {
                    int start = i;
                    while (i < n && text[i] != ',' && text[i] != '\n' && text[i] != '\r')
                        i++;
                    var field = text.AsSpan(start, i - start);
                    tokens.Add(new Token(start, i - start, firstRow ? TokenKind.PropertyName : (LooksNumeric(field) ? TokenKind.Number : TokenKind.String)));
                }
            }

            while (i < n && (text[i] == '\n' || text[i] == '\r'))
                i++;
            if (!rowEmpty)
                firstRow = false;
        }

        return tokens;
    }

    private static bool LooksNumeric(ReadOnlySpan<char> field) =>
        field.Length > 0 && decimal.TryParse(field, out _);

    public IReadOnlyList<Token> TokenizeRange(string text, LineIndex lines, int first, int last, ScannerState entryState) =>
        TokenizerRangeSupport.TokenizeRangeViaWholeDocument(this, text, lines, first, last);

    public ScannerState StateAfter(string text, LineIndex lines, int line, ScannerState entryState) => ScannerState.Initial;
}
