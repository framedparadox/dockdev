using dockdev.Controls;
using dockdev.Models;
using dockdev.Services;
using dockdev.Services.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace dockdev.ToolPages;

/// <summary>Lorem Ipsum Generator: placeholder words, sentences or paragraphs.</summary>
public sealed class LoremPage : FormToolPage
{
    private readonly ComboBox _unit = new();
    private readonly NumberBox _count = new() { Value = 3, Minimum = 1, Maximum = 500, Width = 140 };
    private readonly CheckBox _classicOpening = new() { Content = Loc.Get("Lorem.ClassicOpening"), IsChecked = true };
    private readonly TextBox _results = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 300 };

    private static readonly LoremUnit[] Units = [LoremUnit.Paragraphs, LoremUnit.Sentences, LoremUnit.Words];

    public LoremPage()
    {
        foreach (var u in Units)
            _unit.Items.Add(Loc.Get("Lorem.Unit." + u));
        _unit.SelectedIndex = 0;
        _count.EnableWheelStep();

        var generate = new Button
        {
            Content = Loc.Get("Lorem.Generate"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        generate.Click += (_, _) => Generate();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(LabelledRow(Loc.Get("Lorem.UnitLabel"), _unit));
        actions.Children.Add(LabelledRow(Loc.Get("Lorem.Count"), _count));
        actions.Children.Add(generate);
        AddRow(actions);

        AddRow(_classicOpening);

        var resultsHeaderRow = new Grid { ColumnSpacing = 8 };
        resultsHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        resultsHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var resultsLabel = new TextBlock
        {
            Text = Loc.Get("Uuid.Results"),
            Opacity = 0.8,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        var copyAll = new CopyButton { GetText = () => _results.Text, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(resultsLabel, 0);
        Grid.SetColumn(copyAll, 1);
        resultsHeaderRow.Children.Add(resultsLabel);
        resultsHeaderRow.Children.Add(copyAll);
        AddRow(resultsHeaderRow);
        AddRow(_results);

        _unit.SelectionChanged += (_, _) => Generate();
        _count.ValueChanged += (_, _) => Generate();
        _classicOpening.Checked += (_, _) => Generate();
        _classicOpening.Unchecked += (_, _) => Generate();

        Generate();
    }

    public override ToolKind Kind => ToolKind.Lorem;
    public override bool IsDirty => false;

    public override IReadOnlyList<ToolCommand> Commands =>
    [
        ToolCommand.Run(Generate),
    ];

<<<<<<< HEAD
    private void Generate()
    {
        // Guard the selection index and the NumberBox value: SelectedIndex is -1 while the combo
        // rebuilds, and _count.Value is NaN when the box is empty — both would otherwise throw.
=======
    private void Generate() =>
        _results.Text = LoremTools.Generate(Units[_unit.SelectedIndex], (int)_count.Value, _classicOpening.IsChecked == true);
    private void Generate()
    {
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
        var unit = _unit.SelectedIndex >= 0 && _unit.SelectedIndex < Units.Length ? Units[_unit.SelectedIndex] : Units[0];
        int count = double.IsNaN(_count.Value) ? 3 : (int)Math.Clamp(_count.Value, 1, 500);
        _results.Text = LoremTools.Generate(unit, count, _classicOpening.IsChecked == true);
    }
}
