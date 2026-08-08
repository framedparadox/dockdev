using dockdev.Controls;
using dockdev.Models;
using dockdev.Services;
using dockdev.Services.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace dockdev.ToolPages;

/// <summary>UUID Generator (design doc §14.13): v4 and v7, plus NIL; bulk generate; six formats.</summary>
public sealed class UuidPage : FormToolPage
{
    private readonly ComboBox _version = new();
    private readonly ComboBox _format = new();
    private readonly NumberBox _count = new() { Value = 1, Minimum = 1, Maximum = 1000, MinWidth = 130 };
    private readonly TextBox _results = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 260, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };

    private static readonly UuidVersion[] Versions = [UuidVersion.V4, UuidVersion.V7, UuidVersion.Nil];
    private static readonly UuidFormat[] Formats =
        [UuidFormat.Hyphenated, UuidFormat.Braced, UuidFormat.Parenthesised, UuidFormat.Compact, UuidFormat.Uppercase, UuidFormat.Base64];

    public UuidPage()
    {
        foreach (var v in Versions)
            _version.Items.Add(Loc.Get("Uuid.Version." + v));
        _version.SelectedIndex = 0;
        foreach (var f in Formats)
            _format.Items.Add(Loc.Get("Uuid.Format." + f));
        _format.SelectedIndex = 0;

        _version.HorizontalAlignment = HorizontalAlignment.Stretch;
        _format.HorizontalAlignment = HorizontalAlignment.Stretch;
        _count.HorizontalAlignment = HorizontalAlignment.Stretch;
        _count.EnableWheelStep();

        var options = new Grid { ColumnSpacing = 16 };
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var versionField = LabelledRow(Loc.Get("Uuid.VersionLabel"), _version);
        var formatField = LabelledRow(Loc.Get("Uuid.FormatLabel"), _format);
        var countField = LabelledRow(Loc.Get("Uuid.Count"), _count);
        Grid.SetColumn(versionField, 0);
        Grid.SetColumn(formatField, 1);
        Grid.SetColumn(countField, 2);
        options.Children.Add(versionField);
        options.Children.Add(formatField);
        options.Children.Add(countField);
        AddRow(options);

        var generate = new Button { Content = Loc.Get("Uuid.Generate"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        generate.Click += (_, _) => Generate();
        AddRow(generate);

        // The copy button sits beside the "Results" label rather than beside Generate, with the
        // label standalone (not the box's Header) since the box gets its own full-width row below.
        var resultsRow = new Grid { ColumnSpacing = 8 };
        resultsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        resultsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var resultsLabel = new TextBlock { Text = Loc.Get("Uuid.Results"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        var copyAll = new CopyButton { GetText = () => _results.Text, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(resultsLabel, 0);
        Grid.SetColumn(copyAll, 1);
        resultsRow.Children.Add(resultsLabel);
        resultsRow.Children.Add(copyAll);
        AddRow(resultsRow);
        AddRow(_results);

        _version.SelectionChanged += (_, _) => Generate();
        _format.SelectionChanged += (_, _) => Generate();

        Generate();
    }

    public override ToolKind Kind => ToolKind.Uuid;
    public override bool IsDirty => false;

    public override IReadOnlyList<ToolCommand> Commands =>
    [
        new ToolCommand(Loc.Get("Uuid.Generate"), "", Generate, VirtualKey.Enter, VirtualKeyModifiers.Control, id: ToolCommand.Ids.Generate),
    ];

    private void Generate()
    {
        if (_version.SelectedIndex < 0 || _format.SelectedIndex < 0)
            return;
        var version = Versions[_version.SelectedIndex];
        var format = Formats[_format.SelectedIndex];
        int count = (int)_count.Value;
        _results.Text = string.Join('\n', UuidTools.GenerateMany(version, format, count));
    }
}
