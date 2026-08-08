using System.Diagnostics;
using dockdev.Services.Text;
using Xunit;

namespace dockdev.Tests.Text;

/// <summary>
/// Myers is O((N+M)D), and D is the edit distance — so the cost explodes exactly when two
/// unrelated documents are pasted into a diff tool, which is an ordinary thing to do to one. The
/// old implementation cloned the whole frontier per step and had no ceiling: two 2,000-line
/// documents measured at 122 MB and 153 ms, and it ran synchronously on every keystroke, so
/// 10,000 lines apiece was an out-of-memory crash on a keypress. §22 says that must never happen.
/// <para>
/// These tests hold the two halves of the fix: the script is still right, and the cost is now
/// bounded whatever it is handed.
/// </para>
/// </summary>
public class MyersDiffBoundsTests
{
    // ---- Correctness is unchanged ------------------------------------------

    [Fact]
    public void SimilarDocuments_StillProduceTheMinimalScript()
    {
        var a = Lines("alpha", 500);
        var b = Lines("alpha", 500);
        b[250] = "changed";

        var result = MyersDiff.DiffBounded(a, b);

        Assert.True(result.Minimal);
        Assert.Single(result.Ops, o => o.Kind == ChangeKind.Removed);
        Assert.Single(result.Ops, o => o.Kind == ChangeKind.Added);
        AssertRecovers(a, b, result.Ops);
    }

    [Fact]
    public void ManyScatteredChanges_StillMinimalAndCorrect()
    {
        var a = Lines("line", 400);
        var b = Lines("line", 400);
        for (int i = 0; i < b.Count; i += 7)
            b[i] = "edited " + i;

        var result = MyersDiff.DiffBounded(a, b);

        Assert.True(result.Minimal);
        AssertRecovers(a, b, result.Ops);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(10, 0)]
    [InlineData(0, 10)]
    public void DegenerateSizes_Recover(int aCount, int bCount)
    {
        var a = Lines("a", aCount);
        var b = Lines("b", bCount);
        AssertRecovers(a, b, MyersDiff.DiffBounded(a, b).Ops);
    }

    /// <summary>
    /// The property that matters for any script, minimal or not: replaying it must give back both
    /// inputs exactly. Randomised so the shapes are not just the ones I thought to write down.
    /// </summary>
    [Fact]
    public void GeneratedPairs_AlwaysRecoverBothSides()
    {
        var random = new Random(20260802);
        for (int iteration = 0; iteration < 200; iteration++)
        {
            var a = Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(6).ToString()).ToList();
            var b = Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(6).ToString()).ToList();
            AssertRecovers(a, b, MyersDiff.DiffBounded(a, b).Ops);
        }
    }

    // ---- The cost is now bounded -------------------------------------------

    [Fact]
    public void TotallyDissimilarDocuments_StayWithinBudget()
    {
        // 6,000 lines apiece with nothing in common: the case that used to allocate gigabytes.
        var a = Enumerable.Range(0, 6000).Select(i => $"alpha {i}").ToList();
        var b = Enumerable.Range(0, 6000).Select(i => $"beta {i}").ToList();

        long before = GC.GetTotalAllocatedBytes(precise: false);
        var stopwatch = Stopwatch.StartNew();
        var result = MyersDiff.DiffBounded(a, b);
        stopwatch.Stop();
        double allocatedMb = (GC.GetTotalAllocatedBytes(precise: false) - before) / 1024.0 / 1024.0;

        Assert.False(result.Minimal); // past the budget, so the fallback script
        AssertRecovers(a, b, result.Ops);
        Assert.True(allocatedMb < 120, $"allocated {allocatedMb:F1} MB");
        Assert.True(stopwatch.ElapsedMilliseconds < 4000, $"took {stopwatch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// The case the windowed snapshot is for: a large pair that is nearly identical. Memory used to
    /// scale with D×(N+M) even when D was tiny; now it scales with D alone.
    /// </summary>
    [Fact]
    public void LargeButSimilarDocuments_AreCheap()
    {
        var a = Lines("shared", 8000);
        var b = Lines("shared", 8000);
        b[4000] = "one changed line";

        long before = GC.GetTotalAllocatedBytes(precise: false);
        var result = MyersDiff.DiffBounded(a, b);
        double allocatedMb = (GC.GetTotalAllocatedBytes(precise: false) - before) / 1024.0 / 1024.0;

        Assert.True(result.Minimal);
        Assert.True(allocatedMb < 16, $"allocated {allocatedMb:F1} MB for a two-line edit distance");
    }

    [Fact]
    public void BudgetOfZero_FallsBackImmediatelyOnAnyDifference()
    {
        var a = new List<string> { "a" };
        var b = new List<string> { "b" };

        var result = MyersDiff.DiffBounded(a, b, comparer: null, maxEditDistance: 0);

        Assert.False(result.Minimal);
        AssertRecovers(a, b, result.Ops);
    }

    [Fact]
    public void BudgetOfZero_StillMatchesIdenticalInput()
    {
        // Identical sequences need no edits at all, so a zero budget is enough for them.
        var a = Lines("same", 50);
        var result = MyersDiff.DiffBounded(a, Lines("same", 50), comparer: null, maxEditDistance: 0);

        Assert.True(result.Minimal);
        Assert.All(result.Ops, op => Assert.Equal(ChangeKind.Equal, op.Kind));
    }

    [Fact]
    public void FallbackScript_IsEveryRemovalThenEveryAddition()
    {
        var a = new List<string> { "x", "y" };
        var b = new List<string> { "p", "q" };

        var ops = MyersDiff.DiffBounded(a, b, comparer: null, maxEditDistance: 0).Ops;

        Assert.Equal(
            [ChangeKind.Removed, ChangeKind.Removed, ChangeKind.Added, ChangeKind.Added],
            ops.Select(o => o.Kind));
        AssertRecovers(a, b, ops);
    }

    // ---- Helpers -----------------------------------------------------------

    private static List<string> Lines(string prefix, int count) =>
        Enumerable.Range(0, count).Select(i => $"{prefix} {i}").ToList();

    /// <summary>Replays the script: everything not Added is <c>a</c>, everything not Removed is
    /// <c>b</c>. True of the minimal script and of the fallback alike.</summary>
    private static void AssertRecovers(IReadOnlyList<string> a, IReadOnlyList<string> b, List<DiffOp> ops)
    {
        Assert.Equal(a, ops.Where(o => o.Kind != ChangeKind.Added).Select(o => o.Value));
        Assert.Equal(b, ops.Where(o => o.Kind != ChangeKind.Removed).Select(o => o.Value));
    }
}
