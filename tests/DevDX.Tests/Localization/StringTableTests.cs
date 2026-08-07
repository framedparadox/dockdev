using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace DevDX.Tests.Localization;

/// <summary>
/// Design doc §20/§23: the string tables are eight flat JSON dictionaries with English as the
/// fallback, and the key set is meant to be verified rather than trusted — "every language file
/// carries the same key set, verified in CI". This is that verification, plus the checks that make
/// it worth having: every table well-formed and non-blank, every key the app asks for present, and
/// <c>{0}</c>-style placeholders matching English so <see cref="string.Format"/> can never be handed
/// an argument list the translation does not use.
/// <para>
/// The parity assertion is exact in both directions on purpose. A key English lacks can never be
/// reached, because every lookup falls back through English — it is a typo or a leftover, and the
/// user sees the raw key. A key a translation lacks silently shows English in the middle of a
/// translated window, which is the drift that actually happens: a string added to <c>de.json</c>
/// and forgotten in <c>fr.json</c>.
/// </para>
/// </summary>
public class StringTableTests
{
    private const string FallbackLanguage = "en";

    [Fact]
    public void EveryShippedLanguageHasATable()
    {
        foreach (var code in DevDX.Services.Loc.Available.Select(l => l.Code))
            Assert.True(File.Exists(TablePath(code)), $"No string table for '{code}'.");
    }

    [Fact]
    public void EveryTableParsesAndHasNoBlankValues()
    {
        foreach (var (code, table) in AllTables())
        {
            Assert.NotEmpty(table);
            foreach (var (key, value) in table)
                Assert.False(string.IsNullOrWhiteSpace(value), $"{code}: '{key}' is blank.");
        }
    }

    /// <summary>
    /// §20's parity rule: every language file carries the same key set. Asserted against English in
    /// both directions — a key English lacks is unreachable, a key a translation lacks silently
    /// falls back to English mid-window.
    /// </summary>
    [Fact]
    public void EveryTableCarriesExactlyTheEnglishKeySet()
    {
        var english = Table(FallbackLanguage);
        foreach (var (code, table) in AllTables().Where(t => t.Code != FallbackLanguage))
        {
            var missing = english.Keys.Where(k => !table.ContainsKey(k)).Order().ToList();
            var orphans = table.Keys.Where(k => !english.ContainsKey(k)).Order().ToList();
            Assert.True(missing.Count == 0 && orphans.Count == 0,
                $"{code} is not at parity with English: missing [{string.Join(", ", missing)}], " +
                $"extra [{string.Join(", ", orphans)}]");
        }
    }

    /// <summary>
    /// Every <c>Loc.Get("…")</c> / <c>Loc.Format("…")</c> / <c>{loc:Localize Key=…}</c> in the app
    /// has to resolve in English, or it renders as the bare key on screen.
    /// <para>
    /// Fragments ending in a dot are the literal half of a concatenated key
    /// (<c>Loc.Get("Uuid.Version." + version)</c>) and are not keys in their own right; the tail is
    /// only known at runtime, so a source scan can do no better than skip them.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryReferencedKeyExistsInEnglish()
    {
        var english = Table(FallbackLanguage);
        var missing = ReferencedKeys()
            .Where(k => !k.EndsWith('.'))
            .Where(k => !english.ContainsKey(k))
            .Order()
            .ToList();

        Assert.True(missing.Count == 0,
            "Referenced but absent from en.json: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The placeholders in a translation have to match English's, or <see cref="string.Format"/>
    /// either drops an argument or throws on one that was never supplied.
    /// </summary>
    [Fact]
    public void PlaceholdersMatchEnglish()
    {
        var english = Table(FallbackLanguage);
        foreach (var (code, table) in AllTables().Where(t => t.Code != FallbackLanguage))
        {
            foreach (var (key, value) in table)
            {
                if (!english.TryGetValue(key, out var source))
                    continue; // covered by NoTranslationCarriesAKeyEnglishLacks
                Assert.True(Placeholders(source).SetEquals(Placeholders(value)),
                    $"{code}: '{key}' placeholders {Show(Placeholders(value))} " +
                    $"do not match English {Show(Placeholders(source))}.");
            }
        }
    }

    /// <summary>
    /// A translation that is character-for-character English is usually a key that was copied to
    /// get the file to parity and never actually translated. Some are legitimately identical —
    /// proper nouns, standard names (<c>ISO 8601</c>, <c>HMAC</c>), casing conventions whose whole
    /// point is the literal spelling (<c>camelCase</c>) — so this is a budget rather than a ban.
    /// It exists to catch a bulk copy of a whole section, which is what a rushed backfill produces.
    /// </summary>
    [Fact]
    public void NoTableIsMostlyUntranslatedEnglish()
    {
        var english = Table(FallbackLanguage);
        foreach (var (code, table) in AllTables().Where(t => t.Code != FallbackLanguage))
        {
            int identical = table.Count(kv =>
                english.TryGetValue(kv.Key, out var source) &&
                string.Equals(source, kv.Value, StringComparison.Ordinal));

            Assert.True(identical < table.Count / 4,
                $"{code}: {identical} of {table.Count} values are identical to English — " +
                "that looks like keys copied rather than translated.");
        }
    }

    // ---- Helpers -----------------------------------------------------------

    private static HashSet<string> Placeholders(string value) =>
        Regex.Matches(value, @"\{(\d+)[^}]*\}").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    private static string Show(IEnumerable<string> items) => "{" + string.Join(",", items.Order()) + "}";

    private static IEnumerable<(string Code, Dictionary<string, string> Table)> AllTables() =>
        Directory.EnumerateFiles(StringsDirectory, "*.json")
            .Select(p => (Path.GetFileNameWithoutExtension(p), Read(p)))
            .OrderBy(t => t.Item1, StringComparer.Ordinal);

    private static Dictionary<string, string> Table(string code) => Read(TablePath(code));

    private static Dictionary<string, string> Read(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
        ?? throw new InvalidOperationException($"'{path}' did not parse as a string table.");

    private static string TablePath(string code) => Path.Combine(StringsDirectory, code + ".json");

    /// <summary>Every string key the app asks for as a literal, from both C# and XAML.</summary>
    private static IEnumerable<string> ReferencedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(SourceRoot, "*.*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(file) is not (".cs" or ".xaml"))
                continue;
            var parts = file.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("obj") || parts.Contains("bin"))
                continue;

            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"Loc\.(?:Get|Format)\(\s*""([^""]+)"""))
                keys.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"Localize\s+Key=([A-Za-z0-9_.]+)"))
                keys.Add(m.Groups[1].Value);
        }
        return keys;
    }

    private static string StringsDirectory => Path.Combine(SourceRoot, "Strings");

    /// <summary>Walks up from the test binary's output directory to find <c>src/DevDX</c>, the same
    /// way <c>NetworkPolicyArchitectureTests</c> does.</summary>
    private static string SourceRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "src", "DevDX");
                if (Directory.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate src/DevDX from " + AppContext.BaseDirectory);
        }
    }
}
