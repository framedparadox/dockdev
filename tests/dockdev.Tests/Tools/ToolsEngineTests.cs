using System.Text;
using System.Text.RegularExpressions;
using dockdev.Services.Text;
using dockdev.Services.Tools;
using Xunit;
using RegexRunner = dockdev.Services.Tools.RegexRunner;

namespace dockdev.Tests.Tools;

public class Base64ToolsTests
{
    [Fact]
    public void EncodeThenDecode_RoundTrips()
    {
        var bytes = Encoding.UTF8.GetBytes("Hello, dockdev!");
        var encoded = Base64Tools.Encode(bytes, urlSafe: false, mime76: false);
        Assert.True(Base64Tools.TryDecode(encoded, strict: false, out var decoded, out _));
        Assert.Equal(bytes, decoded);
    }

    [Fact]
    public void UrlSafeEncoding_HasNoPlusOrSlash()
    {
        var bytes = new byte[] { 0xFB, 0xFF, 0xBF };
        var encoded = Base64Tools.Encode(bytes, urlSafe: true, mime76: false);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
    }

    [Fact]
    public void InvalidBase64_ReportsErrorInsteadOfThrowing()
    {
        Assert.False(Base64Tools.TryDecode("not valid base64!!!", strict: true, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void SniffImage_RecognizesPngMagicBytes()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0];
        Assert.Equal(ImageKind.Png, Base64Tools.SniffImage(png));
    }
}

public class HashToolsTests
{
    [Fact]
    public void Crc32_MatchesKnownVector()
    {
        // The canonical CRC-32 (IEEE 802.3) test vector for the ASCII string "123456789".
        var crc = Crc32.Compute(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal(0xCBF43926u, crc);
    }

    [Fact]
    public void Sha256_MatchesKnownVector()
    {
        // The well-known SHA-256 digest of the empty string.
        var hash = HashTools.Compute("SHA-256", Encoding.ASCII.GetBytes(""));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash);
    }

    [Fact]
    public void ConstantTimeEquals_IsCaseInsensitiveAndTrimmed()
    {
        Assert.True(HashTools.ConstantTimeEquals(" ABC123 ", "abc123"));
        Assert.False(HashTools.ConstantTimeEquals("abc123", "abc124"));
    }
}

public class UuidToolsTests
{
    [Fact]
    public void V7_IsTimeOrderedAcrossGenerations()
    {
        // v7 randomizes its tail, so strict monotonicity within one millisecond is not
        // guaranteed (design doc §24) — only that successive batches trend upward.
        var first = UuidTools.Generate(UuidVersion.V7);
        Thread.Sleep(5);
        var second = UuidTools.Generate(UuidVersion.V7);
        Assert.True(string.CompareOrdinal(first.ToString("N")[..12], second.ToString("N")[..12]) <= 0);
    }

    [Fact]
    public void Nil_IsAllZeroes() => Assert.Equal(Guid.Empty, UuidTools.Generate(UuidVersion.Nil));

    [Fact]
    public void GenerateMany_ClampsToReasonableCount()
    {
        var many = UuidTools.GenerateMany(UuidVersion.V4, UuidFormat.Hyphenated, 100_000);
        Assert.Equal(10_000, many.Count);
    }
}

public class TimestampToolsTests
{
    [Theory]
    [InlineData(1700000000L, EpochUnit.Seconds)]        // 10 digits
    [InlineData(1700000000000L, EpochUnit.Milliseconds)] // 13 digits
    public void DetectUnit_UsesMagnitude(long value, EpochUnit expected) =>
        Assert.Equal(expected, TimestampTools.DetectUnit(value));

    [Fact]
    public void FromEpochSeconds_RoundTripsToEpoch()
    {
        var when = TimestampTools.FromEpoch(1700000000, EpochUnit.Seconds);
        Assert.Equal(1700000000, TimestampTools.ToEpoch(when, EpochUnit.Seconds));
    }

    [Fact]
    public void NegativeEpoch_BeforeUnixEpoch_DoesNotThrow()
    {
        var when = TimestampTools.FromEpoch(-100000, EpochUnit.Seconds);
        Assert.True(when.Year < 1970);
    }
}

public class NumberBaseToolsTests
{
    [Fact]
    public void Mask_TruncatesToWidth()
    {
        Assert.Equal(0xFFUL, NumberBaseTools.Mask(0x1FF, 8));
    }

    [Fact]
    public void SignExtend_Interprets8BitAsNegative()
    {
        Assert.Equal(-1, NumberBaseTools.SignExtend(0xFF, 8));
    }

    [Fact]
    public void TryParse_HexAndBinary()
    {
        Assert.True(NumberBaseTools.TryParse("FF", 16, out var hex));
        Assert.Equal(255, hex);
        Assert.True(NumberBaseTools.TryParse("1010", 2, out var bin));
        Assert.Equal(10, bin);
    }

