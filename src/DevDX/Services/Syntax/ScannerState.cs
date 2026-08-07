namespace DevDX.Services.Syntax;

/// <summary>
/// The scanner state at the end of a line — what a tokenizer needs to resume mid-construct (a
/// string with escaped newlines, an XML comment spanning lines). A value type: one enum plus a
/// nesting depth, not a parse tree.
/// </summary>
public readonly record struct ScannerState(int Mode, int Depth)
{
    public static readonly ScannerState Initial = new(0, 0);
}
