using DevDX.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DevDX.Controls;

/// <summary>
/// The custom title bar every chrome'd DevDX window draws: a glyph, a title, correctly inset from
/// the left and stopped short of the caption buttons on the right.
/// <para>
/// It exists because the three windows that have one had each grown their own. They used 40, 40
/// and 40 for the height (against 32px caption buttons), 14, 14 and 16 for the left inset, 14, 14
/// and 15 for the glyph, and two of them styled the title as a caption while the third left it as
/// ad-hoc SemiBold body text — which design doc §13.5 rules out outright ("type ramp only, no
/// ad-hoc FontSize/FontWeight anywhere"). None of that was a decision; it was the same element
/// written three times.
/// </para>
/// <para>
/// The geometry lives in <see cref="WindowChrome"/> so the number that sizes this element is the
/// same number that sizes the caption buttons beside it.
/// </para>
/// </summary>
public sealed class AppTitleBar : Grid
{
    private readonly FontIcon _icon;
    private readonly TextBlock _title;

    /// <param name="glyph">A Segoe Fluent glyph, or empty for a title-only bar.</param>
    /// <param name="title">The window's name, as it also appears in Alt-Tab.</param>
    /// <param name="accentGlyph">Tint the glyph with the accent colour, the way Settings and
    /// Add-Tool already did and tool windows did not.</param>
    public AppTitleBar(string glyph, string title, bool accentGlyph = false)
    {
        Height = WindowChrome.TitleBarHeight;
        // Transparent, not unset: the drag region has to be hit-testable, and a Grid with no
        // background is not. This is why every one of these carried an explicit transparent brush.
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            // The right inset is what keeps a long tool name from being laid out under the close
            // button; the title trims instead.
            Margin = new Thickness(
                WindowChrome.TitleBarContentInset, 0, WindowChrome.CaptionButtonReserve, 0),
        };

        bool isTextGlyph = GlyphFonts.IsTextGlyph(glyph);
        _icon = new FontIcon
        {
            Glyph = glyph,
            FontFamily = isTextGlyph
                ? new FontFamily("Segoe UI")
                : (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            // 16 is the title-bar icon size Windows itself uses, and the one size all three of
            // these were approximating. Text glyphs ("01", "</>") run wider than a single icon
            // character, so they come in a touch smaller to avoid looking oversized here.
            FontSize = isTextGlyph ? 16 * 0.8 : 16,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        if (accentGlyph && Application.Current.Resources
                .TryGetValue("AccentTextFillColorPrimaryBrush", out var accent) && accent is Brush brush)
        {
            _icon.Foreground = brush;
        }
        content.Children.Add(_icon);

        _title = new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        content.Children.Add(_title);

        Children.Add(content);
    }
}
