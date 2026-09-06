using System.Text.RegularExpressions;
using dockdev.Services.Formats;

namespace dockdev.Services.Masking;

/// <summary>
/// Finds PII in raw pasted text (design doc §15.1). Detection works directly over the text the
/// user pasted (rather than a re-serialized <c>DataNode</c> tree) so masking can substitute
/// in place and preserve the user's original formatting exactly — every <see cref="Finding"/>'s
/// <see cref="Finding.Start"/>/<see cref="Finding.Length"/> are offsets into that original text.
/// <para>
/// Structure awareness (§15.1's "walks the tree", "classifies per column") is approximated
/// textually: a JSON/XML-shaped key immediately before a match boosts confidence the same way a
/// true structural walk would, and CSV gets a real per-column pass via <see cref="CsvFormat"/>'s
/// own row parser, sampling the header and first rows and then applying the result to every row —
/// exactly the doc's rule. A full DataNode-tree walk is future work; this delivers the same
/// detections for the realistic pretty-printed/minified inputs the tool is used on.
/// </para>
/// </summary>
public static partial class PiiDetector
{
    /// <summary>
    /// Design doc §21: every <see cref="Regex"/> in the app carries an explicit
    /// <see cref="Regex.MatchTimeout"/>. The four patterns below are compiled by the
    /// <c>[GeneratedRegex]</c> source generator rather than held in a plain <c>Regex</c> field, and
    /// that turned out to be exactly the kind of place the rule quietly lapses: the generator emits
    /// each one as a private <em>subclass</em> of <see cref="Regex"/>, so
    /// <c>RegexTimeoutTests</c>' reflective sweep — which only recognises fields declared as
    /// <c>Regex</c> itself — walked straight past them, and they carried no timeout at all. Passed
    /// explicitly here (and the sweep widened to recognise any <see cref="Regex"/>-derived field,
    /// not just an exact match) so a hang here fails the same way every other regex in the app does.
    /// </summary>
    private const int KeyScanTimeoutMs = 500;

    public static List<Finding> Detect(string text, IReadOnlyList<PiiRule> rules, Confidence threshold)
    {
        if (text.Length == 0)
            return [];

        bool csvShaped = FormatRegistry.Csv.DetectConfidence(text) >= 50 &&
                          FormatRegistry.Csv.DetectConfidence(text) >= FormatRegistry.Json.DetectConfidence(text) &&
                          FormatRegistry.Csv.DetectConfidence(text) >= FormatRegistry.Xml.DetectConfidence(text);

        var findings = new List<Finding>();
        findings.AddRange(DetectValuePatterns(text, rules));
        findings.AddRange(csvShaped ? DetectCsvKeyOnly(text, rules) : DetectStructuredKeyOnly(text, rules));

        var resolved = ResolveOverlaps(findings);
        return resolved.Where(f => f.Confidence >= threshold).OrderBy(f => f.Start).ToList();
    }

    // ---- Value-pattern rules (email, phone, cards, secrets, network, low-confidence) --------

    private static IEnumerable<Finding> DetectValuePatterns(string text, IReadOnlyList<PiiRule> rules)
    {
        foreach (var rule in rules)
        {
            if (rule.ValuePattern is null)
                continue;

            List<Match> matches;
            try
            {
                // `Matches()` itself never throws — a MatchCollection is lazy, so it does no
                // scanning at all until enumerated, and a timeout can only fire during that
                // enumeration. Materializing it here, inside the try, is what actually makes this
                // catch reachable; leaving it as `matches = rule.ValuePattern.Matches(text)` looks
                // guarded but lets a real timeout escape from the `foreach` below instead.
                matches = rule.ValuePattern.Matches(text).Cast<Match>().ToList();
            }
            catch (RegexMatchTimeoutException)
            {
                Diag.Log($"PiiDetector: rule '{rule.Id}' timed out — skipped.");
                continue;
            }

            foreach (Match match in matches)
            {
                if (rule.Validator is not null && !SafeValidate(rule.Validator, match.Value))
                    continue; // shape matched but the checksum didn't: not this entity at all

                bool keyMatched = rule.KeyPattern is not null && HasNearbyKey(text, match.Index, rule.KeyPattern);
                var confidence = rule.Validator is not null ? Confidence.High
                    : keyMatched ? Confidence.High
                    : rule.BaseConfidence;

                yield return new Finding(rule.Id, rule.Category, "$", match.Index, match.Length, confidence, rule.DefaultStrategy, Included: true);
            }
        }
    }

