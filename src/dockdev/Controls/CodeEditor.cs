using dockdev.Services;
using dockdev.Services.Syntax;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace dockdev.Controls;

/// <summary>
/// The input side of an editor-shaped tool: a monospace editor with a line-number gutter and, when
/// a <see cref="Tokenizer"/> is set, live syntax colouring using the same
/// <see cref="SyntaxPalette"/> as the read-only <see cref="CodeView"/>.
/// <para>
/// <b>Why a RichEditBox.</b> A plain <see cref="TextBox"/> renders one uniform colour — there is no
/// per-run formatting — so the editable pane was necessarily monochrome. Since the formatter and
/// masker became single-pane tools, that meant no colour anywhere in them. A RichEditBox can
/// colour character ranges through its document object, which is the only way to get a coloured
/// <em>editable</em> surface in WinUI without hand-rolling a text control.
/// </para>
/// <para>
/// <b>Why it isn't the classic performance trap</b> (design doc §10.2): re-colouring is debounced
/// (<see cref="HighlightDelay"/>) so a burst of typing re-tokenizes once at the end rather than
/// per keystroke, and skipped entirely above <see cref="MaxHighlightLength"/>, where the document
/// falls back to plain monospace text. Both are the §22 large-input fallback the design document
/// prescribes.
/// </para>
/// </summary>
public sealed class CodeEditor : Grid
{
    /// <summary>Above this length the document is left uncoloured. Lower than
    /// <see cref="CodeView.MaxHighlightLength"/> because this side is re-coloured as the user
    /// types, not once per render.</summary>
    public const int MaxHighlightLength = 100_000;

    /// <summary>How long typing has to pause before the document is re-coloured.</summary>
    private static readonly TimeSpan HighlightDelay = TimeSpan.FromMilliseconds(180);

