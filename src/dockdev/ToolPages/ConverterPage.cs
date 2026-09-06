using dockdev.Controls;
using dockdev.Models;
using dockdev.Services;
using dockdev.Services.Formats;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace dockdev.ToolPages;

/// <summary>
/// Data Converter (design doc §14): source format · swap · target format · input · read-only
/// output. Conversions route through <see cref="DataNode"/>, so JSON⇄XML is lossless and only the
/// CSV leg flattens (<see cref="CsvProjection"/>), with an in-product info tip saying so.
/// </summary>
public sealed class ConverterPage : EditorToolPage
{
    private readonly CodeEditor _input = new();
    private readonly CodeView _output = new();
    private readonly ComboBox _source = new();
    private readonly ComboBox _target = new();
    private readonly InfoBar _lossyTip = new()
    {
        Severity = InfoBarSeverity.Informational,
        IsClosable = false,
        IsOpen = false,
    };

    private bool _isDirty;
    private static readonly IReadOnlyList<IDataFormat> Formats = FormatRegistry.All;

    public ConverterPage()
    {
        _lossyTip.Message = Loc.Get("Converter.CsvLossyTip");
        _input.AccessibleName = Loc.Get("Common.Input");
        _output.AccessibleName = Loc.Get("Common.Output");

        foreach (var format in Formats)
        {
            _source.Items.Add(Loc.Get(format.DisplayNameKey));
            _target.Items.Add(Loc.Get(format.DisplayNameKey));
        }
        _source.SelectedIndex = 0; // JSON
        _target.SelectedIndex = 1; // XML

        // The source dropdown already states what the input is, so the editor can colour it
        // without having to guess.
        _input.Tokenizer = Formats[_source.SelectedIndex].Tokenizer;
        // Changing either end re-runs the conversion: the output pane is a statement about what
        // the two dropdowns currently say, and leaving yesterday's result sitting under a new pair
        // of formats is simply wrong. Convert already no-ops on an empty editor, so this costs
        // nothing before there is anything to convert.
        _source.SelectionChanged += (_, _) =>
        {
            if (_source.SelectedIndex < 0)
                return;
            _input.Tokenizer = Formats[_source.SelectedIndex].Tokenizer;
            Convert();
        };
        _target.SelectionChanged += (_, _) =>
        {
            if (_target.SelectedIndex >= 0)
                Convert();
        };

        var swap = new Button { Content = new FontIcon { Glyph = "\uE8AB", FontSize = 14 }, Margin = new Thickness(8, 0, 8, 0) };
        ToolTipService.SetToolTip(swap, Loc.Get("Tool.SwapInOut"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swap, Loc.Get("Tool.SwapInOut"));
        swap.Click += (_, _) => (_source.SelectedIndex, _target.SelectedIndex) = (_target.SelectedIndex, _source.SelectedIndex);

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(12, 8, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var fromLabel = OptionLabelFor(_source, Loc.Get("Converter.From"));
        fromLabel.Margin = new Thickness(0, 0, 8, 0);
        header.Children.Add(fromLabel);
        header.Children.Add(_source);
        header.Children.Add(swap);
        var toLabel = OptionLabelFor(_target, Loc.Get("Converter.To"));
        toLabel.Margin = new Thickness(8, 0, 8, 0);
        header.Children.Add(toLabel);
        header.Children.Add(_target);

        _input.TextChanged += (_, _) => { _isDirty = _input.Text.Length > 0; StatusBar.SetCounts(_input.Text); };

        SetOptions(header);

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var inputPane = Pane(_input);
        var outputPane = Pane(_output, secondary: true);
        Grid.SetColumn(inputPane, 0);
        Grid.SetColumn(outputPane, 1);
        split.Children.Add(inputPane);
        split.Children.Add(outputPane);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_lossyTip, 0);
        Grid.SetRow(split, 1);
        body.Children.Add(_lossyTip);
        body.Children.Add(split);

        SetBody(body);
        StatusBar.SetUntouched();
        InitializeChrome();
    }

    public override ToolKind Kind => ToolKind.DataConverter;
    public override bool IsDirty => _isDirty;

    public override IReadOnlyList<ToolCommand> Commands =>
    [
        new ToolCommand(Loc.Get("Tool.ConvertAction"), "\uE895", Convert, VirtualKey.Enter, VirtualKeyModifiers.Control, id: ToolCommand.Ids.Convert),
        ToolCommand.Copy(CopyOutput),
        ToolCommand.Save(SaveOutput),
        ToolCommand.Clear(Clear),
    ];

    public override async Task LoadFileAsync(string path)
    {
        var read = await InputLimits.ReadTextAsync(path);
        if (!read.Ok)
        {
            StatusBar.SetMessage(read.Error, isError: true);
            return;
        }

        _input.Text = read.Text;
        var ext = Path.GetExtension(path);
        int index = Formats.ToList().FindIndex(f => f.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase));
        if (index >= 0)
            _source.SelectedIndex = index;
        Convert();
    }

    private void Convert()
    {
        var text = _input.Text;
        // SelectedIndex is -1 between a ComboBox being cleared and re-populated (which is what a
        // language change does to this page), and indexing Formats with it would take the window
        // down rather than simply having nothing to convert yet.
        if (text.Length == 0 || _source.SelectedIndex < 0 || _target.SelectedIndex < 0)
        {
            _output.Clear();
            StatusBar.SetUntouched();
            return;
        }

        var source = Formats[_source.SelectedIndex];
        var target = Formats[_target.SelectedIndex];
        _lossyTip.IsOpen = target == FormatRegistry.Csv || source == FormatRegistry.Csv;

        try
        {
            var canonical = source.ToCanonical(text);
            if (source == FormatRegistry.Csv && target != FormatRegistry.Csv)
                canonical = CsvProjection.Rebuild(canonical);
            else if (target == FormatRegistry.Csv && source != FormatRegistry.Csv)
                canonical = CsvProjection.Flatten(canonical);

            var converted = target.FromCanonical(canonical, FormatOptions.Default);
            _output.SetContent(converted, target.Tokenizer.Tokenize(converted));
            StatusBar.SetCounts(converted);
            StatusBar.SetValid();
        }
        catch (Exception ex)
        {
            StatusBar.SetError(1, 1);
            Diag.Log("ConverterPage.Convert failed: " + ex.Message);
        }
    }

    private void CopyOutput()
    {
        if (_output.Text.Length == 0)
            return;
<<<<<<< HEAD
=======
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(_output.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
        ClipboardService.TrySetText(_output.Text);
    }

    private void SaveOutput()
    {
<<<<<<< HEAD
=======
        if (_output.Text.Length == 0)
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
        if (_output.Text.Length == 0 || _target.SelectedIndex < 0 || _target.SelectedIndex >= Formats.Count)
            return;

        var extension = Formats[_target.SelectedIndex].Extensions[0];
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var name = "converted";
            var path = Path.Combine(desktop, name + extension);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(desktop, $"{name} ({i}){extension}");

            File.WriteAllText(path, _output.Text);
            StatusBar.SetMessage(Loc.Format("Tool.SavedTo", Path.GetFileName(path)), isError: false);
        }
        catch (Exception ex)
        {
            StatusBar.SetMessage(ex.Message, isError: true);
        }
    }

    private void Clear()
    {
        _input.Text = "";
        _output.Clear();
        StatusBar.SetUntouched();
    }
}
