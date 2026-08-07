using DevDX.Models;
using DevDX.Services.Syntax;

namespace DevDX.Services.Formats;

/// <summary>
/// Holds the format implementations and does auto-detection by taking the highest confidence
/// (design doc §11.1). v1 ships JSON, XML and CSV; YAML/SQL/TOML/HTML are each one more
/// <see cref="IDataFormat"/> class with zero UI work — the payoff this interface exists for.
/// </summary>
public static class FormatRegistry
{
    public static readonly IDataFormat Json = JsonFormat.Instance;
    public static readonly IDataFormat Xml = XmlFormat.Instance;
    public static readonly IDataFormat Csv = CsvFormat.Instance;

    public static readonly IReadOnlyList<IDataFormat> All = [Json, Xml, Csv];

    /// <summary>
    /// A sentinel passed to <c>FormatterPage</c> meaning "detect the format from content, with a
    /// manual override always visible" — the Data Formatter's "beautify anything" mode (§14.2).
    /// Pages compare by reference (<c>== FormatRegistry.Auto</c>) rather than calling through it,
    /// since the concrete format can only be known once there is text to look at.
    /// </summary>
    public static readonly IDataFormat Auto = new AutoDataFormat();

    /// <summary>The format with the highest <see cref="IDataFormat.DetectConfidence"/> for
    /// <paramref name="text"/>, defaulting to JSON when nothing scores above zero.</summary>
    public static IDataFormat DetectBest(string text)
    {
        IDataFormat best = Json;
        int bestScore = 0;
        foreach (var format in All)
        {
            int score = format.DetectConfidence(text);
            if (score > bestScore)
            {
                bestScore = score;
                best = format;
            }
        }
        return best;
    }

    private sealed class AutoDataFormat : IDataFormat
    {
        public string Id => "auto";
        public string DisplayNameKey => "Format.Auto";
        public string[] Extensions => [".json", ".xml", ".csv"];
        public ITokenizer Tokenizer => PlainTokenizer.Instance;
        public int DetectConfidence(ReadOnlySpan<char> text) => 0;
        public FormatResult Format(string text, FormatOptions options) => DetectBest(text).Format(text, options);
        public FormatResult Minify(string text) => DetectBest(text).Minify(text);
        public IReadOnlyList<Diagnostic> Validate(string text) => DetectBest(text).Validate(text);
        public DataNode ToCanonical(string text) => DetectBest(text).ToCanonical(text);
        public string FromCanonical(DataNode node, FormatOptions options) => Json.FromCanonical(node, options);
    }
}
