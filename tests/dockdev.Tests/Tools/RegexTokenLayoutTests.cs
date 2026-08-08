using System.Text;
using System.Text.RegularExpressions;
using dockdev.Services.Syntax;
using Xunit;
using RegexRunner = dockdev.Services.Tools.RegexRunner;

namespace dockdev.Tests.Tools;

/// <summary>
/// <c>RegexRunner</c>'s tokens are handed to <c>CodeView</c>, which walks them in order and emits
/// the span of each one as a run of text. That only reconstructs the subject if the tokens are a
/// <b>non-overlapping cover</b> — the same contract the syntax tokenizers hold to (§10.1). A match
/// token spanning its own capture groups is not one: the overlapping characters get emitted once
/// for the match and again for each group, so the match pane showed <c>abab</c> where the subject
/// said <c>ab</c>.
/// <para>
/// These tests are the contract, expressed as the property that actually matters — replay the
/// tokens the way the view does and get the subject back.
/// </para>
/// </summary>
public class RegexTokenLayoutTests
{
    [Theory]
    // The original bug, at its smallest: one match, two groups, every character double-painted.
    [InlineData(@"(\w)(\w)", "ab")]
    // Nested groups: the inner one overlaps the outer one, which overlaps the match.
    [InlineData(@"((a)b)", "ab")]
    // Groups with literal text between them, so the match has to survive in the gaps.
    [InlineData(@"(\w+)@(\w+)", "contact: ada@example")]
    // Several matches, each with groups.
    [InlineData(@"(\d)(\d)", "12 34 56")]
    // Optional group that does not participate: no token, and no hole in the cover.
    [InlineData(@"(a)(b)?", "a")]
    // Alternation where the group is the whole match.
    [InlineData(@"(cat|dog)", "a cat and a dog")]
    // Zero-length matches: nothing to paint, and nothing to duplicate either.
    [InlineData(@"\b", "one two")]
    [InlineData(@"x*", "abc")]
    // Named groups take the same path as numbered ones.
    [InlineData(@"(?<first>\w+)\s(?<second>\w+)", "hello world")]
    // The whole subject as one match with one group covering it.
    [InlineData(@"^(.*)$", "everything")]
    public void TokensReplayTheSubjectExactly(string pattern, string subject)
    {
        var result = RegexRunner.Run(pattern, subject, RegexOptions.None, null);
        Assert.True(result.Success);
        AssertCovers(result.Tokens, subject);
    }

    [Fact]
    public void GroupsStillReportedSeparatelyFromTokens()
    {
        // Flattening the token layout must not cost the groups table its rows: the two are
        // different outputs of the same run and only one of them is a painting instruction.
        var result = RegexRunner.Run(@"(\w+)@(\w+)", "ada@example", RegexOptions.None, null);

        Assert.Contains(result.Groups, g => g.Value == "ada");
        Assert.Contains(result.Groups, g => g.Value == "example");
        Assert.Contains(result.Groups, g => g.Value == "ada@example"); // group 0, the match itself
    }

    [Fact]
    public void GroupSpansAreStillDistinguishableFromTheirMatch()
    {
        // "ada@example": groups cover the two words, the match covers the '@' between them. The
        // flattening has to keep that distinction — a match painted as one solid block would lose
        // the thing the tester exists to show.
        var result = RegexRunner.Run(@"(\w+)@(\w+)", "ada@example", RegexOptions.None, null);

        Assert.Contains(result.Tokens, t => t.Kind == TokenKind.Group);
        Assert.Contains(result.Tokens, t => t.Kind == TokenKind.Match);

        var atSign = result.Tokens.Single(t => t.Kind == TokenKind.Match);
        Assert.Equal("@", "ada@example"[atSign.Start..atSign.End]);
    }

    [Fact]
    public void MatchWithNoGroupsIsStillOneMatchToken()
    {
        var result = RegexRunner.Run(@"\d+", "abc 123 def", RegexOptions.None, null);
        var token = Assert.Single(result.Tokens);
        Assert.Equal(TokenKind.Match, token.Kind);
        Assert.Equal("123", "abc 123 def"[token.Start..token.End]);
    }

    [Fact]
    public void NoMatches_ProducesNoTokens()
    {
        var result = RegexRunner.Run(@"zzz", "abc", RegexOptions.None, null);
        Assert.True(result.Success);
        Assert.Empty(result.Tokens);
    }

    /// <summary>
    /// The same replay property over generated patterns, since the interesting cases are the ones
    /// nobody thinks to write down. Seeded, so a failure reproduces.
    /// </summary>
    [Fact]
    public void GeneratedPatternsAllReplayExactly()
    {
        string[] patterns =
        [
            @"(\w)(\w)", @"((\w)(\w))", @"(a)|(b)", @"(\w+)?(\d+)?", @"(.)(.)?(.)?",
            @"\b(\w+)\b", @"(?<a>\w)(?<b>\w)?", @"([abc])+", @"(a(b(c)))", @"()", @"(^)|($)",
            @"(\s*)(\S*)", @"((((a))))", @"(?:x)(y)", @"(a)\1?",
        ];
        string[] subjects =
        [
            "", "a", "ab", "abc", "aabbcc", "one two three", "a1 b2 c3", "  spaced  ",
            "aaa", "abab", "x y z", "\n\n", "a\nb", "!@#$%", "aa bb",
        ];

        foreach (var pattern in patterns)
        {
            foreach (var subject in subjects)
            {
                var result = RegexRunner.Run(pattern, subject, RegexOptions.None, null);
                if (!result.Success)
                    continue;
                AssertCovers(result.Tokens, subject, $"pattern={pattern} subject={Quote(subject)}");
            }
        }
    }

    /// <summary>
    /// Replays the tokens the way <c>CodeView.Render</c> does — gap, token, gap, token — and
    /// asserts the result is the subject, character for character. Also pins the structural rules
    /// the view relies on to get there.
    /// </summary>
    private static void AssertCovers(IReadOnlyList<Token> tokens, string subject, string? because = null)
    {
        var context = because is null ? "" : " (" + because + ")";

        int cursor = 0;
        foreach (var token in tokens)
        {
            Assert.True(token.Length > 0, $"Empty token at {token.Start}{context}");
            Assert.True(token.Start >= cursor,
                $"Token at {token.Start} overlaps cursor {cursor}{context}");
            Assert.True(token.End <= subject.Length,
                $"Token [{token.Start},{token.End}) runs past length {subject.Length}{context}");
            cursor = token.End;
        }

        var replayed = new StringBuilder();
        int pos = 0;
        foreach (var token in tokens)
        {
            replayed.Append(subject, pos, token.Start - pos);
            replayed.Append(subject, token.Start, token.Length);
            pos = token.End;
        }
        replayed.Append(subject, pos, subject.Length - pos);

        Assert.Equal(subject, replayed.ToString());
    }

    private static string Quote(string text) =>
        "\"" + text.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
