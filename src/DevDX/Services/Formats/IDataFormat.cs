using DevDX.Models;
using DevDX.Services.Syntax;

namespace DevDX.Services.Formats;

/// <summary>
/// One structured text format. Implement this once and a format lights up the formatter, the
/// converter and the masker with no further UI work (design doc §11.1) — the payoff that makes
/// YAML/SQL/TOML/HTML "one class" roadmap items rather than new tools.
/// </summary>
public interface IDataFormat
{
    string Id { get; }
    string DisplayNameKey { get; }
    string[] Extensions { get; }

    /// <summary>0–100. Drives Auto mode; the user can always override.</summary>
    int DetectConfidence(ReadOnlySpan<char> text);

    ITokenizer Tokenizer { get; }

    FormatResult Format(string text, FormatOptions options);
    FormatResult Minify(string text);
    IReadOnlyList<Diagnostic> Validate(string text);

    DataNode ToCanonical(string text);
    string FromCanonical(DataNode node, FormatOptions options);
}
