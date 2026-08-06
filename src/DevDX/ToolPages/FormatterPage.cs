using DevDX.Controls;
using DevDX.Models;
using DevDX.Services;
using DevDX.Services.Formats;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace DevDX.ToolPages;

/// <summary>
/// Backs the JSON, Data Formatter and XML catalog entries — three catalog entries, one page,
/// differing only by which <see cref="IDataFormat"/> they're constructed with (design doc §8,
/// §14.1–§14.3). One editable pane plus the structure tree; Format, Minify, Validate, sort keys,
/// copy. Invalid input never clears the user's text.
/// <para>
/// <b>One window, not two.</b> Formatting rewrites the editor in place rather than filling a
/// second read-only pane: the workflow is paste → format → copy, and a side-by-side output pane
/// meant the user read one half and copied from the other while the half they were looking at
/// went stale. The single pane is still coloured — <see cref="CodeEditor"/> highlights on a
/// debounce rather than per keystroke (§10.2), and this page points it at the format's tokenizer
/// (see <see cref="SetActiveFormat"/>).
/// </para>
/// </summary>
public sealed class FormatterPage : EditorToolPage
{
    /// <summary>What the indent starts at, and what it falls back to when the field is empty.</summary>
    private const int DefaultIndentWidth = 2;

    /// <summary>
    /// 64 DIPs is Fluent's <c>TextControlThemeMinWidth</c> — the platform's own minimum for a
    /// text-entry control, and the right size for a field that never holds more than one digit.
    /// <para>
    /// It was 96, which is the sizing for the multi-digit spinners on the form-shaped pages
    /// (Password's 4–512, UUID's 1–1000). Those sit in a labelled form row where the width reads as
    /// a column; this one sits in a 48px command bar between Format, Minify and Validate, where it
    /// was the widest element on the bar and mostly empty — a one-character option that looked like
    /// the tool's main text input.
    /// </para>
    /// </summary>
    private const double IndentBoxWidth = 64;

