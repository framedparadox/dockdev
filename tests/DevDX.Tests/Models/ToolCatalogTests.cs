using DevDX.Models;
using Xunit;

namespace DevDX.Tests.Models;

/// <summary>
/// Design doc §29 risk #4: "Sixteen unverified Segoe Fluent glyphs… verify every one against the
/// official list before implementation; do not ship a placeholder." That verification happened by
/// hand once (a real regression: <c>Hash</c> shipped the exact glyph <c>IconChoices</c> uses for its
/// own "Cloud" swatch, and <c>Base64</c> shipped "Copy") and is worth keeping honest afterwards
/// without re-reading the glyph list by eye every time — this is the cheap, permanent half of that.
/// <para>
/// What it cannot check: <em>which</em> icon is the right one for a tool is a design judgement, not
/// a fact a unit test can verify. What it can check for free is the mechanical failure mode — an
/// empty glyph (a silent "tofu box" on the dock) or a codepoint outside the font's own documented
/// ranges (a typo in a <c>\uXXXX</c> escape) — which is exactly the shape of mistake that survives
/// code review because it renders as nothing rather than as something visibly wrong.
/// </para>
/// </summary>
public class ToolCatalogTests
{
    /// <summary>
    /// The five PUA ranges Microsoft's own Segoe Fluent Icons reference actually documents
    /// (learn.microsoft.com/windows/apps/design/iconography/segoe-fluent-icons-font). A glyph
    /// outside all five is not a Segoe Fluent Icons glyph at all, whatever font-fallback happens to
    /// render for it.
    /// </summary>
    private static readonly (char Low, char High)[] SegoeFluentIconRanges =
    [
        ('\uE700', '\uE9F9'),
        ('\uEA0C', '\uECF3'),
        ('\uED0C', '\uEFFF'),
        ('\uF000', '\uF5FF'),
        ('\uF600', '\uF8CC'),
    ];

    [Fact]
    public void EveryCatalogEntryHasAGlyph()
    {
        var missing = ToolCatalog.All.Where(t => string.IsNullOrEmpty(t.Glyph)).Select(t => t.Kind).ToList();

        Assert.True(missing.Count == 0,
            "These tools have no glyph at all, which renders as an empty dock cell: " +
            string.Join(", ", missing));
    }

    [Fact]
    public void EveryGlyphIsExactlyOneCharacter()
    {
        // A Segoe Fluent Icons glyph is always a single UTF-16 code unit in the Basic Multilingual
        // Plane's Private Use Area — never a surrogate pair, never a multi-character string pasted
        // in by mistake.
        var wrong = ToolCatalog.All.Where(t => t.Glyph.Length != 1).Select(t => (t.Kind, t.Glyph.Length)).ToList();

        Assert.True(wrong.Count == 0,
            "These glyphs are not exactly one character: " +
            string.Join(", ", wrong.Select(w => $"{w.Kind} ({w.Length} chars)")));
    }

    [Fact]
    public void EveryGlyphFallsInsideADocumentedSegoeFluentIconsRange()
    {
        var offenders = ToolCatalog.All
            .Where(t => t.Glyph.Length == 1)
            .Where(t => !SegoeFluentIconRanges.Any(r => t.Glyph[0] >= r.Low && t.Glyph[0] <= r.High))
            .Select(t => $"{t.Kind} (U+{(int)t.Glyph[0]:X4})")
            .ToList();

        Assert.True(offenders.Count == 0,
            "These glyphs fall outside every documented Segoe Fluent Icons range, so they are not " +
            "a real icon in this font: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Not a ban on reuse — several pairs share a glyph deliberately (design doc: JSON and XML both
    /// read as "Code", which is the same convention most editors use for either). It exists only to
    /// surface a reused glyph for a human to glance at, the way <c>PackageManifestTests</c>'
    /// <c>TheStoreLogoAndTheTileLogosAreDistinctFiles</c> flags reuse without banning it outright.
    /// </summary>
    [Fact]
    public void ReusedGlyphsAreASmallDeliberateSet()
    {
        var reused = ToolCatalog.All
            .GroupBy(t => t.Glyph)
            .Where(g => g.Count() > 1)
            .ToList();

        // Today: Json/Xml share "Code" (\uE943). If this grows past that one deliberate pair, the
        // catalog is quietly losing the "tell tools apart at a glance" property glyphs exist for.
        Assert.True(reused.Count <= 1 && reused.All(g => g.Count() == 2),
            "More glyph reuse than expected — check these deliberately: " +
            string.Join(" | ", reused.Select(g => string.Join(", ", g.Select(t => t.Kind)))));
    }
}
