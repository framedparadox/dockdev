namespace DevDX.Services.Syntax;

/// <summary>The no-op tokenizer: used for anything with no format-specific highlighting (plain
/// text tools, or a format that failed to parse). Emits a single <see cref="TokenKind.Plain"/>
/// span covering the whole document.</summary>
public sealed class PlainTokenizer : ITokenizer
{
    public static readonly PlainTokenizer Instance = new();

    public IReadOnlyList<Token> Tokenize(string text) =>
        text.Length == 0 ? [] : [new Token(0, text.Length, TokenKind.Plain)];

    public IReadOnlyList<Token> TokenizeRange(string text, LineIndex lines, int first, int last, ScannerState entryState) =>
        TokenizerRangeSupport.TokenizeRangeViaWholeDocument(this, text, lines, first, last);

    public ScannerState StateAfter(string text, LineIndex lines, int line, ScannerState entryState) => ScannerState.Initial;
}