    private readonly IDataFormat? _fixedFormat;
    private readonly CodeEditor _editor = new();
    private readonly StructureTree _tree = new();
    private readonly Border _treeHost;
    private readonly NumberBox _indentBox = new()
    {
        Value = DefaultIndentWidth,
        Minimum = 1,
        Maximum = 8,
        // Explicit rather than left to the defaults: LargeChange defaults to 10, which on a range
        // of 1–8 makes Page Up a jump straight to the end.
        SmallChange = 1,
        LargeChange = 2,
        // Compact keeps the spin buttons out of the box's own width — Inline would put two
        // RepeatButtons inside it and force it back to roughly the width this is fixing.
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        Width = IndentBoxWidth,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private IReadOnlyList<ToolCommand>? _commands;
    private IDataFormat? _activeFormat;
    private bool _isDirty;
    private bool _replacing;
    private readonly ToolKind _kind;

    public FormatterPage(IDataFormat format)
    {
        _kind = ReferenceEquals(format, FormatRegistry.Json) ? ToolKind.Json
              : ReferenceEquals(format, FormatRegistry.Xml) ? ToolKind.Xml
              : ToolKind.DataFormatter;
        _fixedFormat = ReferenceEquals(format, FormatRegistry.Auto) ? null : format;

        _editor.PlaceholderText = Loc.Get("Formatter.InputPlaceholder");
        _editor.AccessibleName = Loc.Get("Common.Input");
        _tree.AccessibleName = Loc.Get("Formatter.Structure");
        // A single-format tool knows how to colour its input from the start. Auto-detect can't:
        // half-typed text has no reliable format yet, so it picks one up the first time a command
        // successfully parses the document (see SetActiveFormat).
        if (_fixedFormat is not null)
            _editor.Tokenizer = _fixedFormat.Tokenizer;
        _editor.TextChanged += (_, _) =>
        {
            if (_replacing)
                return; // our own write-back, not the user typing
            _isDirty = _editor.Text.Length > 0;
            StatusBar.SetCounts(_editor.Text);
        };

        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(_indentBox, Loc.Get("Formatter.Indent"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_indentBox, Loc.Get("Formatter.Indent"));

        // The tree starts collapsed so a fresh window is all editor. It only has something to
        // show once the document has been parsed, which is exactly what Format and Validate do —
        // so that is when it appears, rather than sitting there empty from the start.
        _treeHost = Pane(_tree, secondary: true);
        _treeHost.Width = 260;
        _treeHost.Visibility = Visibility.Collapsed;

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_editor, 0);
        Grid.SetColumn(_treeHost, 1);
        split.Children.Add(_editor);
        split.Children.Add(_treeHost);

        SetBody(split);
        StatusBar.SetUntouched();
        InitializeChrome();
    }

    public override ToolKind Kind => _kind;
    public override bool IsDirty => _isDirty;

    /// <summary>Built once and cached: the indent spinner is a live control, and handing the
    /// same instance out twice would re-parent it into a second command bar container.</summary>
    public override IReadOnlyList<ToolCommand> Commands => _commands ??=
    [
        new ToolCommand(Loc.Get("Tool.Format"), "", Format, VirtualKey.Enter, VirtualKeyModifiers.Control, id: ToolCommand.Ids.Format),
        new ToolCommand(Loc.Get("Tool.Minify"), "", Minify, id: ToolCommand.Ids.Minify),
        ToolCommand.Validate(Validate),
        // Indent sits with the two commands whose result it changes, not off in an options strip.
        ToolCommand.Element(_indentBox),
        ToolCommand.Copy(CopyOutput),
        ToolCommand.Clear(Clear),
    ];

    public override bool AcceptsClipboardText(string text) =>
        text.Length > 0 && (_fixedFormat ?? FormatRegistry.DetectBest(text)).DetectConfidence(text) > 0;

    public override void PasteClipboardText(string text)
    {
        _editor.Text = text;
        Format();
    }

    public override async Task LoadFileAsync(string path)
    {
        // InputLimits, not File.ReadAllTextAsync: §22's ceiling is checked against the file's size
        // before a byte is read, and the refusal is a sentence in the status bar rather than an
        // OOM inside a task nobody awaits.
        var read = await InputLimits.ReadTextAsync(path);
        if (!read.Ok)
        {
            StatusBar.SetMessage(read.Error, isError: true);
            return;
        }

        _editor.Text = read.Text;
        Format();
    }

    private FormatOptions Options => new()
    {
        // NumberBox reports NaN while its field is empty, and (int)double.NaN is undefined in C# —
        // it happens to land on a value the writers' own clamps absorb, but only by luck, and a
        // "luckily this is fine" is not what should decide how a document gets indented.
        IndentWidth = double.IsNaN(_indentBox.Value) ? DefaultIndentWidth : (int)_indentBox.Value,
        SortKeys = false,
    };

    /// <summary>
    /// Replaces the editor's text with the result of a transformation. The one place the page
    /// writes back into the editor, and the only thing that suppresses the TextChanged handler —
    /// without the guard, formatting would re-mark the page dirty on its own write.
    /// </summary>
    private void Replace(string text)
    {
        _replacing = true;
        try
        {
            _editor.Text = text;
        }
        finally
        {
            _replacing = false;
        }
        StatusBar.SetCounts(text);
    }

    private void Format()
    {
        var text = _editor.Text;
        if (text.Length == 0)
        {
            _tree.SetRoot(null);
            _treeHost.Visibility = Visibility.Collapsed;
            StatusBar.SetUntouched();
            return;
        }

        SetActiveFormat(_fixedFormat ?? FormatRegistry.DetectBest(text));
        var result = _activeFormat!.Format(text, Options);
        if (!result.Success)
        {
            // Invalid input never clears the user's text (§14.1): the editor keeps exactly what
            // they pasted and the status bar points at the offending line.
            var d = result.Diagnostics[0];
            StatusBar.SetError(d.Line, d.Column);
            return;
        }

        Replace(result.Text);
        StatusBar.SetValid();
        ShowStructure(text);
    }

    /// <summary>Records which format the document turned out to be and points the editor's
    /// syntax colouring at its tokenizer.</summary>
    private void SetActiveFormat(IDataFormat format)
    {
        _activeFormat = format;
        _editor.Tokenizer = format.Tokenizer;
    }

    /// <summary>Parses the document into the structure tree and reveals the sidebar. Called by the
    /// two commands that have already proved the text parses.</summary>
    private void ShowStructure(string text)
    {
        try
        {
            _tree.SetRoot((_activeFormat ?? _fixedFormat ?? FormatRegistry.DetectBest(text)).ToCanonical(text));
            _treeHost.Visibility = Visibility.Visible;
        }
        catch
        {
            _tree.SetRoot(null);
            _treeHost.Visibility = Visibility.Collapsed;
        }
    }

    private void Minify()
    {
        var text = _editor.Text;
        if (text.Length == 0)
            return;
        SetActiveFormat(_fixedFormat ?? FormatRegistry.DetectBest(text));
        var result = _activeFormat!.Minify(text);
        if (!result.Success)
        {
            var d = result.Diagnostics[0];
            StatusBar.SetError(d.Line, d.Column);
            return;
        }
        Replace(result.Text);
        StatusBar.SetValid();
    }

    private void Validate()
    {
        var text = _editor.Text;
        var format = _fixedFormat ?? FormatRegistry.DetectBest(text);
        var diagnostics = format.Validate(text);
        if (diagnostics.Count == 0)
        {
            StatusBar.SetValid();
            SetActiveFormat(format);
            ShowStructure(text);
        }
        else
        {
            StatusBar.SetError(diagnostics[0].Line, diagnostics[0].Column);
        }
    }

    private void CopyOutput()
    {
        if (_editor.Text.Length == 0)
            return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(_editor.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void Clear()
    {
        Replace("");
        _isDirty = false;
        _tree.SetRoot(null);
        _treeHost.Visibility = Visibility.Collapsed;
        StatusBar.SetUntouched();
    }
}
