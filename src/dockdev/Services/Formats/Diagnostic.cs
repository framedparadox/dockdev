namespace dockdev.Services.Formats;

/// <summary>One validation problem, 1-based line/column so it lines up with what a text editor
/// (and dockdev's own status bar) would show.</summary>
public sealed record Diagnostic(int Line, int Column, string Message);

/// <summary>Indent width, sort-keys and similar per-format knobs a tool page exposes.</summary>
public sealed record FormatOptions
{
    public int IndentWidth { get; init; } = 2;
    public bool SortKeys { get; init; }

    public static readonly FormatOptions Default = new();
}

/// <summary>
/// The result of a format/minify pass. Invalid input never clears the user's text (design doc
/// §14.1): <see cref="Success"/> is false, <see cref="Text"/> is empty, and
/// <see cref="Diagnostics"/> carries what to show instead.
/// </summary>
public sealed record FormatResult(bool Success, string Text, IReadOnlyList<Diagnostic> Diagnostics)
{
    public static FormatResult Ok(string text) => new(true, text, []);
    public static FormatResult Fail(Diagnostic diagnostic) => new(false, "", [diagnostic]);
    public static FormatResult Fail(IReadOnlyList<Diagnostic> diagnostics) => new(false, "", diagnostics);
}
