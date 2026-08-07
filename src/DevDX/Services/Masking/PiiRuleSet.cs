using System.Text.RegularExpressions;

namespace DevDX.Services.Masking;

/// <summary>
/// The v1 rule pack (design doc Appendix C): ~25 rules covering contact info, payment, national
/// IDs, network identifiers and developer secrets, plus key-only detection for names/addresses
/// (§15 explains why those can't be detected by value without an NER model DevDX deliberately
/// does not bundle).
/// </summary>
public static class PiiRuleSet
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);

    private static Regex Rx(string pattern, RegexOptions options = RegexOptions.None) =>
        new(pattern, options | RegexOptions.Compiled, Timeout);

    public static IReadOnlyList<PiiRule> Default { get; } = BuildDefault();

    private static IReadOnlyList<PiiRule> BuildDefault() =>
    [
        // ---- Contact -------------------------------------------------------------------
        new("email", PiiCategory.Contact, Rx(@"^(email|e-?mail|mail)$", RegexOptions.IgnoreCase),
            Rx(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b"),
            null, Confidence.Medium, MaskStrategy.PartialKeep),

        new("phone-e164", PiiCategory.Contact, Rx(@"^(phone|mobile|tel|telephone)$", RegexOptions.IgnoreCase),
            Rx(@"\+[1-9]\d{7,14}\b"), null, Confidence.Medium, MaskStrategy.PartialKeep),

        new("phone-nanp", PiiCategory.Contact, Rx(@"^(phone|mobile|tel|telephone)$", RegexOptions.IgnoreCase),
            Rx(@"\(?\b[2-9]\d{2}\)?[-.\s]\d{3}[-.\s]\d{4}\b"), null, Confidence.Medium, MaskStrategy.PartialKeep),

        new("phone-in", PiiCategory.Contact, Rx(@"^(phone|mobile|tel|telephone)$", RegexOptions.IgnoreCase),
            Rx(@"\b[6-9]\d{9}\b"), null, Confidence.Low, MaskStrategy.PartialKeep, "in"),

        // ---- Payment -------------------------------------------------------------------
        new("payment-card", PiiCategory.Payment, Rx(@"^(card|card_?number|cc|pan)$", RegexOptions.IgnoreCase),
            Rx(@"\b(?:\d[ -]?){13,19}\b"),
            v => Validators.Luhn(Digits(v)), Confidence.Medium, MaskStrategy.PartialKeep),

        new("iban", PiiCategory.Payment, Rx(@"^(iban)$", RegexOptions.IgnoreCase),
            Rx(@"\b[A-Z]{2}\d{2}[A-Z0-9]{11,30}\b"),
            Validators.IbanMod97, Confidence.Medium, MaskStrategy.PartialKeep),

        // ---- National ID -----------------------------------------------------------------
        new("us-ssn", PiiCategory.NationalId, Rx(@"^(ssn|social_?security)$", RegexOptions.IgnoreCase),
            Rx(@"\b\d{3}-\d{2}-\d{4}\b"), null, Confidence.Medium, MaskStrategy.Redact, "us"),

        new("in-aadhaar", PiiCategory.NationalId, Rx(@"^(aadhaar|aadhar)$", RegexOptions.IgnoreCase),
            Rx(@"\b\d{4}\s?\d{4}\s?\d{4}\b"),
            v => Validators.Verhoeff(Digits(v)), Confidence.Medium, MaskStrategy.Redact, "in"),

        new("in-pan", PiiCategory.NationalId, Rx(@"^(pan)$", RegexOptions.IgnoreCase),
            Rx(@"\b[A-Z]{5}\d{4}[A-Z]\b"), null, Confidence.Medium, MaskStrategy.Redact, "in"),

        new("in-gstin", PiiCategory.NationalId, Rx(@"^(gstin|gst)$", RegexOptions.IgnoreCase),
            Rx(@"\b\d{2}[A-Z]{5}\d{4}[A-Z]\d[Zz][A-Z\d]\b"), null, Confidence.Medium, MaskStrategy.Redact, "in"),

        new("uk-nino", PiiCategory.NationalId, Rx(@"^(nino|ni_?number)$", RegexOptions.IgnoreCase),
            Rx(@"\b[A-CEGHJ-PR-TW-Z]{2}\d{6}[A-D]\b"), null, Confidence.Medium, MaskStrategy.Redact, "gb"),

        // ---- Network -------------------------------------------------------------------
        new("ipv4", PiiCategory.Network, null,
            Rx(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d{1,2})\.){3}(?:25[0-5]|2[0-4]\d|1?\d{1,2})\b"),
            null, Confidence.Medium, MaskStrategy.Hash),

        new("ipv6", PiiCategory.Network, null,
            Rx(@"\b(?:[A-Fa-f0-9]{1,4}:){2,7}[A-Fa-f0-9]{1,4}\b"),
            null, Confidence.Medium, MaskStrategy.Hash),

        new("mac-address", PiiCategory.Network, null,
            Rx(@"\b(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}\b"),
            null, Confidence.Medium, MaskStrategy.Hash),

        // ---- Secrets -------------------------------------------------------------------
        new("jwt", PiiCategory.Secret, null,
            Rx(@"\bey[A-Za-z0-9_-]{10,}\.ey[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{5,}\b"),
            null, Confidence.High, MaskStrategy.Redact),

        new("aws-access-key", PiiCategory.Secret, null,
            Rx(@"\b(AKIA|ASIA)[A-Z0-9]{16}\b"), null, Confidence.High, MaskStrategy.Redact),

        new("aws-secret-key", PiiCategory.Secret, Rx(@"^(aws_?secret|secret_?access_?key)$", RegexOptions.IgnoreCase),
            Rx(@"\b[A-Za-z0-9/+=]{40}\b"), null, Confidence.Medium, MaskStrategy.Redact),

        new("github-token", PiiCategory.Secret, null,
            Rx(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b"), null, Confidence.High, MaskStrategy.Redact),

        new("slack-token", PiiCategory.Secret, null,
            Rx(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b"), null, Confidence.High, MaskStrategy.Redact),

        new("stripe-key", PiiCategory.Secret, null,
            Rx(@"\b(sk|pk)_(live|test)_[A-Za-z0-9]{16,}\b"), null, Confidence.High, MaskStrategy.Redact),

        new("pem-private-key", PiiCategory.Secret, null,
            Rx(@"-----BEGIN (RSA |EC |)PRIVATE KEY-----[\s\S]+?-----END (RSA |EC |)PRIVATE KEY-----"),
            null, Confidence.High, MaskStrategy.Redact),

        new("connection-string-password", PiiCategory.Secret, Rx(@"^(pwd|password)$", RegexOptions.IgnoreCase),
            null, null, Confidence.Medium, MaskStrategy.Redact),

        new("bearer-token", PiiCategory.Secret, Rx(@"^authorization$", RegexOptions.IgnoreCase),
            Rx(@"\bBearer\s+[A-Za-z0-9\-_.=]{8,}\b"), null, Confidence.High, MaskStrategy.Redact),

        // ---- Location -------------------------------------------------------------------
        new("lat-long", PiiCategory.Location, null,
            Rx(@"-?\d{1,2}\.\d{3,},\s?-?\d{1,3}\.\d{3,}"),
            LooksLikeLatLong, Confidence.Medium, MaskStrategy.Redact),

        // ---- Key-only (§15: names/addresses can't be detected by value without NER) -----
        new("key-name", PiiCategory.KeyOnly, Rx(@"^(name|firstname|first_?name|lastname|last_?name|full_?name)$", RegexOptions.IgnoreCase),
            null, null, Confidence.Medium, MaskStrategy.Redact),
        new("key-address", PiiCategory.KeyOnly, Rx(@"^(address|street|street_?address)$", RegexOptions.IgnoreCase),
            null, null, Confidence.Medium, MaskStrategy.Redact),
        new("key-dob", PiiCategory.KeyOnly, Rx(@"^(dob|date_?of_?birth|birthdate)$", RegexOptions.IgnoreCase),
            null, null, Confidence.Medium, MaskStrategy.Redact),
        new("key-secret", PiiCategory.KeyOnly, Rx(@"^(password|secret|api_?key|apikey|token)$", RegexOptions.IgnoreCase),
            null, null, Confidence.Medium, MaskStrategy.Redact),

        // ---- Low confidence (off by default) --------------------------------------------
        new("bare-10-digit", PiiCategory.KeyOnly, null, Rx(@"\b\d{10}\b"), null, Confidence.Low, MaskStrategy.Redact),
        new("lone-guid", PiiCategory.KeyOnly, null,
            Rx(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b"),
            null, Confidence.Low, MaskStrategy.Redact),
    ];

    private static string Digits(string s) => new(s.Where(char.IsAsciiDigit).ToArray());

    private static bool LooksLikeLatLong(string value)
    {
        var parts = value.Split(',');
        if (parts.Length != 2)
            return false;
        if (!double.TryParse(parts[0].Trim(), out var lat) || !double.TryParse(parts[1].Trim(), out var lon))
            return false;
        return lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
    }
}
