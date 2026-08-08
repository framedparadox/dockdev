using System.Globalization;
using dockdev.Controls;
using dockdev.Models;
using dockdev.Services;
using dockdev.Services.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace dockdev.ToolPages;

/// <summary>
/// Password Generator: cryptographically random secrets with the usual character-class switches,
/// plus the entropy the chosen settings actually buy — the one number that makes "longer beats
/// weirder" obvious. Nothing generated here is stored or logged; close the window and it's gone.
/// </summary>
public sealed class PasswordPage : FormToolPage
{
    private readonly NumberBox _length = new() { Value = 20, Minimum = 4, Maximum = 512, Width = 150 };
    private readonly NumberBox _count = new() { Value = 5, Minimum = 1, Maximum = 500, Width = 150 };
    private readonly CheckBox _lower = new() { Content = Loc.Get("Password.Lowercase"), IsChecked = true };
    private readonly CheckBox _upper = new() { Content = Loc.Get("Password.Uppercase"), IsChecked = true };
    private readonly CheckBox _digits = new() { Content = Loc.Get("Password.Digits"), IsChecked = true };
    private readonly CheckBox _symbols = new() { Content = Loc.Get("Password.Symbols"), IsChecked = true };
    private readonly CheckBox _excludeAmbiguous = new() { Content = Loc.Get("Password.ExcludeAmbiguous") };
    private readonly TextBlock _entropy = new() { Opacity = 0.85 };
    private readonly TextBox _results = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 260, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };

    public PasswordPage()
    {
        var classes = new Grid { RowSpacing = 8, ColumnSpacing = 12 };
        classes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        classes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        classes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        classes.RowDefinitions.Add(new RowDefinition());
        classes.RowDefinitions.Add(new RowDefinition());

        var checkboxes = new[] { _lower, _upper, _digits, _symbols, _excludeAmbiguous };
        for (var i = 0; i < checkboxes.Length; i++)
        {
            var box = checkboxes[i];
            Grid.SetRow(box, i / 3);
            Grid.SetColumn(box, i % 3);
            classes.Children.Add(box);
            box.Checked += (_, _) => Generate();
            box.Unchecked += (_, _) => Generate();
        }
        AddRow(LabelledRow(Loc.Get("Password.Characters"), classes));

        var generate = new Button
        {
            Content = Loc.Get("Password.Generate"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        generate.Click += (_, _) => Generate();

        var settings = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        settings.Children.Add(LabelledRow(Loc.Get("Password.Length"), _length));
        settings.Children.Add(LabelledRow(Loc.Get("Password.Count"), _count));
        settings.Children.Add(generate);
        AddRow(settings);
        AddRow(_entropy);

        var copyAll = new CopyButton { GetText = () => _results.Text, VerticalAlignment = VerticalAlignment.Center };
        var resultsHeader = new Grid { ColumnSpacing = 8 };
        resultsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        resultsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var resultsLabel = SectionHeader(Loc.Get("Uuid.Results"));
        resultsLabel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(resultsLabel, 0);
        Grid.SetColumn(copyAll, 1);
        resultsHeader.Children.Add(resultsLabel);
        resultsHeader.Children.Add(copyAll);
        AddRow(resultsHeader);
        AddRow(_results);

        _length.EnableWheelStep();
        _count.EnableWheelStep();
        _length.ValueChanged += (_, _) => Generate();
        _count.ValueChanged += (_, _) => Generate();
        Generate();
    }

    public override ToolKind Kind => ToolKind.Password;
    public override bool IsDirty => false;

    public override IReadOnlyList<ToolCommand> Commands =>
    [
        ToolCommand.Run(Generate),
    ];

    private PasswordOptions Options => new(
        (int)_length.Value,
        _lower.IsChecked == true,
        _upper.IsChecked == true,
        _digits.IsChecked == true,
        _symbols.IsChecked == true,
        _excludeAmbiguous.IsChecked == true);

    private void Generate()
    {
        var options = Options;
        var bits = PasswordTools.EntropyBits(options);
        _entropy.Text = bits <= 0
            ? Loc.Get("Password.NoCharacters")
            : Loc.Format("Password.Entropy", bits.ToString("0", CultureInfo.CurrentCulture));
        _results.Text = bits <= 0 ? "" : string.Join('\n', PasswordTools.GenerateMany(options, (int)_count.Value));
    }
}
