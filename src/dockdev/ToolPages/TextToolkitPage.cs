using dockdev.Controls;
using dockdev.Models;
using dockdev.Services;
using dockdev.Services.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace dockdev.ToolPages;

/// <summary>
/// Text Toolkit (design doc §14.10): one page, operation picker. Operations chain — the output
/// pane is the input to the next operation with one click.
/// </summary>
public sealed class TextToolkitPage : EditorToolPage
{
    private readonly CodeEditor _input = new();
    private readonly CodeView _output = new();
    private readonly ComboBox _operation = new();
    private readonly TextBox _separatorBox = new()
    {
        Width = 80,
        Text = ",",
        Visibility = Visibility.Collapsed,
        PlaceholderText = ",",
    };
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
        _input.AccessibleName = Loc.Get("Common.Input");
        _output.AccessibleName = Loc.Get("Common.Output");
        foreach (var (key, _) in Operations)
            _operation.Items.Add(Loc.Get(key));
        _operation.SelectedIndex = 0;
        _operation.SelectionChanged += (_, _) =>
        {
            if (_operation.SelectedIndex < 0)
                return;
            var key = Operations[_operation.SelectedIndex].Key;
            _separatorBox.Visibility = key is "Text.Op.Join" or "Text.Op.Split" ? Visibility.Visible : Visibility.Collapsed;
            Apply();
        };
        _separatorBox.TextChanged += (_, _) => Apply();

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_separatorBox, Loc.Get("Text.SeparatorLabel"));

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12, 8, 12, 0) };
        header.Children.Add(OptionLabelFor(_operation, Loc.Get("Text.Operation")));
        header.Children.Add(_operation);
        header.Children.Add(_separatorBox);
        var chain = new Button { Content = Loc.Get("Text.ChainToInput") };
        chain.Click += (_, _) => { _input.Text = _output.Text; Apply(); };
        header.Children.Add(chain);

        _input.TextChanged += (_, _) => { _isDirty = _input.Text.Length > 0; Apply(); };

        SetOptions(header);

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
        var inputSurface = Pane(_input);
        var outputSurface = Pane(outputPane, secondary: true);
        Grid.SetColumn(inputSurface, 0);
        Grid.SetColumn(outputSurface, 1);
        split.Children.Add(inputSurface);
        split.Children.Add(outputSurface);

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
        var read = await InputLimits.ReadTextAsync(path);
        if (!read.Ok)
        {
            StatusBar.SetMessage(read.Error, isError: true);
            return;
        }

        _input.Text = read.Text;
        Apply();
    }

    private void Apply()
    {
        var text = _input.Text;
        StatusBar.SetCounts(text);
        if (text.Length == 0 || _operation.SelectedIndex < 0)
        if (text.Length == 0 || _operation.SelectedIndex < 0 || _operation.SelectedIndex >= Operations.Length)
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
        ClipboardService.TrySetText(_output.Text);
    }

    private void Clear()
    {
        _input.Text = "";
        _output.Clear();
        _countsSummary.Text = "";
        StatusBar.SetUntouched();
    }
}