    private static bool SafeValidate(Func<string, bool> validator, string value)
    {
        try { return validator(value); }
        catch { return false; }
    }

    /// <summary>Looks in the ~48 characters before a match for a JSON <c>"key":</c> or an XML
    /// <c>key="</c> attribute whose name satisfies <paramref name="keyPattern"/>.</summary>
    private static bool HasNearbyKey(string text, int matchStart, Regex keyPattern)
    {
        int windowStart = Math.Max(0, matchStart - 48);
        var window = text[windowStart..matchStart];

        var jsonKey = JsonKeyBefore().Match(window);
        if (jsonKey.Success && keyPattern.IsMatch(jsonKey.Groups[1].Value))
            return true;

        var attrKey = AttrKeyBefore().Match(window);
        return attrKey.Success && keyPattern.IsMatch(attrKey.Groups[1].Value);
    }

    [GeneratedRegex("\"([A-Za-z0-9_\\-]+)\"\\s*:\\s*\"?$", RegexOptions.None, KeyScanTimeoutMs)]
    private static partial Regex JsonKeyBefore();

    [GeneratedRegex("([A-Za-z0-9_\\-]+)\\s*=\\s*\"?$", RegexOptions.None, KeyScanTimeoutMs)]
    private static partial Regex AttrKeyBefore();

    // ---- Key-only rules (names, addresses, dob, secrets — no value shape to key off) --------

    private static IEnumerable<Finding> DetectStructuredKeyOnly(string text, IReadOnlyList<PiiRule> rules)
    {
        var keyOnlyRules = rules.Where(r => r.Category == PiiCategory.KeyOnly && r.ValuePattern is null && r.KeyPattern is not null).ToList();
        if (keyOnlyRules.Count == 0)
            yield break;

        // Unlike JsonKeyBefore/AttrKeyBefore (a ~48-char window), these two scan the whole
        // document, so a timeout is a real possibility on a large paste — materialized inside the
        // try for the same reason as DetectValuePatterns above: a MatchCollection is lazy, and
        // only enumerating it can actually throw.
        List<Match> jsonMatches;
        try
        {
            jsonMatches = JsonKeyValue().Matches(text).Cast<Match>().ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            Diag.Log("PiiDetector: JSON key/value scan timed out — skipped.");
            jsonMatches = [];
        }

        foreach (Match m in jsonMatches)
        {
            var key = m.Groups[1].Value;
            var rule = keyOnlyRules.FirstOrDefault(r => r.KeyPattern!.IsMatch(key));
            if (rule is null)
                continue;
            var valueGroup = m.Groups[2];
            if (valueGroup.Length == 0)
                continue;
            yield return new Finding(rule.Id, rule.Category, "$." + key, valueGroup.Index, valueGroup.Length, Confidence.Medium, rule.DefaultStrategy, Included: true);
        }

        List<Match> xmlMatches;
        try
        {
            xmlMatches = XmlAttrKeyValue().Matches(text).Cast<Match>().ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            Diag.Log("PiiDetector: XML attribute key/value scan timed out — skipped.");
            xmlMatches = [];
        }

        foreach (Match m in xmlMatches)
        {
            var key = m.Groups[1].Value;
            var rule = keyOnlyRules.FirstOrDefault(r => r.KeyPattern!.IsMatch(key));
            if (rule is null)
                continue;
            var valueGroup = m.Groups[2];
            if (valueGroup.Length == 0)
                continue;
            yield return new Finding(rule.Id, rule.Category, "@" + key, valueGroup.Index, valueGroup.Length, Confidence.Medium, rule.DefaultStrategy, Included: true);
        }
    }

