using System.Text.RegularExpressions;

namespace dockdev.Services.Text;

public sealed record TextCounts(int Characters, int Words, int Lines);

/// <summary>Line-oriented operations for Text Toolkit (design doc §14.10): sort, dedupe, trim,
/// number, reverse, join/split, counting.</summary>
public static partial class LineOps
{
    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

    public static string SortLines(string text, bool natural, bool descending)
    {
        var lines = Lines(text).ToList();
        IComparer<string> comparer = natural ? new NaturalComparer() : StringComparer.Ordinal;
        lines.Sort(comparer);
        if (descending)
            lines.Reverse();
        return string.Join('\n', lines);
    }

    public static string Dedupe(string text)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var line in Lines(text))
            if (seen.Add(line))
                result.Add(line);
        return string.Join('\n', result);
    }

    public static string TrimLines(string text) => string.Join('\n', Lines(text).Select(l => l.Trim()));

    public static string NumberLines(string text)
    {
        var lines = Lines(text);
        int width = lines.Length.ToString().Length;
        return string.Join('\n', lines.Select((l, i) => $"{(i + 1).ToString().PadLeft(width)}: {l}"));
    }

    public static string ReverseLines(string text) => string.Join('\n', Lines(text).Reverse());

    public static string Join(string text, string separator) => string.Join(separator, Lines(text));

    public static string Split(string text, string separator) =>
        separator.Length == 0 ? text : string.Join('\n', text.Split(separator));

    public static TextCounts Count(string text)
    {
        int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        int lines = text.Length == 0 ? 0 : Lines(text).Length;
        return new TextCounts(text.Length, words, lines);
    }

    /// <summary>Natural sort: numeric runs compare by value, not lexicographically ("item2" before
    /// "item10").</summary>
    private sealed partial class NaturalComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            x ??= "";
            y ??= "";
<<<<<<< HEAD
=======
            var partsX = SplitNumeric().Matches(x).Select(m => m.Value).ToList();
            var partsY = SplitNumeric().Matches(y).Select(m => m.Value).ToList();
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
            List<string> partsX;
            List<string> partsY;
            try
            {
                partsX = SplitNumeric().Matches(x).Select(m => m.Value).ToList();
                partsY = SplitNumeric().Matches(y).Select(m => m.Value).ToList();
            }
            catch (RegexMatchTimeoutException)
            {
<<<<<<< HEAD
                // A pathological input tripped the regex timeout; fall back to an ordinal compare
                // rather than throwing out of a sort comparer (which would abort the whole sort).
=======
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
                return string.CompareOrdinal(x, y);
            }

            int count = Math.Min(partsX.Count, partsY.Count);
            for (int i = 0; i < count; i++)
            {
                bool numX = char.IsDigit(partsX[i][0]);
                bool numY = char.IsDigit(partsY[i][0]);
                int cmp;
                if (numX && numY)
                {
<<<<<<< HEAD
                    // TryParse, not Parse: a numeric run can be longer than BigInteger will parse in
                    // one gulp only in absurd cases, but a non-throwing path keeps the sort alive.
=======
                    var bigX = System.Numerics.BigInteger.Parse(partsX[i]);
                    var bigY = System.Numerics.BigInteger.Parse(partsY[i]);
                    cmp = bigX.CompareTo(bigY);
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
                    if (System.Numerics.BigInteger.TryParse(partsX[i], out var bigX) &&
                        System.Numerics.BigInteger.TryParse(partsY[i], out var bigY))
                    {
                        cmp = bigX.CompareTo(bigY);
                    }
                    else
                    {
                        cmp = string.CompareOrdinal(partsX[i], partsY[i]);
                    }
                }
                else
                {
                    cmp = string.CompareOrdinal(partsX[i], partsY[i]);
                }
                if (cmp != 0)
                    return cmp;
            }
            return partsX.Count.CompareTo(partsY.Count);
        }

<<<<<<< HEAD
=======
        [GeneratedRegex(@"\d+|\D+")]
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
        [GeneratedRegex(@"\d+|\D+", RegexOptions.None, matchTimeoutMilliseconds: 500)]
        private static partial Regex SplitNumeric();
    }
}
