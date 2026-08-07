using DevDX.Services;
using DevDX.Services.Syntax;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DevDX.Controls;

/// <summary>
/// Read-only, colourised output view (design doc §10.2). v1 renders the whole document into one
/// selectable <see cref="TextBlock"/> (<c>IsTextSelectionEnabled</c> gives real click-drag
/// selection across colour boundaries "for free", covering §10.2's "no free-form cross-line
/// drag-select" concern without needing the plain-text-view fallback) rather than the fully
/// virtualized per-line <c>ItemsRepeater</c> the design document describes — see §29 risk #1,
/// whose own prescribed fallback ("cap live highlighting at a smaller threshold and lean harder
/// on the plain-text path") is exactly what <see cref="MaxHighlightLength"/> below does.
/// </summary>
public sealed class CodeView : Grid
{
    /// <summary>Above this length, colouring is skipped and the raw text is shown plain — the
    /// §22 "large input" fallback, in lieu of full incremental virtualization.</summary>
    public const int MaxHighlightLength = 300_000;

    private readonly TextBlock _gutter = new()
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 13,
        TextAlignment = TextAlignment.Right,
        Opacity = 0.45,
        Margin = new Thickness(8, 8, 8, 8),
    };

    /// <summary>
    /// Deliberately <b>not</b> wrapped. A wrapped line takes several rows on screen while the
    /// gutter beside it still spends one row per line, so from the first long line onwards every
    /// number pointed at the wrong row — which is worse than no gutter at all when the status bar
    /// has just told you the error is on line 40. Long lines scroll horizontally instead.
    /// </summary>
    private readonly TextBlock _content = new()
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 13,
        TextWrapping = TextWrapping.NoWrap,
        IsTextSelectionEnabled = true,
        Margin = new Thickness(0, 8, 8, 8),
    };

    private string _text = "";
    private IReadOnlyList<Token> _tokens = [];
    private bool _showLineNumbers = true;

    public CodeView()
    {
        // No column definitions on the view itself: the gutter/content split belongs to the `row`
        // grid inside the scroller below. Declaring an Auto first column here as well left the
        // scroller sitting in it, so the whole view sized itself to the gutter instead of filling
        // the space it was given — the output pane never spanned its half of the window.
        // Horizontal scrolling is on because the content pane no longer wraps: with it Disabled a
        // line longer than the pane was clipped at the edge with no way to reach the rest of it.
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_gutter, 0);
        Grid.SetColumn(_content, 1);
        row.Children.Add(_gutter);
        row.Children.Add(_content);
        scroller.Content = row;
        Children.Add(scroller);

        // Decorative: the line numbers are a visual aid, and Narrator reading "one two three
        // four…" ahead of the document is noise, not information.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
            _gutter, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        // On the content block, not this Grid: a UI test reads the result off the element that
        // holds the text.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_content, AutomationIds.EditorOutput);

        ActualThemeChanged += (_, _) => Render();
    }

    public bool ShowLineNumbers
    {
        get => _showLineNumbers;
        set { _showLineNumbers = value; _gutter.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    }

    public string Text => _text;

    /// <summary>
    /// This view's accessible name, announced by Narrator when focus lands in its selectable text.
    /// A read-only colourised view carries no placeholder to fall back on at all, so without this
    /// it has no name whatsoever — see <see cref="CodeEditor.AccessibleName"/> for the input-side
    /// equivalent.
    /// </summary>
    public string AccessibleName
    {
        get => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(_content);
        set => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_content, value);
    }

    /// <summary>Sets the text and its tokens (empty tokens for plain/large-file display).</summary>
    public void SetContent(string text, IReadOnlyList<Token>? tokens = null)
    {
        _text = text ?? "";
        _tokens = _text.Length > MaxHighlightLength ? [] : tokens ?? [];
        Render();
    }

    public void Clear() => SetContent("");

    private void Render()
    {
        _content.Inlines.Clear();
        _content.TextHighlighters.Clear();

        int lineCount = 1;
        foreach (char c in _text)
            if (c == '\n')
                lineCount++;
        _gutter.Text = _showLineNumbers ? string.Join('\n', Enumerable.Range(1, lineCount)) : "";

        if (_text.Length == 0)
            return;

        bool highContrast = HighContrast.IsActive();
        bool dark = ActualTheme != ElementTheme.Light;

        var highlightRanges = new Dictionary<TokenKind, List<TextRange>>();
        int cursor = 0;
        foreach (var token in _tokens.OrderBy(t => t.Start))
        {
            // Every token is clipped to the text and to what has not been emitted yet, rather than
            // trusted to be in range and disjoint. Producers are supposed to hand over a
            // non-overlapping cover (§10.1) and the ones here do — but this loop turns a token into
            // a run of text, so a token overlapping its neighbour silently prints those characters
            // twice, and one running past the end takes the window down on a Substring. Neither is
            // a failure a reader could diagnose from what they see, so neither is left possible.
            int start = Math.Max(token.Start, cursor);
            int end = Math.Min(token.End, _text.Length);
            if (end <= start)
                continue;

            if (start > cursor)
                _content.Inlines.Add(new Run { Text = _text[cursor..start] });

            var text = _text[start..end];
            if (!highContrast && SyntaxPalette.IsBackgroundKind(token.Kind))
            {
                _content.Inlines.Add(new Run { Text = text });
                if (!highlightRanges.TryGetValue(token.Kind, out var list))
                    highlightRanges[token.Kind] = list = [];
                list.Add(new TextRange { StartIndex = start, Length = end - start });
            }
            else
            {
                var run = new Run { Text = text };
                if (!highContrast)
                {
                    var color = SyntaxPalette.ForegroundFor(token.Kind, dark);
                    if (color is { } c)
                        run.Foreground = new SolidColorBrush(c);
                }
                _content.Inlines.Add(run);
            }
            cursor = end;
        }
        if (cursor < _text.Length)
            _content.Inlines.Add(new Run { Text = _text.Substring(cursor) });

        if (!highContrast)
        {
            foreach (var (kind, ranges) in highlightRanges)
            {
                var highlighter = new TextHighlighter { Background = new SolidColorBrush(SyntaxPalette.BackgroundFor(kind, dark)) };
                foreach (var r in ranges)
                    highlighter.Ranges.Add(r);
                _content.TextHighlighters.Add(highlighter);
            }
        }
    }

}

