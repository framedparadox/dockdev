namespace DevDX.Services;

/// <summary>
/// Whether a tool's <c>Glyph</c> is a real icon codepoint (single Segoe Fluent Icons
/// private-use-area character) or literal text (Base64's "01", Xml's "</>"). A FontIcon rendering
/// the latter through the icon font shows tofu, so every render site needs to pick its FontFamily
/// based on this.
/// </summary>
public static class GlyphFonts
{
    public static bool IsTextGlyph(string glyph) => glyph.Length > 1;
}
