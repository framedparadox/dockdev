namespace dockdev.Services.Syntax;

/// <summary>
/// Tokenizes a document into coloured spans. Tokenizers are hand-written single-pass scanners —
/// no regex on hot paths, no allocation per character — so they are pure functions of a string
/// and 100% unit-testable with no UI.
/// <para>
/// <b>Property every tokenizer must satisfy:</b> concatenating every token's span in order, plus
/// the gaps between them, must reconstruct the input byte for byte. See
/// <c>dockdev.Tests.Syntax.TokenizerReconstructionTests</c>.
/// </para>
/// </summary>
public interface ITokenizer
{
    /// <summary>Tokenize the whole document.</summary>
    IReadOnlyList<Token> Tokenize(string text);

    /// <summary>
    /// Tokenize only lines [<paramref name="first"/>, <paramref name="last"/>), for
    /// <c>CodeView</c>'s "visible lines only" performance tier (design doc §22). The v1
    /// implementation tokenizes the whole document (cheap relative to layout/render for the sizes
    /// dockdev targets) and filters to the requested lines, rather than a fully resumable
    /// incremental scan — see <see cref="StateAfter"/>.
    /// </summary>
    IReadOnlyList<Token> TokenizeRange(string text, LineIndex lines, int first, int last, ScannerState entryState);

    /// <summary>The scanner state at the end of a line, so a future incremental implementation of
    /// <see cref="TokenizeRange"/> can resume mid-construct. v1 tokenizers have no cross-line
    /// state (JSON/CSV never span an unterminated construct across a line in valid input, and the
    /// whole-document fallback makes this moot for XML too), so this returns
    /// <see cref="ScannerState.Initial"/>.</summary>
    ScannerState StateAfter(string text, LineIndex lines, int line, ScannerState entryState);
}

/// <summary>Shared default implementations of the range/state members, in terms of the
/// whole-document <see cref="ITokenizer.Tokenize"/> — see the type-level remarks on why v1 does
/// not implement a truly resumable incremental scan.</summary>
public static class TokenizerRangeSupport
{
    public static IReadOnlyList<Token> TokenizeRangeViaWholeDocument(
        ITokenizer tokenizer, string text, LineIndex lines, int first, int last)
    {
        first = Math.Clamp(first, 0, lines.LineCount);
        last = Math.Clamp(last, first, lines.LineCount);
        int startOffset = lines.StartOf(first);
        int endOffset = last >= lines.LineCount ? text.Length : lines.StartOf(last);

        var result = new List<Token>();
        foreach (var token in tokenizer.Tokenize(text))
        {
            if (token.Start >= endOffset)
                break;
            if (token.End <= startOffset)
                continue;
            result.Add(token);
        }
        return result;
    }
}
