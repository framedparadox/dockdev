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
    private readonly NumberBox _count = new() { Value = 3, Minimum = 1, Maximum = 500, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 120 };
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
        AddRow(LabelledRow(Loc.Get("Lorem.Count"), _count));
        AddRow(_classicOpening);

        var generate = new Button { Content = Loc.Get("Lorem.Generate"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        generate.Click += (_, _) => Generate();
        var copyAll = new CopyButton(Loc.Get("Uuid.CopyAll")) { GetText = () => _results.Text };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(generate);
        actions.Children.Add(copyAll);
        AddRow(actions);

        AddRow(LabelledRow(Loc.Get("Uuid.Results"), _results));

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
