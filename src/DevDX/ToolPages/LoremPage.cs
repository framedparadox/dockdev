using DevDX.Controls;
using DevDX.Models;
using DevDX.Services;
using DevDX.Services.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevDX.ToolPages;

/// <summary>Lorem Ipsum Generator: placeholder words, sentences or paragraphs.</summary>
public sealed class LoremPage : FormToolPage
{
    private readonly ComboBox _unit = new();
    private readonly NumberBox _count = new() { Value = 3, Minimum = 1, Maximum = 500, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 140 };
    private readonly CheckBox _classicOpening = new() { Content = Loc.Get("Lorem.ClassicOpening"), IsChecked = true };
    private readonly TextBox _results = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 300 };

    private static readonly LoremUnit[] Units = [LoremUnit.Paragraphs, LoremUnit.Sentences, LoremUnit.Words];

    public LoremPage()
    {
        AddRow(SectionHeader(Loc.Get("Tool.Lorem.Name")));

        foreach (var u in Units)
            _unit.Items.Add(Loc.Get("Lorem.Unit." + u));
        _unit.SelectedIndex = 0;

        AddRow(LabelledRow(Loc.Get("Lorem.UnitLabel"), _unit));

        var generate = new Button
        {
            Content = Loc.Get("Lorem.Generate"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        generate.Click += (_, _) => Generate();
        _classicOpening.VerticalAlignment = VerticalAlignment.Bottom;

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(generate);
        actions.Children.Add(LabelledRow(Loc.Get("Lorem.Count"), _count));
        actions.Children.Add(_classicOpening);
        AddRow(actions);

        // The copy button sits beside the results box (its own row) rather than beside Generate,
        // mirroring FormToolPage.ResultRow's shape for a box that already exists as a field.
        var resultsRow = new Grid { ColumnSpacing = 8 };
        resultsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        resultsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var resultsLabelled = LabelledRow(Loc.Get("Uuid.Results"), _results);
        var copyAll = new CopyButton { GetText = () => _results.Text, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(resultsLabelled, 0);
        Grid.SetColumn(copyAll, 1);
        resultsRow.Children.Add(resultsLabelled);
        resultsRow.Children.Add(copyAll);
        AddRow(resultsRow);

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

    private void Generate() =>
        _results.Text = LoremTools.Generate(Units[_unit.SelectedIndex], (int)_count.Value, _classicOpening.IsChecked == true);
}
