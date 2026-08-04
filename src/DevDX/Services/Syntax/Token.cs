namespace DevDX.Services.Syntax;

/// <summary>
/// A coloured span. Every syntax-highlighting, regex-match, PII-finding and diff-change feature
/// in DevDX is "colour these ranges of this text" — this one struct is the shared vocabulary
/// (see the design document §10).
/// </summary>
public readonly record struct Token(int Start, int Length, TokenKind Kind)
{
    public int End => Start + Length;
}

public enum TokenKind
{
    Plain, Punctuation, PropertyName, String, Number, Boolean, Null, Comment,
    TagName, AttributeName, AttributeValue, CData, Keyword, Error,   // syntax
    Match, Group,                                                     // regex
    Finding, Masked,                                                  // masker
    Added, Removed,                                                   // diff
}
