namespace DevDX.Services.Syntax;

/// <summary>
/// The character offset of every line start in a document, built in one O(n) pass. Cheap even for
/// a large document (an <c>int[]</c>), and it is what maps a caret position to a line/column for
/// the status bar and error banners, and what <c>CodeView</c> uses to find which lines are visible.
/// </summary>
public sealed class LineIndex
{
    private readonly int[] _starts;

    public LineIndex(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }
        _starts = starts.ToArray();
    }

    public int LineCount => _starts.Length;

    /// <summary>The character offset where line <paramref name="line"/> (0-based) starts.</summary>
    public int StartOf(int line) => _starts[Math.Clamp(line, 0, _starts.Length - 1)];

    /// <summary>The 0-based line index containing character offset <paramref name="pos"/>.</summary>
    public int LineAt(int pos)
    {
        int lo = 0, hi = _starts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_starts[mid] <= pos)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Maps an absolute offset to a 1-based (line, column) pair, for status bars and
    /// error banners.</summary>
    public (int Line, int Column) ToLineColumn(int pos)
    {
        int line = LineAt(pos);
        return (line + 1, pos - _starts[line] + 1);
    }
}
