using System.Security.Cryptography;
using System.Text;
using dockdev.Services.Syntax;

namespace dockdev.Services.Masking;

public sealed record MaskOutput(string Text, IReadOnlyList<Token> MaskedSpans);

/// <summary>
/// Applies mask strategies to produce the masked output (design doc §15.2). Pseudonyms are
/// stable within (and across, since state lives on the instance for a session) documents so
/// masked data still joins — "the differentiator against every web masker".
/// </summary>
public sealed class Masker
{
    private readonly Dictionary<string, string> _pseudonyms = new();
    private readonly Dictionary<PiiCategory, int> _pseudonymCounters = new();
    private readonly string _sessionSalt = Guid.NewGuid().ToString("N");
    private readonly string? _userSalt;

    public Masker(string? userSalt = null) => _userSalt = userSalt;

    public MaskOutput BuildOutput(string originalText, IReadOnlyList<Finding> findings)
    {
        var included = findings.Where(f => f.Included).OrderBy(f => f.Start).ToList();
        var sb = new StringBuilder();
        var spans = new List<Token>();
        int cursor = 0;

        foreach (var finding in included)
        {
            if (finding.Start < cursor)
                continue; // an overlap slipped through; never mask the same span twice
            sb.Append(originalText, cursor, finding.Start - cursor);
            var original = originalText.Substring(finding.Start, finding.Length);
            var masked = Apply(finding, original);
            spans.Add(new Token(sb.Length, masked.Length, TokenKind.Masked));
            sb.Append(masked);
            cursor = finding.Start + finding.Length;
        }
        sb.Append(originalText, cursor, originalText.Length - cursor);
        return new MaskOutput(sb.ToString(), spans);
    }

    private string Apply(Finding finding, string original) => finding.Strategy switch
    {
        MaskStrategy.Redact => "***",
        MaskStrategy.PartialKeep => PartialKeep(original),
        MaskStrategy.Hash => Hash(original),
        MaskStrategy.Pseudonymize => Pseudonymize(finding, original),
        MaskStrategy.FormatPreservingFake => FormatPreservingFake(finding, original),
        MaskStrategy.Nullify => "null",
        MaskStrategy.Truncate => Truncate(original),
        _ => "***",
    };

    private static string PartialKeep(string value)
    {
        int at = value.IndexOf('@');
        if (at > 0)
        {
            var local = value[..at];
            var domain = value[at..];
            return local[0] + new string('*', Math.Max(1, local.Length - 1)) + domain;
        }
        var digitsOnly = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (digitsOnly.Length >= 4)
            return new string('*', Math.Max(0, value.Length - 4)) + value[^4..];
        return new string('*', value.Length);
    }

    private string Hash(string value)
    {
        var salt = _userSalt ?? _sessionSalt;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(salt + value));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    private string Pseudonymize(Finding finding, string value)
    {
        var key = finding.Category + "|" + value;
        if (_pseudonyms.TryGetValue(key, out var existing))
            return existing;
        int n = _pseudonymCounters.GetValueOrDefault(finding.Category) + 1;
        _pseudonymCounters[finding.Category] = n;
        var label = $"{finding.Category.ToString().ToUpperInvariant()}_{n}";
        _pseudonyms[key] = label;
        return label;
    }

    private static string FormatPreservingFake(Finding finding, string original) => finding.RuleId switch
    {
        "payment-card" => FakeCard(),
        // (uint) cast, not Math.Abs: Math.Abs(int.MinValue) throws OverflowException, and GetHashCode
        // can legitimately return int.MinValue.
        "email" => $"user{(uint)original.GetHashCode() % 10_000}@example.com",
        _ => new string('*', original.Length),
    };

    private static string FakeCard()
    {
        // A well-known test BIN range (never a real issued number), with a computed Luhn check
        // digit so downstream "looks like a card" validators stay happy (design doc §15.2).
        var rnd = Random.Shared;
        var body = "41111111111" + rnd.Next(0, 10) + rnd.Next(0, 10) + rnd.Next(0, 10) + rnd.Next(0, 10);
        for (int check = 0; check <= 9; check++)
            if (Validators.Luhn(body + check))
                return body + check;
        return body + "0";
    }

    private static string Truncate(string value) => value.Length <= 8 ? value : value[..8] + "…";
}
