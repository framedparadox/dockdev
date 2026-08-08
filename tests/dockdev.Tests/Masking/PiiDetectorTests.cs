using dockdev.Services.Masking;
using Xunit;

namespace dockdev.Tests.Masking;

/// <summary>
/// Design doc §15.4: tests assert in both directions — recall against a positives corpus, and
/// zero false positives against a negatives corpus.
/// </summary>
public class PiiDetectorTests
{
    private static List<Finding> Detect(string text, Confidence threshold = Confidence.Medium) =>
        PiiDetector.Detect(text, PiiRuleSet.Default, threshold);

    // ---- Positives: must be found ---------------------------------------------------------

    [Fact]
    public void FindsEmailAddress()
    {
        var findings = Detect("""{"contact": "ada.lovelace@example.com"}""");
        Assert.Contains(findings, f => f.RuleId == "email");
    }

    [Fact]
    public void FindsPaymentCard_WithLuhnValidation()
    {
        var findings = Detect("""{"card": "4111111111111111"}""");
        Assert.Contains(findings, f => f.RuleId == "payment-card" && f.Confidence == Confidence.High);
    }

    [Fact]
    public void FindsAwsAccessKey()
    {
        var findings = Detect("AKIAIOSFODNN7EXAMPLE");
        Assert.Contains(findings, f => f.RuleId == "aws-access-key");
    }

    [Fact]
    public void FindsJwt()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        var findings = Detect(jwt);
        Assert.Contains(findings, f => f.RuleId == "jwt");
    }

    [Fact]
    public void FindsUsSsn()
    {
        var findings = Detect("""{"ssn": "123-45-6789"}""");
        Assert.Contains(findings, f => f.RuleId == "us-ssn");
    }

    [Fact]
    public void KeyOnlyRule_FindsNameByKeyAlone()
    {
        var findings = Detect("""{"firstName": "Ada"}""");
        Assert.Contains(findings, f => f.RuleId == "key-name");
    }

    [Fact]
    public void CsvColumn_ClassifiesEveryRowFromHeaderAlone()
    {
        // Design doc §15.1: sampling (here, the header) decides the column; masking then applies
        // to every row in that column, not just the sampled ones.
        var findings = Detect("name,age\nAda,30\nGrace,85\nCharles,45", Confidence.Medium);
        var nameFindings = findings.Where(f => f.RuleId == "key-name").ToList();
        Assert.Equal(3, nameFindings.Count);
    }

    // ---- Negatives: must NOT be found (zero false positives) -----------------------------

    [Fact]
    public void VersionString_IsNotFlaggedAsIpv4()
    {
        var findings = Detect("""{"version": "1.2.3"}""");
        Assert.DoesNotContain(findings, f => f.RuleId == "ipv4");
    }

    [Fact]
    public void OrderId_IsNotFlaggedAsPaymentCard()
    {
        // A 16-digit-shaped order id that fails Luhn must not register as a card at all.
        var findings = Detect("""{"orderId": "1234567890123456"}""");
        Assert.DoesNotContain(findings, f => f.RuleId == "payment-card");
    }

    [Fact]
    public void SessionIdGuid_IsNotMaskedAtDefaultThreshold()
    {
        var findings = Detect("""{"sessionId": "550e8400-e29b-41d4-a716-446655440000"}""", Confidence.Medium);
        Assert.DoesNotContain(findings, f => f.RuleId == "lone-guid");
    }

    [Fact]
    public void PlainProseWithNoIdentifiers_ProducesNoFindings()
    {
        var findings = Detect("The quick brown fox jumps over the lazy dog near the river bank.");
        Assert.Empty(findings);
    }
}