    [GeneratedRegex("\"([A-Za-z0-9_\\-]+)\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.None, KeyScanTimeoutMs)]
    private static partial Regex JsonKeyValue();

    [GeneratedRegex("([A-Za-z0-9_\\-]+)\\s*=\\s*\"([^\"]*)\"", RegexOptions.None, KeyScanTimeoutMs)]
    private static partial Regex XmlAttrKeyValue();

    /// <summary>Per-column CSV classification (design doc §15.1) for key-only rules: the header
    /// row decides a column's classification, and masking then applies to every data row in that
    /// column — the doc's "sampling decides, masking applies to all rows" rule, specialized to
    /// the case where the signal is the column name rather than a sample of its values.</summary>
    private static IEnumerable<Finding> DetectCsvKeyOnly(string text, IReadOnlyList<PiiRule> rules)
    {
        var keyOnlyRules = rules.Where(r => r.Category == PiiCategory.KeyOnly && r.ValuePattern is null && r.KeyPattern is not null).ToList();
        var cellRows = ParseCellsWithOffsets(text);
        if (cellRows.Count < 2 || keyOnlyRules.Count == 0)
            yield break;

        var header = cellRows[0];
        for (int col = 0; col < header.Count; col++)
        {
            var headerText = text.Substring(header[col].Start, header[col].Length);
            var rule = keyOnlyRules.FirstOrDefault(r => r.KeyPattern!.IsMatch(headerText));
            if (rule is null)
                continue;

            for (int row = 1; row < cellRows.Count; row++)
            {
                if (col >= cellRows[row].Count)
                    continue;
                var cell = cellRows[row][col];
                if (cell.Length == 0)
                    continue;
                yield return new Finding(rule.Id, rule.Category, $"csv[{row}].{headerText}", cell.Start, cell.Length, Confidence.Medium, rule.DefaultStrategy, Included: true);
            }
        }
    }

    /// <summary>RFC 4180 field scan that also returns each field's (start, length) in the
    /// original text, mirroring <see cref="CsvFormat"/>'s own parser.</summary>
    private static List<List<(int Start, int Length)>> ParseCellsWithOffsets(string text)
    {
        var rows = new List<List<(int, int)>>();
        int i = 0;
        int n = text.Length;
        while (i < n)
        {
            var row = new List<(int, int)>();
            while (true)
            {
                int fieldStart = i;
                if (i < n && text[i] == '"')
                {
                    i++;
                    while (i < n)
                    {
                        if (text[i] == '"')
                        {
                            if (i + 1 < n && text[i + 1] == '"') { i += 2; continue; }
                            i++;
                            break;
                        }
                        i++;
                    }
                    row.Add((fieldStart + 1, Math.Max(0, i - fieldStart - 2)));
                }
                else
                {
                    while (i < n && text[i] != ',' && text[i] != '\n' && text[i] != '\r')
                        i++;
                    row.Add((fieldStart, i - fieldStart));
                }

                if (i < n && text[i] == ',') { i++; continue; }
                break;
            }
            if (i < n && text[i] == '\r') i++;
            if (i < n && text[i] == '\n') i++;
            rows.Add(row);
        }
        return rows;
    }

    // ---- Overlap resolution ------------------------------------------------------------

    /// <summary>When two rules match overlapping spans, keep the higher-confidence (then longer)
    /// one — e.g. a matched JWT should not also register as three "lone GUID" false positives.</summary>
    private static List<Finding> ResolveOverlaps(List<Finding> findings)
    {
        var ordered = findings.OrderByDescending(f => f.Confidence).ThenByDescending(f => f.Length).ToList();
        var accepted = new List<Finding>();
        foreach (var candidate in ordered)
        {
            bool overlaps = accepted.Any(a => candidate.Start < a.Start + a.Length && a.Start < candidate.Start + candidate.Length);
            if (!overlaps)
                accepted.Add(candidate);
        }
        return accepted;
    }
}
