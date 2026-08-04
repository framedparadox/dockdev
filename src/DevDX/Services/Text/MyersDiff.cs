namespace DevDX.Services.Text;

public enum ChangeKind { Equal, Added, Removed }

public sealed record DiffOp(ChangeKind Kind, string Value);

/// <summary>An edit script, and whether it is the shortest one.</summary>
/// <param name="Ops">The script: every element of <c>a</c> as Equal or Removed, every element of
/// <c>b</c> as Equal or Added, in order.</param>
/// <param name="Minimal">False when the search hit its budget and the result is the fallback
/// "everything replaced" script rather than the shortest edit. Still a correct description of the
/// two inputs — just not a useful one, which is worth telling the reader.</param>
public sealed record DiffResult(List<DiffOp> Ops, bool Minimal);

/// <summary>
/// The classic Myers O((N+M)D) shortest-edit-script diff, generic over any sequence of comparable
/// strings — lines for <c>Text Diff</c>'s line-level pass, words for its within-line refinement
/// (design doc §14: "Myers diff at line level, refined to word level within changed lines").
/// <para>
/// <b>Bounded, because Myers is only cheap on similar inputs.</b> The cost is driven by D, the edit
/// distance, and for two documents with nothing in common D is N+M — so the work and the memory go
/// quadratic exactly when someone pastes two unrelated files in, which is a perfectly ordinary
/// thing to do to a diff tool. Two 2,000-line documents measured at 122 MB and 153 ms, two 5,000-
/// line ones extrapolate to about 765 MB, and 10,000 lines apiece is several gigabytes — an OOM on
/// a keystroke, which is precisely what §22 says must never happen. Two things bound it:
/// </para>
/// <list type="number">
/// <item>The per-step snapshot keeps only the diagonals that step could have touched
/// (<c>k ∈ [-(d+1), d+1]</c>) rather than cloning the whole frontier. Total memory becomes O(D²)
/// instead of O(D·(N+M)), which for the ordinary case — big files, few changes — is the difference
/// between megabytes and kilobytes.</item>
/// <item><see cref="MaxEditDistance"/> caps D outright. Past it the answer is the fallback script,
/// every element of <c>a</c> removed and every element of <c>b</c> added, flagged by
/// <see cref="DiffResult.Minimal"/>. For inputs that far apart the minimal script is unreadable
/// anyway; the point of the budget is that the tool answers rather than dies.</item>
/// </list>
/// </summary>
public static class MyersDiff
{
    /// <summary>
    /// The largest edit distance the search will explore. The snapshots cost about
    /// <c>4·D²</c> bytes in total, so this is a ~25 MB ceiling on a worst case that used to have
    /// none. Reached only when the inputs differ by more than this many elements — two documents
    /// sharing nothing hit it at 2,500 lines between them.
    /// </summary>
    public const int MaxEditDistance = 2500;

    /// <summary>The shortest edit script, or the fallback one if the inputs are too far apart.
    /// Callers that only want the script can use <see cref="Diff"/>.</summary>
    public static DiffResult DiffBounded(IReadOnlyList<string> a, IReadOnlyList<string> b,
        IEqualityComparer<string>? comparer = null, int maxEditDistance = MaxEditDistance)
    {
        comparer ??= EqualityComparer<string>.Default;
        int n = a.Count, m = b.Count;
        int max = n + m;
        if (max == 0)
            return new DiffResult([], true);

        int budget = Math.Min(max, Math.Max(maxEditDistance, 0));

        int offset = max;
        var v = new int[2 * max + 1];
        var trace = new List<int[]>();
        int foundD = -1;

        for (int d = 0; d <= budget; d++)
        {
            trace.Add(Snapshot(v, offset, d));
            for (int k = -d; k <= d; k += 2)
            {
                int x = (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                int y = x - k;

                while (x < n && y < m && comparer.Equals(a[x], b[y]))
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;

                if (x >= n && y >= m)
                {
                    foundD = d;
                    break;
                }
            }
            if (foundD >= 0)
                break;
        }

        if (foundD < 0)
            return new DiffResult(ReplaceEverything(a, b), false);

        var ops = new List<DiffOp>();
        int cx = n, cy = m;
        for (int d = trace.Count - 1; d >= 0; d--)
        {
            int k = cx - cy;
            int prevK = (k == -d || (k != d && Read(trace[d], d, k - 1) < Read(trace[d], d, k + 1)))
                ? k + 1
                : k - 1;
            int prevX = Read(trace[d], d, prevK);
            int prevY = prevX - prevK;

            while (cx > prevX && cy > prevY)
            {
                ops.Add(new DiffOp(ChangeKind.Equal, a[cx - 1]));
                cx--;
                cy--;
            }

            if (d > 0)
            {
                if (cx == prevX)
                    ops.Add(new DiffOp(ChangeKind.Added, b[cy - 1]));
                else
                    ops.Add(new DiffOp(ChangeKind.Removed, a[cx - 1]));
            }

            cx = prevX;
            cy = prevY;
        }

        ops.Reverse();
        return new DiffResult(ops, true);
    }

    /// <summary>The edit script alone. Kept for callers that have no use for the minimality flag —
    /// a too-far-apart pair still comes back as a correct, if coarse, script.</summary>
    public static List<DiffOp> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b,
        IEqualityComparer<string>? comparer = null) => DiffBounded(a, b, comparer).Ops;

    /// <summary>
    /// The frontier restricted to the diagonals step <paramref name="d"/> can reach. Backtracking
    /// from step d reads k±1 for |k| ≤ d, so [-(d+1), d+1] is exactly what has to survive; every
    /// diagonal outside it is still at its initial zero, which is what the caller would have read
    /// from a full clone anyway.
    /// </summary>
    private static int[] Snapshot(int[] v, int offset, int d)
    {
        var window = new int[2 * d + 3];
        for (int k = -(d + 1); k <= d + 1; k++)
        {
            int index = offset + k;
            if (index >= 0 && index < v.Length)
                window[k + d + 1] = v[index];
        }
        return window;
    }

    private static int Read(int[] window, int d, int k) =>
        k >= -(d + 1) && k <= d + 1 ? window[k + d + 1] : 0;

    /// <summary>The script for two sequences with nothing usable in common: all of the first gone,
    /// all of the second new. Correct, cheap, and the honest answer when the budget runs out.</summary>
    private static List<DiffOp> ReplaceEverything(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var ops = new List<DiffOp>(a.Count + b.Count);
        foreach (var line in a)
            ops.Add(new DiffOp(ChangeKind.Removed, line));
        foreach (var line in b)
            ops.Add(new DiffOp(ChangeKind.Added, line));
        return ops;
    }

    /// <summary>Splits on whitespace boundaries while keeping the whitespace as its own token, so
    /// re-joining every token reconstructs the original line exactly.</summary>
    public static List<string> SplitWords(string line)
    {
        var words = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            int start = i;
            bool isSpace = char.IsWhiteSpace(line[i]);
            while (i < line.Length && char.IsWhiteSpace(line[i]) == isSpace)
                i++;
            words.Add(line[start..i]);
        }
        return words;
    }
}
