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
        new Choice("\uE838", "Open folder"),
        new Choice("\uE8B7", "Folder"),
        new Choice("\uE80F", "Home"),
        new Choice("\uE774", "Browser"),
        new Choice("\uE943", "Code"),
        new Choice("\uE8F2", "Chat"),
        new Choice("\uE715", "Mail"),
        new Choice("\uE714", "Video"),
        new Choice("\uE722", "Camera"),
        new Choice("\uE8B9", "Photos"),
        new Choice("\uE787", "Calendar"),
        new Choice("\uE917", "Clock"),
        new Choice("\uE753", "Cloud"),
        new Choice("\uE72E", "Lock"),
        new Choice("\uE734", "Favorite"),
        new Choice("\uE719", "Shop"),
        new Choice("\uE8F1", "Library"),
        new Choice("\uE95E", "Health"),
        new Choice("\uE716", "People"),
        new Choice("\uE968", "Network"),
        new Choice("\uE90F", "Tools"),
        new Choice("\uE804", "Car"),
    };
}
