using DevDX.Controls;
using DevDX.Models;
using DevDX.Services;
using DevDX.Services.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace DevDX.ToolPages;

/// <summary>
/// Text Toolkit (design doc §14.10): one page, operation picker. Operations chain — the output
/// pane is the input to the next operation with one click.
/// </summary>
public sealed class TextToolkitPage : EditorToolPage
{
    private readonly CodeEditor _input = new();
    private readonly CodeView _output = new();
    private readonly ComboBox _operation = new();
    private readonly TextBox _separatorBox = new() { Width = 80, Text = ",", Visibility = Visibility.Collapsed };
    private readonly TextBlock _countsSummary = new() { Opacity = 0.7, Margin = new Thickness(12, 4, 12, 0) };
    private bool _isDirty;

    private static readonly (string Key, Func<string, string?, string> Apply)[] Operations =
    [
        ("Text.Op.CamelCase", (t, _) => CaseConvert.ToCamelCase(t)),
        ("Text.Op.PascalCase", (t, _) => CaseConvert.ToPascalCase(t)),
        ("Text.Op.SnakeCase", (t, _) => CaseConvert.ToSnakeCase(t)),
        ("Text.Op.KebabCase", (t, _) => CaseConvert.ToKebabCase(t)),
        ("Text.Op.ConstantCase", (t, _) => CaseConvert.ToConstantCase(t)),
        ("Text.Op.TitleCase", (t, _) => CaseConvert.ToTitleCase(t)),
        ("Text.Op.SentenceCase", (t, _) => CaseConvert.ToSentenceCase(t)),
        ("Text.Op.Slugify", (t, _) => CaseConvert.Slugify(t)),
        ("Text.Op.SortAsc", (t, _) => LineOps.SortLines(t, natural: true, descending: false)),
        ("Text.Op.SortDesc", (t, _) => LineOps.SortLines(t, natural: true, descending: true)),
        ("Text.Op.Dedupe", (t, _) => LineOps.Dedupe(t)),
        ("Text.Op.TrimLines", (t, _) => LineOps.TrimLines(t)),
        ("Text.Op.NumberLines", (t, _) => LineOps.NumberLines(t)),
        ("Text.Op.ReverseLines", (t, _) => LineOps.ReverseLines(t)),
        ("Text.Op.Join", (t, sep) => LineOps.Join(t, sep ?? ",")),
        ("Text.Op.Split", (t, sep) => LineOps.Split(t, sep ?? ",")),
        ("Text.Op.JsonEscape", (t, _) => EscapeCodecs.JsonEscape(t)),
        ("Text.Op.JsonUnescape", (t, _) => EscapeCodecs.JsonUnescape(t)),
        ("Text.Op.CSharpEscape", (t, _) => EscapeCodecs.CSharpEscape(t)),
        ("Text.Op.CSharpUnescape", (t, _) => EscapeCodecs.CSharpUnescape(t)),
        ("Text.Op.SqlEscape", (t, _) => EscapeCodecs.SqlEscape(t)),
        ("Text.Op.SqlUnescape", (t, _) => EscapeCodecs.SqlUnescape(t)),
        ("Text.Op.ShellEscape", (t, _) => EscapeCodecs.ShellEscape(t)),
        ("Text.Op.ShellUnescape", (t, _) => EscapeCodecs.ShellUnescape(t)),
        ("Text.Op.RegexEscape", (t, _) => EscapeCodecs.RegexEscape(t)),
        ("Text.Op.RegexUnescape", (t, _) => EscapeCodecs.RegexUnescape(t)),
    ];

    public TextToolkitPage()
    {
        foreach (var (key, _) in Operations)
            _operation.Items.Add(Loc.Get(key));
        _operation.SelectedIndex = 0;
        _operation.SelectionChanged += (_, _) =>
        {
            var key = Operations[_operation.SelectedIndex].Key;
            _separatorBox.Visibility = key is "Text.Op.Join" or "Text.Op.Split" ? Visibility.Visible : Visibility.Collapsed;
            Apply();
        };
        _separatorBox.TextChanged += (_, _) => Apply();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12, 8, 12, 0) };
        header.Children.Add(new TextBlock { Text = Loc.Get("Text.Operation"), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(_operation);
        header.Children.Add(_separatorBox);
        var chain = new Button { Content = Loc.Get("Text.ChainToInput") };
        chain.Click += (_, _) => { _input.Text = _output.Text; Apply(); };
        header.Children.Add(chain);

        _input.TextChanged += (_, _) => { _isDirty = _input.Text.Length > 0; Apply(); };

        var inputPane = new Grid();
        inputPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inputPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(_input, 1);
        inputPane.Children.Add(header);
        inputPane.Children.Add(_input);

        var outputPane = new Grid();
        outputPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outputPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_output, 0);
        Grid.SetRow(_countsSummary, 1);
        outputPane.Children.Add(_output);
        outputPane.Children.Add(_countsSummary);

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(inputPane, 0);
        Grid.SetColumn(outputPane, 1);
        split.Children.Add(inputPane);
        split.Children.Add(outputPane);

        SetBody(split);
        StatusBar.SetUntouched();
        InitializeChrome();
    }

    public override ToolKind Kind => ToolKind.TextToolkit;
    public override bool IsDirty => _isDirty;

    public override IReadOnlyList<ToolCommand> Commands =>
    [
        ToolCommand.Copy(CopyOutput),
        ToolCommand.Clear(Clear),
    ];

    public override bool AcceptsClipboardText(string text) => text.Length > 0;
    public override void PasteClipboardText(string text) => _input.Text = text;

    public override async Task LoadFileAsync(string path)
    {
        _input.Text = await File.ReadAllTextAsync(path);
        Apply();
    }

    private void Apply()
    {
        var text = _input.Text;
        StatusBar.SetCounts(text);
        if (text.Length == 0)
        {
            _output.Clear();
            _countsSummary.Text = "";
            StatusBar.SetUntouched();
            return;
        }

        var op = Operations[_operation.SelectedIndex];
        string result;
        try
        {
            result = op.Apply(text, _separatorBox.Text);
        }
        catch (Exception ex)
        {
            result = "(error: " + ex.Message + ")";
        }
        _output.SetContent(result);
        var counts = LineOps.Count(result);
        _countsSummary.Text = Loc.Format("Text.Counts", counts.Characters, counts.Words, counts.Lines);
        StatusBar.SetValid();
    }

    private void CopyOutput()
    {
        if (_output.Text.Length == 0)
            return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(_output.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void Clear()
    {
        _input.Text = "";
        _output.Clear();
        _countsSummary.Text = "";
        StatusBar.SetUntouched();
    }
}
