using DevDX.Services.Tools;
using Xunit;

namespace DevDX.Tests.Tools;

public class CronToolsTests
{
    private static DateTime Anchor => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public void EveryFifteenMinutes_FiresOnTheQuarterHours()
    {
        var schedule = CronTools.Parse("*/15 * * * *");
        Assert.True(schedule.Success);

        // Strictly after the anchor, so the 00:00 firing at the anchor itself is not included.
        var next = CronTools.NextOccurrences(schedule, Anchor, 4);
        Assert.Equal([15, 30, 45, 0], next.Select(o => o.Minute));
    }

    [Fact]
    public void WeekdayRange_SkipsTheWeekend()
    {
        // 2026-01-01 is a Thursday, and 09:00 that day is still ahead of the midnight anchor.
        var schedule = CronTools.Parse("0 9 * * mon-fri");
        var next = CronTools.NextOccurrences(schedule, Anchor, 2);

        Assert.Equal(DayOfWeek.Thursday, next[0].DayOfWeek);
        Assert.Equal(DayOfWeek.Friday, next[1].DayOfWeek);
        Assert.All(next, o => Assert.Equal(9, o.Hour));
    }

    [Fact]
    public void DayOfMonthAndDayOfWeek_BothRestricted_MatchEither()
    {
        // Cron's documented oddity: "1st of the month OR any Monday", not "1st and a Monday".
        var schedule = CronTools.Parse("0 0 1 * mon");
        // Enough occurrences to reach past March's Mondays and into 1 April, a Wednesday.
        var next = CronTools.NextOccurrences(schedule, new DateTime(2026, 3, 2, 12, 0, 0), 6);

        Assert.Contains(next, o => o.Day == 1 && o.DayOfWeek != DayOfWeek.Monday);
        Assert.Contains(next, o => o.DayOfWeek == DayOfWeek.Monday && o.Day != 1);
    }

    [Fact]
    public void Macros_ExpandToTheirFiveFieldForm()
    {
        var next = CronTools.NextOccurrences(CronTools.Parse("@daily"), Anchor, 2);
        Assert.All(next, o => Assert.Equal(new TimeSpan(0, 0, 0), o.TimeOfDay));
        Assert.Equal(1, (next[1] - next[0]).Days);
    }

    [Fact]
    public void ImpossibleDate_TerminatesWithNoOccurrences()
    {
        // 30 February: the search must give up rather than walk forward forever.
        var schedule = CronTools.Parse("0 0 30 2 *");
        Assert.True(schedule.Success);
        Assert.Empty(CronTools.NextOccurrences(schedule, Anchor, 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("99 * * * *")]
    [InlineData("nope * * * *")]
    [InlineData("*/0 * * * *")]
    public void MalformedExpressions_FailWithAMessage(string expression)
    {
        var schedule = CronTools.Parse(expression);
        Assert.False(schedule.Success);
        Assert.NotEmpty(schedule.Error);
    }
}

public class ColorToolsTests
{
    [Theory]
    [InlineData("#2D7FF9")]
    [InlineData("2d7ff9")]
    [InlineData("rgb(45, 127, 249)")]
    [InlineData("  rgb(45,127,249)  ")]
    public void EquivalentNotations_ParseToTheSameColour(string text)
    {
        Assert.True(ColorTools.TryParse(text, out var color));
        Assert.Equal(new Rgba(45, 127, 249, 255), color);
    }

    [Fact]
    public void ShorthandHex_ExpandsEachDigit()
    {
        Assert.True(ColorTools.TryParse("#abc", out var color));
        Assert.Equal(new Rgba(0xAA, 0xBB, 0xCC, 255), color);
    }

    [Fact]
    public void EightDigitHex_KeepsAlpha()
    {
        Assert.True(ColorTools.TryParse("#11223380", out var color));
        Assert.Equal((byte)0x80, color.A);
        Assert.Equal("#11223380", color.ToHex());
    }

    [Fact]
    public void HslRoundTrip_ReturnsTheOriginalColour()
    {
        var original = new Rgba(45, 127, 249, 255);
        var (h, s, l) = ColorTools.ToHsl(original);
        Assert.Equal(original, ColorTools.FromHsl(h, s, l));
    }

    [Fact]
    public void HsvRoundTrip_ReturnsTheOriginalColour()
    {
        var original = new Rgba(200, 30, 90, 255);
        var (h, s, v) = ColorTools.ToHsv(original);
        Assert.Equal(original, ColorTools.FromHsv(h, s, v));
    }

    [Fact]
    public void ContrastRatio_BlackOnWhite_IsTwentyOne()
    {
        var ratio = ColorTools.ContrastRatio(new Rgba(0, 0, 0, 255), new Rgba(255, 255, 255, 255));
        Assert.Equal(21.0, ratio, 2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("burgundy")]
    [InlineData("#12345")]
    [InlineData("rgb(1, 2)")]
    public void UnrecognisedInput_IsRejectedRatherThanGuessed(string text) =>
        Assert.False(ColorTools.TryParse(text, out _));
}

public class PasswordToolsTests
{
    [Fact]
    public void Generate_HonoursLengthAndCharacterClasses()
    {
        var options = new PasswordOptions(32, Lowercase: true, Uppercase: false, Digits: false, Symbols: false);
        var password = PasswordTools.Generate(options);

        Assert.Equal(32, password.Length);
        Assert.All(password, c => Assert.InRange(c, 'a', 'z'));
    }

    [Fact]
    public void ExcludeAmbiguous_DropsTheLookAlikeCharacters()
    {
        var options = new PasswordOptions(400, Symbols: false, ExcludeAmbiguous: true);
        var password = PasswordTools.Generate(options);
        Assert.DoesNotContain(password, c => "Il1O0o".Contains(c));
    }

    [Fact]
    public void NoCharacterClasses_YieldsNothingRatherThanAWeakDefault()
    {
        var options = new PasswordOptions(20, false, false, false, false);
        Assert.Equal("", PasswordTools.Generate(options));
        Assert.Equal(0, PasswordTools.EntropyBits(options));
    }

    [Fact]
    public void EntropyBits_IsLengthTimesLogOfTheAlphabet()
    {
        // 26 lowercase letters ≈ 4.7 bits each.
        var bits = PasswordTools.EntropyBits(new PasswordOptions(10, true, false, false, false));
        Assert.Equal(10 * Math.Log2(26), bits, 6);
    }

    [Fact]
    public void GenerateMany_ProducesDistinctSecrets()
    {
        var passwords = PasswordTools.GenerateMany(new PasswordOptions(24), 50);
        Assert.Equal(50, passwords.Count);
        Assert.Equal(50, passwords.Distinct().Count());
    }
}

public class LoremToolsTests
{
    [Fact]
    public void Words_ReturnsExactlyThatManyWords()
    {
        var text = LoremTools.Generate(LoremUnit.Words, 12);
        Assert.Equal(12, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Paragraphs_AreSeparatedByABlankLine()
    {
        var text = LoremTools.Generate(LoremUnit.Paragraphs, 3);
        Assert.Equal(3, text.Split("\n\n").Length);
    }

    [Fact]
    public void ClassicOpening_StartsWithTheCanonicalPhrase()
    {
        Assert.StartsWith("Lorem ipsum dolor sit amet", LoremTools.Generate(LoremUnit.Sentences, 2));
        Assert.DoesNotContain("\n", LoremTools.Generate(LoremUnit.Sentences, 2));
    }

    [Fact]
    public void CountIsClamped_SoAWildNumberCantHangTheUi() =>
        Assert.NotEmpty(LoremTools.Generate(LoremUnit.Words, int.MaxValue));
}
