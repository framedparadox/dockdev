namespace DevDX.Services.Masking;

/// <summary>Checksum validators that turn a value-pattern match into High confidence (design doc
/// §15.1): a shape match alone is Medium; a shape match whose checksum also passes is High.</summary>
public static class Validators
{
    /// <summary>Luhn mod-10 — payment cards.</summary>
    public static bool Luhn(string digitsOnly)
    {
        if (digitsOnly.Length == 0)
            return false;
        int sum = 0;
        bool alternate = false;
        for (int i = digitsOnly.Length - 1; i >= 0; i--)
        {
            if (!char.IsAsciiDigit(digitsOnly[i]))
                return false;
            int d = digitsOnly[i] - '0';
            if (alternate)
            {
                d *= 2;
                if (d > 9)
                    d -= 9;
            }
            sum += d;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }

    /// <summary>IBAN mod-97 (ISO 7064), per the standard's own check: move the first four
    /// characters to the end, convert letters to numbers (A=10..Z=35), and the result mod 97 must
    /// be 1.</summary>
    public static bool IbanMod97(string iban)
    {
        iban = iban.Replace(" ", "").ToUpperInvariant();
        if (iban.Length < 8)
            return false;
        var rearranged = iban[4..] + iban[..4];
        var sb = new System.Text.StringBuilder();
        foreach (char c in rearranged)
        {
            if (char.IsAsciiDigit(c))
                sb.Append(c);
            else if (c is >= 'A' and <= 'Z')
                sb.Append(c - 'A' + 10);
            else
                return false;
        }

        // mod 97 over a huge numeral string, done in chunks so it fits in a long.
        var digits = sb.ToString();
        int remainder = 0;
        foreach (char c in digits)
        {
            remainder = (remainder * 10 + (c - '0')) % 97;
        }
        return remainder == 1;
    }

    /// <summary>Verhoeff checksum — used by India's Aadhaar number.</summary>
    public static bool Verhoeff(string digitsOnly)
    {
        if (digitsOnly.Length == 0 || !digitsOnly.All(char.IsAsciiDigit))
            return false;

        int c = 0;
        var reversed = digitsOnly.Reverse().ToArray();
        for (int i = 0; i < reversed.Length; i++)
        {
            int digit = reversed[i] - '0';
            c = D[c, P[i % 8, digit]];
        }
        return c == 0;
    }

    private static readonly int[,] D =
    {
        {0,1,2,3,4,5,6,7,8,9}, {1,2,3,4,0,6,7,8,9,5}, {2,3,4,0,1,7,8,9,5,6}, {3,4,0,1,2,8,9,5,6,7},
        {4,0,1,2,3,9,5,6,7,8}, {5,9,8,7,6,0,4,3,2,1}, {6,5,9,8,7,1,0,4,3,2}, {7,6,5,9,8,2,1,0,4,3},
        {8,7,6,5,9,3,2,1,0,4}, {9,8,7,6,5,4,3,2,1,0},
    };

    private static readonly int[,] P =
    {
        {0,1,2,3,4,5,6,7,8,9}, {1,5,7,6,2,8,3,0,9,4}, {5,8,0,3,7,9,6,1,4,2}, {8,9,1,6,0,4,3,5,2,7},
        {9,4,5,3,1,2,6,8,7,0}, {4,2,8,6,5,7,3,9,0,1}, {2,7,9,3,8,0,6,4,1,5}, {7,0,4,6,9,1,3,2,5,8},
    };
}
