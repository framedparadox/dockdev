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
/// §14.1–§14.3). One editable pane; Format, Minify, Validate, sort keys, copy. Invalid input
/// never clears the user's text.
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

    /// <summary>Wide enough for the field's one or two digits plus some breathing room — there are
    /// no spin buttons to share the width with any more (see <see cref="_indentBox"/>).</summary>
    private const double IndentBoxWidth = 60;

    private readonly IDataFormat? _fixedFormat;
    private readonly CodeEditor _editor = new();
    private readonly NumberBox _indentBox = new()
    {
        Value = DefaultIndentWidth,
        Minimum = 1,
        Maximum = 8,
        // Explicit rather than left to the defaults: LargeChange defaults to 10, which on a range
        // of 1–8 makes Page Up a jump straight to the end.
        SmallChange = 1,
        LargeChange = 2,
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
        _indentBox.EnableWheelStep(1);

        SetBody(Pane(_editor));
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
        ToolCommand.Save(SaveOutput),
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
    }

    /// <summary>Records which format the document turned out to be and points the editor's
    /// syntax colouring at its tokenizer.</summary>
    private void SetActiveFormat(IDataFormat format)
    {
        _activeFormat = format;
        _editor.Tokenizer = format.Tokenizer;
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

    private void SaveOutput()
    {
        if (_editor.Text.Length == 0)
            return;

        var extension = (_activeFormat ?? _fixedFormat)?.Extensions is [var ext, ..] ? ext : ".txt";
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var name = "formatted";
            var path = Path.Combine(desktop, name + extension);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(desktop, $"{name} ({i}){extension}");

            File.WriteAllText(path, _editor.Text);
            StatusBar.SetMessage(Loc.Format("Tool.SavedTo", Path.GetFileName(path)), isError: false);
        }
        catch (Exception ex)
        {
            StatusBar.SetMessage(ex.Message, isError: true);
        }
    }

    private void Clear()
    {
        Replace("");
        _isDirty = false;
        StatusBar.SetUntouched();
    }
}
