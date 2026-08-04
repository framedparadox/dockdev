namespace DevDX.Services;

/// <summary>
/// A curated set of built-in Segoe Fluent Icons glyphs offered by the icon picker, as a
/// no-network, no-file-dialog alternative to browsing for a custom image. Deliberately a small,
/// general-purpose set — enough to tell one pinned tool from another at a glance —
/// not an exhaustive dump of every icon Windows ships.
/// <para>
/// <see cref="Choice.Name"/> is a plain English literal rather than routed through
/// <see cref="Loc"/>: it is a mnemonic label on a swatch grid (the glyph itself is the content
/// being chosen), not a sentence a non-English speaker needs translated to use the feature —
/// the same scoping most icon/color pickers give their swatch names. Every other string in the
/// picker's chrome (title, the "browse for an image" entry) is fully localized as usual.
/// </para>
/// </summary>
public static class IconChoices
{
    public readonly record struct Choice(string Glyph, string Name);

    public static readonly IReadOnlyList<Choice> All = new[]
    {
        new Choice("", "Open folder"),
        new Choice("", "Folder"),
        new Choice("", "Home"),
        new Choice("", "Browser"),
        new Choice("", "Code"),
        new Choice("", "Chat"),
        new Choice("", "Mail"),
        new Choice("", "Video"),
        new Choice("", "Camera"),
        new Choice("", "Photos"),
        new Choice("", "Calendar"),
        new Choice("", "Clock"),
        new Choice("", "Cloud"),
        new Choice("", "Lock"),
        new Choice("", "Favorite"),
        new Choice("", "Shop"),
        new Choice("", "Library"),
        new Choice("", "Health"),
        new Choice("", "People"),
        new Choice("", "Network"),
        new Choice("", "Tools"),
        new Choice("", "Car"),
    };
}
