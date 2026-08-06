using DevDX.Controls;
using DevDX.Models;
using DevDX.Services;
using DevDX.Services.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace DevDX.ToolPages;

/// <summary>UUID Generator (design doc §14.13): v4 and v7, plus NIL; bulk generate; six formats.</summary>
public sealed class UuidPage : FormToolPage
{
    private readonly ComboBox _version = new();
    private readonly ComboBox _format = new();
    private readonly NumberBox _count = new() { Value = 1, Minimum = 1, Maximum = 1000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 100 };
    private readonly TextBox _results = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 260, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };

    private static readonly UuidVersion[] Versions = [UuidVersion.V4, UuidVersion.V7, UuidVersion.Nil];
    private static readonly UuidFormat[] Formats =
        [UuidFormat.Hyphenated, UuidFormat.Braced, UuidFormat.Parenthesised, UuidFormat.Compact, UuidFormat.Uppercase, UuidFormat.Base64];

    public UuidPage()
    {
        AddRow(SectionHeader(Loc.Get("Uuid.Title")));

        foreach (var v in Versions)
            _version.Items.Add(Loc.Get("Uuid.Version." + v));
        _version.SelectedIndex = 0;
        foreach (var f in Formats)
            _format.Items.Add(Loc.Get("Uuid.Format." + f));
        _format.SelectedIndex = 0;

        AddRow(LabelledRow(Loc.Get("Uuid.VersionLabel"), _version));
        AddRow(LabelledRow(Loc.Get("Uuid.FormatLabel"), _format));
        AddRow(LabelledRow(Loc.Get("Uuid.Count"), _count));

        var generate = new Button { Content = Loc.Get("Uuid.Generate"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        generate.Click += (_, _) => Generate();
        var copyAll = new CopyButton(Loc.Get("Uuid.CopyAll")) { GetText = () => _results.Text };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(generate);
        actions.Children.Add(copyAll);
        AddRow(actions);

        AddRow(LabelledRow(Loc.Get("Uuid.Results"), _results));

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
