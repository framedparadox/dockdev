using dockdev.Services;
using dockdev.Services.Syntax;
using dockdev.Services.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace dockdev.Controls;

/// <summary>
/// Text Diff's output surface. Renders a <b>unified</b> diff (removed / added / unchanged lines
/// in one column, each prefixed with a coloured <c>-</c>/<c>+</c>/space marker) rather than the
/// design document's two-synchronised-panes layout — a deliberate simplification that reuses
/// <see cref="CodeView"/> unchanged instead of hand-rolling scroll-position sync between two
/// panes, while still satisfying §10.3's "colour is never the only signal" (the literal +/- glyph
/// carries the meaning) and §14's "line level, refined to word level within changed lines".
/// </summary>
public sealed class DiffView : Grid
{
    private readonly CodeView _codeView = new() { ShowLineNumbers = false };
    private readonly TextBlock _summary = new() { Opacity = 0.7, Margin = new Thickness(4, 0, 0, 4) };

    public int ChangeCount { get; private set; }

    /// <summary>
    /// False when the two inputs were too far apart for <see cref="MyersDiff"/>'s budget and the
    /// view is showing the fallback "everything replaced" script. Surfaced so the page can say so:
    /// a diff that claims every line changed is indistinguishable from a diff of two genuinely
    /// unrelated documents, and the reader deserves to know which one they are looking at.
    /// </summary>
    public bool IsMinimal { get; private set; } = true;

    /// <summary>Forwards to the underlying <see cref="CodeView"/>'s accessible name — see
    /// <see cref="CodeView.AccessibleName"/>.</summary>
    public string AccessibleName
    {
        get => _codeView.AccessibleName;
        set => _codeView.AccessibleName = value;
    }

    public DiffView()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_summary, 0);
        Grid.SetRow(_codeView, 1);
        Children.Add(_summary);
        Children.Add(_codeView);
    }

    public void Compute(string leftText, string rightText, bool ignoreWhitespace, bool ignoreCase, bool ignoreBlankLines)
    {
        var linesA = NormalizeLines(leftText);
        var linesB = NormalizeLines(rightText);
        var comparer = new LineComparer(ignoreWhitespace, ignoreCase, ignoreBlankLines);
        var lineDiff = MyersDiff.DiffBounded(linesA, linesB, comparer);
        IsMinimal = lineDiff.Minimal;
        var blocks = GroupConsecutive(lineDiff.Ops);

        var text = new System.Text.StringBuilder();
        var tokens = new List<Token>();
        int changeCount = 0;
        bool first = true;

        void EmitLine(char marker, string content, IReadOnlyList<(int Start, int Length, TokenKind Kind)>? spans)
        {
            if (!first)
                text.Append('\n');
            first = false;

            int lineStart = text.Length;
            text.Append(marker).Append(' ').Append(content);

            if (marker != ' ')
                tokens.Add(new Token(lineStart, 1, marker == '+' ? TokenKind.Added : TokenKind.Removed));
            if (spans is not null)
                foreach (var (start, length, kind) in spans)
                    tokens.Add(new Token(lineStart + 2 + start, length, kind));
        }

        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.Kind == ChangeKind.Equal)
            {
                foreach (var line in block.Values)
                    EmitLine(' ', line, null);
                continue;
            }

            if (block.Kind == ChangeKind.Removed && i + 1 < blocks.Count && blocks[i + 1].Kind == ChangeKind.Added)
            {
                var removed = block.Values;
                var added = blocks[i + 1].Values;
                int pairCount = Math.Min(removed.Count, added.Count);
                for (int p = 0; p < pairCount; p++)
                {
                    changeCount++;
                    var (oldSpans, newSpans) = WordDiffLine(removed[p], added[p]);
                    EmitLine('-', removed[p], oldSpans);
                    EmitLine('+', added[p], newSpans);
                }
                for (int p = pairCount; p < removed.Count; p++)
                {
                    changeCount++;
                    EmitLine('-', removed[p], [(0, removed[p].Length, TokenKind.Removed)]);
                }
                for (int p = pairCount; p < added.Count; p++)
                {
                    changeCount++;
                    EmitLine('+', added[p], [(0, added[p].Length, TokenKind.Added)]);
                }
                i++; // the Added block was consumed alongside its Removed pair
                continue;
            }

            char marker = block.Kind == ChangeKind.Removed ? '-' : '+';
            var wholeKind = block.Kind == ChangeKind.Removed ? TokenKind.Removed : TokenKind.Added;
            foreach (var line in block.Values)
            {
                changeCount++;
                EmitLine(marker, line, [(0, line.Length, wholeKind)]);
            }
        }

        ChangeCount = changeCount;
        _summary.Text = !IsMinimal
            ? Loc.Get("Diff.TooDifferent")
            : changeCount == 0
                ? Loc.Get("Diff.NoChanges")
                : Loc.Format("Diff.ChangeCount", changeCount);
        _codeView.SetContent(text.ToString(), tokens);
    }

    private static List<string> NormalizeLines(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList();

    private static (List<(int, int, TokenKind)> OldSpans, List<(int, int, TokenKind)> NewSpans) WordDiffLine(string oldLine, string newLine)
    {
        var wordsA = MyersDiff.SplitWords(oldLine);
        var wordsB = MyersDiff.SplitWords(newLine);
        var ops = MyersDiff.Diff(wordsA, wordsB);

        var oldSpans = new List<(int, int, TokenKind)>();
        var newSpans = new List<(int, int, TokenKind)>();
        int posA = 0, posB = 0;
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case ChangeKind.Equal:
                    posA += op.Value.Length;
                    posB += op.Value.Length;
                    break;
                case ChangeKind.Removed:
                    oldSpans.Add((posA, op.Value.Length, TokenKind.Removed));
                    posA += op.Value.Length;
                    break;
                case ChangeKind.Added:
                    newSpans.Add((posB, op.Value.Length, TokenKind.Added));
                    posB += op.Value.Length;
                    break;
            }
        }
        return (oldSpans, newSpans);
    }

    private sealed record Block(ChangeKind Kind, List<string> Values);

    private static List<Block> GroupConsecutive(List<DiffOp> ops)
    {
        var blocks = new List<Block>();
        foreach (var op in ops)
        {
            if (blocks.Count > 0 && blocks[^1].Kind == op.Kind)
                blocks[^1].Values.Add(op.Value);
            else
                blocks.Add(new Block(op.Kind, [op.Value]));
        }
        return blocks;
    }

    private sealed class LineComparer(bool ignoreWhitespace, bool ignoreCase, bool ignoreBlankLines) : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y)
        {
            x ??= "";
            y ??= "";
            if (ignoreBlankLines && x.Trim().Length == 0 && y.Trim().Length == 0)
                return true;
            if (ignoreWhitespace)
            {
                x = string.Concat(x.Where(c => !char.IsWhiteSpace(c)));
                y = string.Concat(y.Where(c => !char.IsWhiteSpace(c)));
            }
            return ignoreCase
                ? string.Equals(x, y, StringComparison.CurrentCultureIgnoreCase)
                : string.Equals(x, y, StringComparison.Ordinal);
        }

        public int GetHashCode(string obj) => 0; // never called: MyersDiff only calls Equals
    }
}