/// <summary>The theme-aware colour table for every <see cref="TokenKind"/> (design doc Appendix B).
/// Colour is never the only signal elsewhere in the app (errors also get a status-bar message,
/// diff lines a gutter mark, findings a badge) — this table supplies the colour half of that.
/// <para>
/// The syntax colours are Visual Studio Code's <b>Dark+</b> and <b>Light+</b> defaults, matched
/// hex for hex. They are the scheme the largest number of developers already read fluently, so a
/// JSON document here looks like the same document in their editor: keys light blue, string values
/// warm, numbers green, <c>true</c>/<c>false</c>/<c>null</c> blue. The previous table was GitHub's
/// palette, which is equally defensible but meant DevDX agreed with neither the user's editor nor
/// itself.
/// </para></summary>
public static class SyntaxPalette
{
    public static bool IsBackgroundKind(TokenKind kind) =>
        kind is TokenKind.Match or TokenKind.Finding or TokenKind.Masked or TokenKind.Added or TokenKind.Removed;

    public static Color? ForegroundFor(TokenKind kind, bool dark) => kind switch
    {
        // JSON keys and XML attribute names — Dark+ variable blue / Light+ deep blue.
        TokenKind.PropertyName or TokenKind.AttributeName => dark ? C("#9CDCFE") : C("#0451A5"),
        // String values, including XML attribute values.
        TokenKind.String or TokenKind.AttributeValue => dark ? C("#CE9178") : C("#A31515"),
        TokenKind.Number => dark ? C("#B5CEA8") : C("#098658"),
        // true / false / null, and any language keyword.
        TokenKind.Boolean or TokenKind.Null or TokenKind.Keyword => dark ? C("#569CD6") : C("#0000FF"),
        TokenKind.TagName => dark ? C("#569CD6") : C("#800000"),
        TokenKind.Comment or TokenKind.CData => dark ? C("#6A9955") : C("#008000"),
        // Braces, brackets, commas and colons: dimmed rather than absent, so structure recedes
        // behind content instead of competing with it.
        TokenKind.Punctuation => dark ? C("#D4D4D4") : C("#3B3B3B"),
        TokenKind.Error => dark ? C("#F48771") : C("#C42B1C"),
        _ => null, // Plain: inherit the ambient text colour
    };

    public static Color BackgroundFor(TokenKind kind, bool dark) => kind switch
    {
        TokenKind.Match => dark ? C("#5A4B00") : C("#FFF3C4"),
        TokenKind.Finding => dark ? C("#5A2020") : C("#FFE0E0"),
        TokenKind.Masked => dark ? C("#1F3055") : C("#E0E8FF"),
        TokenKind.Added => dark ? C("#12341F") : C("#DDF4E4"),
        TokenKind.Removed => dark ? C("#3F1A1D") : C("#FBE3E4"),
        _ => Colors.Transparent,
    };

    private static Color C(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = System.Convert.ToByte(hex[..2], 16);
        byte g = System.Convert.ToByte(hex[2..4], 16);
        byte b = System.Convert.ToByte(hex[4..6], 16);
        return Color.FromArgb(255, r, g, b);
    }
}
