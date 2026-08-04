using System.Text;
using DevDX.Models;
using DevDX.Services.Syntax;

namespace DevDX.Services.Formats;

/// <summary>
/// The CSV format: a hand-rolled RFC 4180 reader/writer (no dependency for a simple format).
/// <see cref="ToCanonical"/>/<see cref="FromCanonical"/> operate on the <b>flat</b> shape — an
/// <see cref="ArrayNode"/> of scalar-valued <see cref="ObjectNode"/> rows, dotted keys allowed —
/// which is CSV's native shape. Converting a <em>nested</em> tree (from JSON/XML) through CSV
/// goes through <see cref="CsvProjection"/> first; that is the one place lossiness is confined to
/// (design doc §11.2).
/// </summary>
public sealed class CsvFormat : IDataFormat
{
    public static readonly CsvFormat Instance = new();

    public string Id => "csv";
    public string DisplayNameKey => "Format.Csv";
    public string[] Extensions => [".csv"];
    public ITokenizer Tokenizer => CsvTokenizer.Instance;

    public int DetectConfidence(ReadOnlySpan<char> text)
    {
        var lines = text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(3).ToList();
        if (lines.Count < 2)
            return 0;
        var counts = lines.Select(l => ParseRow(l).Count).ToList();
        bool consistent = counts.Distinct().Count() == 1 && counts[0] > 1;
        return consistent ? 50 : (text.Contains(',') ? 15 : 0);
    }

    public FormatResult Format(string text, FormatOptions options)
    {
        var rows = ParseRows(text);
        return FormatResult.Ok(WriteRows(rows));
    }

    public FormatResult Minify(string text)
    {
        var rows = ParseRows(text)
            .Where(r => r.Any(c => c.Length > 0))
            .Select(r => r.Select(c => c.Trim()).ToList())
            .ToList();
        return FormatResult.Ok(WriteRows(rows));
    }

    public IReadOnlyList<Diagnostic> Validate(string text)
    {
        var rows = ParseRows(text);
        if (rows.Count == 0)
            return [];
        int expected = rows[0].Count;
        var diagnostics = new List<Diagnostic>();
        for (int i = 1; i < rows.Count; i++)
        {
            if (rows[i].Count != expected)
                diagnostics.Add(new Diagnostic(i + 1, 1,
                    $"Row has {rows[i].Count} field(s); header has {expected}."));
        }
        return diagnostics;
    }

    public DataNode ToCanonical(string text)
    {
        var rows = ParseRows(text);
        if (rows.Count == 0)
            return new ArrayNode([]);

        var header = rows[0];
        var records = new List<DataNode>();
        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var members = new List<(string, DataNode)>();
            for (int c = 0; c < header.Count; c++)
            {
                var raw = c < row.Count ? row[c] : "";
                members.Add((header[c], ScalarFor(raw)));
            }
            records.Add(new ObjectNode(members));
        }
        return new ArrayNode(records);
    }

    public string FromCanonical(DataNode node, FormatOptions options)
    {
        var records = node is ArrayNode arr ? arr.Items : [node];

        var header = new List<string>();
        var seen = new HashSet<string>();
        foreach (var record in records)
        {
            if (record is not ObjectNode obj)
                continue;
            foreach (var (key, _) in obj.Members)
                if (seen.Add(key))
                    header.Add(key);
        }

        var rows = new List<List<string>> { header };
        foreach (var record in records)
        {
            var row = new List<string>(new string[header.Count]);
            if (record is ObjectNode obj)
            {
                foreach (var (key, value) in obj.Members)
                {
                    int idx = header.IndexOf(key);
                    if (idx >= 0)
                        row[idx] = (value as ScalarNode)?.Raw ?? "";
                }
            }
            for (int i = 0; i < row.Count; i++)
                row[i] ??= "";
            rows.Add(row);
        }
        return WriteRows(rows);
    }

    private static DataNode ScalarFor(string raw) =>
        decimal.TryParse(raw, out _) && raw.Length > 0
            ? new ScalarNode(raw, ScalarKind.Number)
            : new ScalarNode(raw, ScalarKind.String);

    // ---- RFC 4180 parsing ---------------------------------------------------------------

    /// <summary>Parses the whole document into rows of raw (unescaped) field values. Public so
    /// the Data Masker can sample columns the same way the format itself does.</summary>
    public static List<List<string>> ParseRows(string text)
    {
        var rows = new List<List<string>>();
        int i = 0;
        int n = text.Length;
        while (i < n)
        {
            var (row, next) = ParseOneRow(text, i);
            i = next;
            if (row.Count == 1 && row[0].Length == 0 && i >= n)
                break; // trailing blank line at EOF contributes no row
            rows.Add(row);
        }
        return rows;
    }

    private static List<string> ParseRow(string line) => ParseOneRow(line, 0).Row;

    private static (List<string> Row, int Next) ParseOneRow(string text, int i)
    {
        var fields = new List<string>();
        int n = text.Length;
        var sb = new StringBuilder();

        while (true)
        {
            if (i < n && text[i] == '"')
            {
                i++; // opening quote
                while (i < n)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < n && text[i + 1] == '"')
                        {
                            sb.Append('"');
                            i += 2;
                            continue;
                        }
                        i++; // closing quote
                        break;
                    }
                    sb.Append(text[i]);
                    i++;
                }
            }
            else
            {
                while (i < n && text[i] != ',' && text[i] != '\n' && text[i] != '\r')
                {
                    sb.Append(text[i]);
                    i++;
                }
            }

            fields.Add(sb.ToString());
            sb.Clear();

            if (i < n && text[i] == ',')
            {
                i++;
                continue;
            }
            break;
        }

        if (i < n && text[i] == '\r')
            i++;
        if (i < n && text[i] == '\n')
            i++;

        return (fields, i);
    }

    // ---- Writing --------------------------------------------------------------------------

    private static string WriteRows(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            for (int c = 0; c < row.Count; c++)
            {
                if (c > 0)
                    sb.Append(',');
                sb.Append(WriteField(row[c]));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Quotes a field if it needs it, and neutralizes CSV/formula injection (design doc §21): a
    /// leading <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> can be executed as a formula by whatever
    /// spreadsheet opens the exported file later, so it is prefixed with a plain single quote —
    /// the standard "force text" escape — even though DevDX itself never executes anything.
    /// </summary>
    private static string WriteField(string field)
    {
        if (field.Length > 0 && field[0] is '=' or '+' or '-' or '@')
            field = "'" + field;

        bool needsQuoting = field.IndexOfAny([',', '"', '\n', '\r']) >= 0;
        if (!needsQuoting)
            return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