    public RichEditBox TextBox { get; } = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 13,
        BorderThickness = new Thickness(0),
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        IsSpellCheckEnabled = false,
        VerticalContentAlignment = VerticalAlignment.Top,
        Padding = new Thickness(4, 2, 8, 8),
    };

    private readonly TextBlock _gutter = new()
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 13,
        TextAlignment = TextAlignment.Right,
        Opacity = 0.45,
        Margin = new Thickness(8, 2, 8, 8),
        IsTextSelectionEnabled = false,
    };

    private readonly DispatcherQueueTimer _highlightTimer;
    private bool _suppressTextChanged;

    public event EventHandler? TextChanged;

    /// <summary>The tokenizer to colour with, or null to leave the text plain. Setting it
    /// re-colours what is already in the editor.</summary>
    public ITokenizer? Tokenizer
    {
        get => _tokenizer;
        set
        {
            if (ReferenceEquals(_tokenizer, value))
                return;
            _tokenizer = value;
            Highlight();
        }
    }

    private ITokenizer? _tokenizer;

    public string PlaceholderText
    {
        get => TextBox.PlaceholderText;
        set => TextBox.PlaceholderText = value;
    }

    /// <summary>
    /// The editor's accessible name, announced by Narrator when focus lands in it. Distinct from
    /// <see cref="PlaceholderText"/>, which is example content (and often absent, or a syntax
    /// sample like a JWT shape) rather than a name for the field — a two-pane tool needs its input
    /// and output distinguishable by name alone, not by whichever example text happens to be set.
    /// </summary>
    public string AccessibleName
    {
        get => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(TextBox);
        set => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TextBox, value);
    }

    /// <summary>
    /// The document's text, with the rich-edit paragraph mark (<c>\r</c>) normalised to
    /// <c>\n</c>. The substitution is one character for one character, so a token offset computed
    /// over this string indexes the same position in the underlying document — which is what makes
    /// colouring by range possible at all.
    /// </summary>
    public string Text
    {
        get
        {
            TextBox.Document.GetText(TextGetOptions.None, out var text);
            // The document always reports a trailing paragraph mark that the user did not type.
            if (text.EndsWith('\r'))
                text = text[..^1];
            return text.Replace('\r', '\n');
        }
        set
        {
            _suppressTextChanged = true;
            try
            {
                TextBox.Document.SetText(TextSetOptions.None, value ?? "");
            }
            finally
            {
                _suppressTextChanged = false;
            }
            RefreshGutter();
            Highlight();
            TextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public CodeEditor()
    {
        // The editor's text does not wrap (a code editor that reflows a long JSON line is unusable
        // for finding a column number), so the scroller has to be able to scroll sideways —
        // Disabled meant anything past the right edge was clipped and unreachable.
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
        Grid.SetColumn(TextBox, 1);
        row.Children.Add(_gutter);
        row.Children.Add(TextBox);
        scroller.Content = row;
        Children.Add(scroller);

        // Decorative: the line numbers are a visual aid, and Narrator reading "one two three
        // four…" ahead of the document is noise, not information.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
            _gutter, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        // On the RichEditBox rather than on this Grid: the id has to land on the control that
        // actually carries the text pattern, which is what a UI test types into.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(TextBox, AutomationIds.EditorInput);

        // RichEditBox is a rich-text control being used as a code editor: everything that would
        // "help" a document — smart quotes, auto-bulleting, spell check — corrupts code.
        TextBox.ClipboardCopyFormat = RichEditClipboardFormat.PlainText;
        TextBox.DisabledFormattingAccelerators = DisabledFormattingAccelerators.All;
        TextBox.Document.UndoLimit = 100;

        _highlightTimer = DispatcherQueue.CreateTimer();
        _highlightTimer.IsRepeating = false;
        _highlightTimer.Interval = HighlightDelay;
        _highlightTimer.Tick += (_, _) => Highlight();

        // A pending highlight outlives the window that owns it. The dispatcher holds a running
        // timer, the timer's Tick closure holds this editor, and the editor holds the document —
        // so between the last keystroke and the tick 180ms later, closing the window leaves a
        // colouring pass queued against a RichEditBox whose native document has gone. Highlight()
        // guards its tokenizing but reads the selection before that guard opens, so the throw
        // lands on a timer tick with no caller: an unhandled exception, not a mis-coloured line.
        Unloaded += (_, _) => _highlightTimer.Stop();

        TextBox.TextChanged += (_, _) =>
        {
            if (_suppressTextChanged)
                return;
            RefreshGutter();
            _highlightTimer.Start(); // restarts the countdown on every keystroke
            TextChanged?.Invoke(this, EventArgs.Empty);
        };

        ActualThemeChanged += (_, _) => Highlight();
        RefreshGutter();
    }

    private void RefreshGutter()
    {
        int lines = 1;
        foreach (char c in Text)
            if (c == '\n')
                lines++;

        // A pathological paste (millions of lines) would otherwise build a multi-megabyte gutter
        // string and hang the UI thread; cap the rendered gutter and mark the overflow instead.
        const int MaxGutterLines = 10_000;
        int displayLines = Math.Min(lines, MaxGutterLines);

        var sb = new System.Text.StringBuilder(displayLines * 6);
        for (int i = 1; i <= displayLines; i++)
        {
            if (i > 1)
                sb.Append('\n');
            sb.Append(i);
        }
        if (lines > MaxGutterLines)
            sb.Append("\n…");

        _gutter.Text = sb.ToString();
    }

    /// <summary>
    /// Re-colours the whole document from the current tokenizer. Runs inside a display-update
    /// batch so the user never sees the intermediate reset-to-default, and saves and restores the
    /// selection because setting character formatting moves the insertion point.
    /// </summary>
    private void Highlight()
    {
        _highlightTimer.Stop();

        var text = Text;
        // Under High Contrast the platform's own TextControlForeground resource already carries
        // the palette the user picked (design doc §13.3: "High Contrast always wins") — forcing a
        // hardcoded Black/White here would fight it, exactly the bug CodeView's own render path
        // already guards against for the read-only side. There is no "clear to ambient" for a
        // RichEditBox's character-level ForegroundColor the way there is for a TextBlock brush, so
        // the ambient colour has to be read back from the control's own (theme-resolved) Foreground
        // and reapplied explicitly instead.
        bool highContrast = HighContrast.IsActive();
        var defaultColor = highContrast
            ? (TextBox.Foreground as SolidColorBrush)?.Color
            : ActualTheme == ElementTheme.Light ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;

        int selectionStart;
        int selectionEnd;
        try
        {
            // Reading the selection and opening the batch are inside a guard of their own, not
            // above the main one. Every line here talks to the RichEditBox's native document, and
            // the one caller is a timer tick — so on the closed-window path (see the Unloaded
            // handler in the constructor) these are the calls that throw, and they used to throw
            // from outside every try in this method.
            selectionStart = TextBox.Document.Selection.StartPosition;
            selectionEnd = TextBox.Document.Selection.EndPosition;
            TextBox.Document.BatchDisplayUpdates();
        }
        catch (Exception ex)
        {
            Services.Diag.Log("CodeEditor.Highlight: document unavailable: " + ex.Message);
            return;
        }

        _suppressTextChanged = true;
        try
        {
            // Tokenizing is inside the guard, not before it. This runs on whatever the user has
            // pasted, on a timer tick — and an exception on a timer tick has no caller to catch it,
            // so a scanner that walked off the end of a half-typed document would take the process
            // down rather than mis-colour a line. (The scanners are fuzzed against truncated and
            // mutated input in TokenizerFuzzTests; this is the belt to that pair of braces.)
            IReadOnlyList<Token> tokens = !highContrast && Tokenizer is { } tokenizer && text.Length is > 0 and <= MaxHighlightLength
                ? tokenizer.Tokenize(text)
                : [];

            // One reset, then one range per token: the gaps between tokens keep the default
            // colour rather than needing a range of their own. If the ambient colour could not be
            // resolved to a concrete Color (unexpected, but Foreground is a Brush, not always a
            // SolidColorBrush), the existing formatting is left alone rather than guessed at.
            if (defaultColor is { } resolvedDefault)
                TextBox.Document.GetRange(0, text.Length + 1).CharacterFormat.ForegroundColor = resolvedDefault;

            if (!highContrast)
            {
                bool dark = ActualTheme != ElementTheme.Light;
                foreach (var token in tokens)
                {
                    if (SyntaxPalette.ForegroundFor(token.Kind, dark) is not { } color)
                        continue;
                    TextBox.Document.GetRange(token.Start, token.End).CharacterFormat.ForegroundColor = color;
                }
            }
        }
        catch (Exception ex)
        {
            // A malformed range must never take the window down — the worst case here is
            // uncoloured text, which is exactly what the large-document path already shows.
            Services.Diag.Log("CodeEditor.Highlight failed: " + ex.Message);
        }
        finally
        {
            // Guarded too: a finally that throws replaces the exception the catch above just
            // handled with a new one, and this one runs on that same timer tick.
            try
            {
                TextBox.Document.Selection.SetRange(selectionStart, selectionEnd);
                TextBox.Document.ApplyDisplayUpdates();
            }
            catch (Exception ex)
            {
                Services.Diag.Log("CodeEditor.Highlight: could not restore the document: " + ex.Message);
            }
            _suppressTextChanged = false;
        }
    }

    public void Focus() => TextBox.Focus(FocusState.Programmatic);
}
