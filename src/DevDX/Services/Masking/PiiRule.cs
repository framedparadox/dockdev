using System.Text.RegularExpressions;

namespace DevDX.Services.Masking;

public enum PiiCategory { Contact, Payment, NationalId, Network, Secret, Location, KeyOnly }

public enum Confidence { Low, Medium, High }

/// <summary>
/// How a finding is neutralised (design doc §15.2). Named <c>Pseudonymize</c>, not
/// <c>Tokenize</c>, so it can never be confused with §10's span-colouring tokenizer.
/// </summary>
public enum MaskStrategy { Redact, PartialKeep, Hash, Pseudonymize, FormatPreservingFake, Nullify, Truncate }

/// <summary>One detection rule (design doc §15.1). Every <see cref="Regex"/> here is constructed
/// with an explicit <see cref="Regex.MatchTimeout"/> — the rule pack is data, so a malformed rule
/// degrades to "this rule timed out, skipped", never a hung window.</summary>
public sealed record PiiRule(
    string Id,
    PiiCategory Category,
    Regex? KeyPattern,
    Regex? ValuePattern,
    Func<string, bool>? Validator,
    Confidence BaseConfidence,
    MaskStrategy DefaultStrategy,
    string? Locale = null);

public sealed record Finding(
    string RuleId, PiiCategory Category, string Path,
    int Start, int Length, Confidence Confidence,
    MaskStrategy Strategy, bool Included);
