using DevDX.Services.Masking;
using Xunit;

namespace DevDX.Tests.Masking;

public class ValidatorsTests
{
    [Theory]
    [InlineData("4111111111111111", true)]  // well-known Luhn-valid test number
    [InlineData("4111111111111112", false)]
    [InlineData("", false)]
    public void Luhn_ValidatesCheckDigit(string digits, bool expected) =>
        Assert.Equal(expected, Validators.Luhn(digits));

    [Theory]
    [InlineData("GB82WEST12345698765432", true)]  // canonical IBAN example
    [InlineData("GB82WEST12345698765431", false)]
    public void IbanMod97_ValidatesCheckDigits(string iban, bool expected) =>
        Assert.Equal(expected, Validators.IbanMod97(iban));

    [Fact]
    public void Verhoeff_ExactlyOneCheckDigitValidatesAGivenPrefix()
    {
        // Rather than depending on a memorized "known good" Verhoeff number, prove the algorithm's
        // own invariant: exactly one of the ten possible trailing check digits validates.
        const string prefix = "123456789012345";
        var validDigits = Enumerable.Range(0, 10).Where(d => Validators.Verhoeff(prefix + d)).ToList();
        Assert.Single(validDigits);
    }

    [Fact]
    public void Verhoeff_DetectsAdjacentTransposition()
    {
        const string prefix = "123456789012345";
        int checkDigit = Enumerable.Range(0, 10).First(d => Validators.Verhoeff(prefix + d));
        string valid = prefix + checkDigit;

        var chars = valid.ToCharArray();
        (chars[0], chars[1]) = (chars[1], chars[0]);
        string transposed = new(chars);

        if (transposed != valid)
            Assert.False(Validators.Verhoeff(transposed));
    }
}
