using System.Text.RegularExpressions;
using dockdev.Services.Syntax;

namespace dockdev.Services.Tools;

public sealed record RegexGroupInfo(int Index, string Name, string Value, int Position);

public sealed record RegexRunResult(
    bool Success, string? Error, bool TimedOut,
    IReadOnlyList<Token> Tokens, IReadOnlyList<RegexGroupInfo> Groups, string? Replacement);

/// <summary>
/// Regex Tester's engine (design doc §14.12). <b>Every <see cref="Regex"/> is constructed with an
/// explicit <see cref="Regex.MatchTimeout"/></b> — a regex tester is the one place in the app
/// where the user can trivially write a catastrophic-backtracking pattern, and it must degrade to
/// "pattern timed out" rather than hang. Callers are expected to invoke this off the UI thread.
/// </summary>
public static class RegexRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public static RegexRunResult Run(string pattern, string subject, RegexOptions options, string? replacement)
    {
        if (pattern.Length == 0)
            return new RegexRunResult(true, null, false, [], [], null);

        Regex regex;
        try
        {
            regex = new Regex(pattern, options, Timeout);
        }
        catch (ArgumentException ex)
        {
            return new RegexRunResult(false, ex.Message, false, [], [], null);
        }

        try
        {
            var paint = new Paint[subject.Length];
            var groups = new List<RegexGroupInfo>();
            foreach (Match match in regex.Matches(subject))
            {
                Fill(paint, match.Index, match.Length, Paint.Match);
                foreach (Group group in match.Groups)
                {
                    if (!group.Success)
                        continue;
                    // Group 0 is the match itself — it is listed in the table but must not repaint
                    // the match as a group, or every match would read as one big capture.
                    if (group.Name != "0")
                        Fill(paint, group.Index, group.Length, Paint.Group);
                    groups.Add(new RegexGroupInfo(
                        int.TryParse(group.Name, out var idx) ? idx : -1, group.Name, group.Value, group.Index));
                }
            }

            string? replaced = null;
            if (replacement is not null)
            {
                try { replaced = regex.Replace(subject, replacement); }
                catch (ArgumentException) { replaced = null; }
            }

            return new RegexRunResult(true, null, false, Flatten(paint), groups, replaced);
        }
        catch (RegexMatchTimeoutException)
        {
            return new RegexRunResult(false, "Pattern timed out.", true, [], [], null);
        }
    }

    /// <summary>What a single character of the subject ended up being part of.</summary>
    private enum Paint : byte { None, Match, Group }

    private static void Fill(Paint[] paint, int start, int length, Paint value)
    {
        // Clamped rather than trusted: nothing in .NET's regex returns an out-of-range span, but
        // these indices end up addressing a live document, so the guard costs nothing.
        int from = Math.Max(start, 0);
        int to = Math.Min(start + length, paint.Length);
        for (int i = from; i < to; i++)
            paint[i] = value;
    }

    /// <summary>
    /// Turns the per-character painting into the token list the views consume: maximal runs of one
    /// kind, in order, never overlapping, never empty.
    /// <para>
    /// Painting a character array and reading runs back off it, rather than emitting a token per
    /// match and per group, is what makes the overlap impossible by construction. A match token
    /// spanning its own capture groups is not a thing a view can render: <c>CodeView</c> replays
    /// tokens as consecutive runs of text, so the characters covered by both were emitted twice and
    /// the match pane showed <c>abab</c> for a subject of <c>ab</c>. Groups paint over their match
    /// because the group is the more specific statement about a character; the match survives in
    /// the gaps between them, which is exactly the span a reader wants to see distinguished.
    /// </para>
    /// <para>
    /// Zero-length matches (<c>\b</c>, <c>x*</c>) paint nothing and so contribute no token, rather
    /// than the empty spans they used to add.
    /// </para>
    /// </summary>
    private static List<Token> Flatten(Paint[] paint)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < paint.Length)
        {
            var kind = paint[i];
            if (kind == Paint.None)
            {
                i++;
                continue;
            }

            int start = i;
            while (i < paint.Length && paint[i] == kind)
                i++;
            tokens.Add(new Token(start, i - start, kind == Paint.Match ? TokenKind.Match : TokenKind.Group));
        }
        return tokens;
    }
}