    [Fact]
    public void BitwiseOps_MatchExpectedResults()
    {
        Assert.Equal(0b1100UL, NumberBaseTools.Or(0b1000, 0b0100));
        Assert.Equal(0b1000UL, NumberBaseTools.And(0b1100, 0b1001));
        Assert.Equal(0b0110UL, NumberBaseTools.Xor(0b1100, 0b1010));
    }
}

public class MyersDiffTests
{
    [Fact]
    public void IdenticalSequences_ProduceOnlyEqualOps()
    {
        var ops = MyersDiff.Diff(["a", "b", "c"], ["a", "b", "c"]);
        Assert.All(ops, op => Assert.Equal(ChangeKind.Equal, op.Kind));
    }

    [Fact]
    public void SingleLineChange_ProducesMinimalEdit()
    {
        var ops = MyersDiff.Diff(["a", "b", "c"], ["a", "x", "c"]);
        Assert.Contains(ops, o => o.Kind == ChangeKind.Removed && o.Value == "b");
        Assert.Contains(ops, o => o.Kind == ChangeKind.Added && o.Value == "x");
        Assert.Equal(2, ops.Count(o => o.Kind == ChangeKind.Equal));
    }

    [Fact]
    public void ReconstructingFromOps_RecoversBothSequences()
    {
        var a = new List<string> { "line1", "line2", "line3" };
        var b = new List<string> { "line1", "lineTWO", "line3", "line4" };
        var ops = MyersDiff.Diff(a, b);

        var recoveredA = ops.Where(o => o.Kind != ChangeKind.Added).Select(o => o.Value).ToList();
        var recoveredB = ops.Where(o => o.Kind != ChangeKind.Removed).Select(o => o.Value).ToList();
        Assert.Equal(a, recoveredA);
        Assert.Equal(b, recoveredB);
    }
}

public class RegexRunnerTests
{
    [Fact]
    public void CatastrophicBacktracking_TimesOutInsteadOfHanging()
    {
        var result = RegexRunner.Run("(a+)+$", new string('a', 40) + "!", RegexOptions.None, null);
        Assert.True(result.TimedOut || !result.Success);
    }

    [Fact]
    public void InvalidPattern_ReportsErrorInsteadOfThrowing()
    {
        var result = RegexRunner.Run("(unterminated", "subject", RegexOptions.None, null);
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ValidPattern_FindsMatchesAndGroups()
    {
        var result = RegexRunner.Run(@"(\w+)@(\w+)", "contact: ada@example", RegexOptions.None, null);
        Assert.True(result.Success);
        Assert.NotEmpty(result.Tokens);
        Assert.Contains(result.Groups, g => g.Value == "ada");
    }
}

public class TextEngineTests
{
    [Theory]
    [InlineData("hello world", "helloWorld")]
    [InlineData("Hello-World_Test", "helloWorldTest")]
    public void CaseConvert_ToCamelCase(string input, string expected) =>
        Assert.Equal(expected, CaseConvert.ToCamelCase(input));

    [Fact]
    public void CaseConvert_ToSnakeCase() =>
        Assert.Equal("hello_world", CaseConvert.ToSnakeCase("HelloWorld"));

    [Fact]
    public void EscapeCodecs_JsonRoundTrips()
    {
        const string original = "line1\nline2\t\"quoted\"";
        var escaped = EscapeCodecs.JsonEscape(original);
        Assert.Equal(original, EscapeCodecs.JsonUnescape(escaped));
    }

    [Fact]
    public void LineOps_SortNatural_OrdersNumericSuffixesByValue()
    {
        var sorted = LineOps.SortLines("item10\nitem2\nitem1", natural: true, descending: false);
        Assert.Equal("item1\nitem2\nitem10", sorted);
    }

    [Fact]
    public void LineOps_Dedupe_RemovesRepeatsPreservingOrder()
    {
        Assert.Equal("a\nb\nc", LineOps.Dedupe("a\nb\na\nc\nb"));
    }
}

public class UrlToolsTests
{
    [Fact]
    public void Rfc3986_EncodesSpaceAsPercent20()
    {
        Assert.Equal("a%20b", UrlTools.EncodeRfc3986("a b"));
    }

    [Fact]
    public void FormEncoding_EncodesSpaceAsPlus()
    {
        Assert.Equal("a+b", UrlTools.EncodeForm("a b"));
    }

    [Fact]
    public void TryParse_ExtractsQueryParameters()
    {
        Assert.True(UrlTools.TryParse("https://example.com/path?x=1&y=2#frag", out var parsed));
        Assert.Equal("example.com", parsed.Host);
        Assert.Equal("/path", parsed.Path);
        Assert.Equal("frag", parsed.Fragment);
        Assert.Contains(parsed.Query, q => q.Key == "x" && q.Value == "1");
    }
}
