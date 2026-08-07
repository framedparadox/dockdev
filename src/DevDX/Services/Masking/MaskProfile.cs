namespace DevDX.Services.Masking;

/// <summary>
/// A named bundle of enabled rules and per-category strategies (design doc §15.3): a team
/// standardises on one, exports it as JSON, and imports it elsewhere.
/// </summary>
public sealed record MaskProfile(string Name, Confidence Threshold, IReadOnlyDictionary<PiiCategory, MaskStrategy> CategoryStrategy)
{
    public MaskStrategy StrategyFor(PiiCategory category) =>
        CategoryStrategy.TryGetValue(category, out var strategy) ? strategy : MaskStrategy.Redact;

    /// <summary>Tokenizes contact/national-id/network identifiers so masked data still joins, and
    /// redacts secrets outright — the profile for handing a repro to someone else.</summary>
    public static MaskProfile ShareABugReport { get; } = new("Share a bug report", Confidence.Medium, new Dictionary<PiiCategory, MaskStrategy>
    {
        [PiiCategory.Contact] = MaskStrategy.Pseudonymize,
        [PiiCategory.NationalId] = MaskStrategy.Pseudonymize,
        [PiiCategory.Network] = MaskStrategy.Pseudonymize,
        [PiiCategory.Location] = MaskStrategy.Pseudonymize,
        [PiiCategory.Payment] = MaskStrategy.Redact,
        [PiiCategory.Secret] = MaskStrategy.Redact,
        [PiiCategory.KeyOnly] = MaskStrategy.Redact,
    });

    /// <summary>Hashes everything so identical values still correlate across log lines, keeping
    /// shape without keeping the value.</summary>
    public static MaskProfile Logs { get; } = new("Logs", Confidence.Medium, new Dictionary<PiiCategory, MaskStrategy>
    {
        [PiiCategory.Contact] = MaskStrategy.Hash,
        [PiiCategory.NationalId] = MaskStrategy.Hash,
        [PiiCategory.Network] = MaskStrategy.Hash,
        [PiiCategory.Location] = MaskStrategy.Hash,
        [PiiCategory.Payment] = MaskStrategy.Hash,
        [PiiCategory.Secret] = MaskStrategy.Redact,
        [PiiCategory.KeyOnly] = MaskStrategy.Hash,
    });

    /// <summary>Redacts every finding, including Low confidence — maximum safety, most false
    /// positives.</summary>
    public static MaskProfile Strict { get; } = new("Strict", Confidence.Low, Enum.GetValues<PiiCategory>()
        .ToDictionary(c => c, _ => MaskStrategy.Redact));

    public static IReadOnlyList<MaskProfile> BuiltIn { get; } = [ShareABugReport, Logs, Strict];
}
